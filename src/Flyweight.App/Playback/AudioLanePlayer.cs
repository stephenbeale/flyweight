using System.Diagnostics;
using Flyweight.Core.Editing;
using Flyweight.Core.Media;
using Flyweight.Core.Model;
using LibVLCSharp.Shared;

namespace Flyweight.App.Playback;

/// <summary>
/// Plays the audio track (music / narration) alongside the video during playback. One LibVLC
/// player per item currently sounding, so overlapping items mix and a music bed carries on
/// smoothly across video cuts (the video player reloads at cuts; these don't).
///
/// Driven from <see cref="PlaybackEngine"/>'s UI timer: <see cref="Update"/> starts items that the
/// playhead has reached, stops ones it has left, follows volume changes, and re-seeks an item that
/// has drifted from the playhead (e.g. after the user seeks, or edits the item, while playing).
/// Stopping a LibVLC player blocks, so stops run on the thread pool (rule 8: never from a callback).
/// </summary>
sealed class AudioLanePlayer : IDisposable
{
    static readonly TimeSpan DriftLimit = TimeSpan.FromMilliseconds(350);
    static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(800); // LibVLC's Time is coarse just after start/seek

    sealed class Voice
    {
        public required MediaPlayer Player;
        public required Guid SourceId;
        public readonly Stopwatch SinceSeek = Stopwatch.StartNew();
    }

    readonly LibVLC _vlc;
    readonly ProjectSession _session;
    readonly Dictionary<Guid, Voice> _voices = new();

    public AudioLanePlayer(LibVLC vlc, ProjectSession session)
    {
        _vlc = vlc;
        _session = session;
    }

    /// <summary>Call often (every tick). <paramref name="position"/> is the timeline playhead.</summary>
    public void Update(TimeSpan position, bool playing)
    {
        if (!playing) { StopAll(); return; }
        var p = _session.Project;
        var want = TimelineOps.AudioAt(p, position)
            .Where(a => a.EffectiveVolume > 0 && a.End - position > TimeSpan.FromMilliseconds(60))
            .ToDictionary(a => a.Id);

        foreach (var id in _voices.Keys.Where(id => !want.ContainsKey(id) || _voices[id].SourceId != want[id].SourceId).ToList())
            Stop(id);

        foreach (var item in want.Values)
        {
            var expected = item.In + (position - item.Start);
            if (!_voices.TryGetValue(item.Id, out var v))
            {
                if (p.FindMedia(item.SourceId) is not { } media || !File.Exists(media.OriginalPath)) continue;
                _voices[item.Id] = Start(media.OriginalPath, item, expected);
                continue;
            }

            var player = v.Player;
            int target = (int)Math.Round(item.EffectiveVolume * 100);
            int now = player.Volume;
            if (now >= 0 && now != target) player.Volume = target;

            if (v.SinceSeek.Elapsed > SettleTime && player.State == VLCState.Playing)
            {
                var at = TimeSpan.FromMilliseconds(player.Time);
                if ((at - expected).Duration() > DriftLimit)
                {
                    player.Time = (long)expected.TotalMilliseconds;
                    v.SinceSeek.Restart();
                }
            }
        }
    }

    Voice Start(string path, AudioItem item, TimeSpan sourceTime)
    {
        var player = new MediaPlayer(_vlc);
        // :no-video so cover art in an MP3 doesn't open a window.
        using var media = new LibVLCSharp.Shared.Media(_vlc, path, FromType.FromPath,
            $":start-time={FfmpegRunner.Num(sourceTime.TotalSeconds)}", ":no-video");
        player.Play(media);
        player.Volume = (int)Math.Round(item.EffectiveVolume * 100);
        return new Voice { Player = player, SourceId = item.SourceId };
    }

    void Stop(Guid id)
    {
        if (!_voices.Remove(id, out var v)) return;
        var player = v.Player;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { player.Stop(); } catch { }
            player.Dispose();
        });
    }

    public void StopAll()
    {
        foreach (var id in _voices.Keys.ToList()) Stop(id);
    }

    public void Dispose()
    {
        foreach (var v in _voices.Values)
        {
            try { v.Player.Stop(); } catch { }
            v.Player.Dispose();
        }
        _voices.Clear();
    }
}
