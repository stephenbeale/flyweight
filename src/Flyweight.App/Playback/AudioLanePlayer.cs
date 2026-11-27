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
/// Driven from <see cref="PlaybackEngine"/>'s UI timer: <see cref="Update"/> starts items just BEFORE the
/// playhead reaches them (pre-roll, see <see cref="_startLead"/>), stops ones it has left, follows volume changes, and re-seeks an item that
/// has drifted from the playhead (e.g. after the user seeks, or edits the item, while playing).
/// Stopping a LibVLC player blocks, so stops run on the thread pool (rule 8: never from a callback).
/// </summary>
sealed class AudioLanePlayer : IDisposable
{
    static readonly TimeSpan DriftLimit = TimeSpan.FromMilliseconds(350);
    static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(800); // LibVLC's Time is coarse just after start/seek

    /// <summary>
    /// How long a new LibVLC player takes before its sound actually comes out (~410–450 ms measured in playback.log).
    /// Items are started this far AHEAD of their timeline start (pre-roll), so their sound lands on time. Without it,
    /// every join between audio items (e.g. separated clip sound slid together) came in late, then the drift check
    /// jumped it forward: a gap and a skip at each join (owner: "audio is getting chopped"). Learned from each new
    /// voice's first drift reading.
    /// </summary>
    TimeSpan _startLead = TimeSpan.FromMilliseconds(420);
    static readonly TimeSpan MaxStartLead = TimeSpan.FromMilliseconds(1000);

    sealed class Voice
    {
        public required MediaPlayer Player;
        public required Guid SourceId;
        public readonly Stopwatch SinceSeek = Stopwatch.StartNew();
        public readonly PlayerClock Clock = new();
        public int Volume = -2; // last value we set, so we don't query the audio output every tick
        public bool LeadLearned; // its first drift reading has tuned _startLead
    }

    readonly LibVLC _vlc;
    readonly ProjectSession _session;
    readonly Dictionary<Guid, Voice> _voices = [];

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
        // Items playing now, plus ones starting within the pre-roll (their player needs that long to get going).
        var lead = _startLead;
        var want = p.AudioTrack
            .Where(a => a.Start - lead <= position && position < a.End)
            .Where(a => a.EffectiveVolume > 0 && a.End - position > TimeSpan.FromMilliseconds(60))
            .ToDictionary(a => a.Id);

        foreach (var id in _voices.Keys.Where(id => !want.ContainsKey(id) || _voices[id].SourceId != want[id].SourceId).ToList())
            Stop(id);

        foreach (var item in want.Values)
        {
            var expected = item.In + (position - item.Start); // before the item's start (pre-roll) this is < In
            if (!_voices.TryGetValue(item.Id, out var v))
            {
                if (p.FindMedia(item.SourceId) is not { } media || !File.Exists(media.OriginalPath)) continue;
                // Pre-roll: start at In, ~lead early, so the sound comes out right at the item's start. Already inside
                // the item (play pressed mid-item, a seek): start `lead` further on, so it's in step once it's audible.
                var last = item.Out - TimeSpan.FromMilliseconds(50);
                var from = expected < item.In ? item.In : expected + lead < last ? expected + lead : last;
                _voices[item.Id] = Start(media.OriginalPath, item, from);
                continue;
            }

            var player = v.Player;
            int target = (int)Math.Round(item.EffectiveVolume * 100);
            if (v.Volume != target && player.Volume >= 0) // -1 = no audio output yet: try again next tick
            {
                PlaybackLog.Write($"lane volume -> {target}");
                player.Volume = target;
                v.Volume = target;
            }

            // Compare an ESTIMATE of where it is (PlayerClock), not LibVLC's coarse Time: the raw value always
            // looked a few hundred ms behind, and re-seeking for it every second made the music stutter.
            if (position < item.Start) continue; // still pre-rolling: nothing to compare yet
            if (v.SinceSeek.Elapsed > SettleTime && player.State == VLCState.Playing &&
                v.Clock.Estimate(player.Time) is { } at)
            {
                var behind = expected.TotalMilliseconds - at; // + = the sound is late
                if (!v.LeadLearned)
                {
                    // Tune the pre-roll from this voice's first reading (half-step, so one noisy reading can't swing it).
                    v.LeadLearned = true;
                    var tuned = _startLead + TimeSpan.FromMilliseconds(behind / 2);
                    _startLead = tuned < TimeSpan.Zero ? TimeSpan.Zero : tuned > MaxStartLead ? MaxStartLead : tuned;
                    PlaybackLog.Write($"lane start lead {_startLead.TotalMilliseconds:0} ms (first reading {behind:+0;-0} ms)");
                }
                if (Math.Abs(behind) <= DriftLimit.TotalMilliseconds) continue;
                PlaybackLog.Write($"lane drift resync: ~{at} -> {expected.TotalMilliseconds:0} ms");
                player.Time = (long)expected.TotalMilliseconds;
                v.SinceSeek.Restart();
                v.Clock.Reset((long)expected.TotalMilliseconds);
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
        var voice = new Voice { Player = player, SourceId = item.SourceId };
        voice.Clock.Reset((long)sourceTime.TotalMilliseconds);
        return voice;
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
