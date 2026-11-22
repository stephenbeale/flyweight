using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Flyweight.Core.Editing;
using Flyweight.Core.Media;
using LibVLCSharp.Shared;

namespace Flyweight.App.Playback;

/// <summary>
/// Plays the timeline by playing each clip's PROXY with LibVLC.
///
/// We don't use LibVLC's VideoView control: it owns a native window that WinForms can't draw
/// on top of (the "airspace" problem). Instead LibVLC decodes into our memory buffer
/// (video callbacks) and <see cref="Controls.PreviewCanvas"/> paints that bitmap, then the
/// annotations over it.
///
/// Proxies are always 960×540 and all-keyframe, so seeking is fast and lands on the exact frame.
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    const int W = Canvas.ProxyWidth, H = Canvas.ProxyHeight, Pitch = W * 4;
    static readonly TimeSpan SeekThrottle = TimeSpan.FromMilliseconds(40);

    readonly ProjectSession _session;
    readonly MediaLibrary _library;
    readonly LibVLC _vlc;
    readonly MediaPlayer _player;
    readonly IntPtr _decodeBuffer;
    readonly IntPtr _displayBuffer;
    readonly System.Windows.Forms.Timer _timer;
    readonly AudioLanePlayer _lane;

    // Keep delegates in fields so the GC can't collect them while native code holds them.
    readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
    readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb;

    Guid? _loadedSourceId;
    string? _loadedPath;
    Guid? _clipId;
    volatile bool _playing;
    volatile bool _pauseOnFirstFrame;
    TimeSpan? _pendingSeek;
    readonly Stopwatch _sinceLastSeek = Stopwatch.StartNew();
    long _lastVlcTime = -1;
    readonly Stopwatch _sinceVlcTime = new();
    long _ignoreVlcTimeBelowMs;

    /// <summary>Lock this while reading <see cref="Frame"/>.</summary>
    public object FrameLock { get; } = new();

    /// <summary>The latest decoded proxy frame (960×540).</summary>
    public Bitmap Frame { get; }

    public bool HasFrame { get; private set; }

    /// <summary>True when the clip under the playhead has no proxy yet (still optimising).</summary>
    public bool WaitingForProxy { get; private set; }

    /// <summary>
    /// The proxy isn't ready yet, so the clip under the playhead is playing from its ORIGINAL file
    /// (scaled to 960×540 by LibVLC, so a non-16:9 original arrives stretched: see <see cref="PlayingMedia"/>).
    /// Seeking is slower than with a proxy, but you can watch and edit while it's optimising.
    /// </summary>
    public bool PlayingOriginal { get; private set; }

    /// <summary>The media under the playhead (null if none).</summary>
    public Core.Model.SourceMedia? PlayingMedia { get; private set; }

    /// <summary>Playhead position in TIMELINE time.</summary>
    public TimeSpan Position { get; private set; }

    public bool IsPlaying => _playing;

    /// <summary>Raised on a LibVLC thread when a new frame is ready. Marshal to the UI before painting.</summary>
    public event EventHandler? FrameReady;

    /// <summary>Raised on the UI thread whenever <see cref="Position"/> changes.</summary>
    public event EventHandler? PositionChanged;

    /// <summary>Raised on the UI thread when playback starts or stops (pausing doesn't move the playhead).</summary>
    public event EventHandler? PlayingChanged;

    public PlaybackEngine(ProjectSession session, MediaLibrary library)
    {
        _session = session;
        _library = library;
        // --aout=directsound: the default Windows output (mmdevice) sets volume/mute on the whole
        // app's audio session, so the video player and the audio-track players overwrote each
        // other's volume every tick (muting a clip garbled the music too). DirectSound sets it per player.
        _vlc = new LibVLC("--quiet", "--no-osd", "--no-video-title-show", "--no-snapshot-preview", "--aout=directsound");
        _player = new MediaPlayer(_vlc);

        _decodeBuffer = Marshal.AllocHGlobal(Pitch * H);
        _displayBuffer = Marshal.AllocHGlobal(Pitch * H);
        Frame = new Bitmap(W, H, Pitch, PixelFormat.Format32bppRgb, _displayBuffer);

        _lockCb = OnLock;
        _displayCb = OnDisplay;
        _player.SetVideoFormat("RV32", W, H, Pitch);
        _player.SetVideoCallbacks(_lockCb, null, _displayCb);

        _lane = new AudioLanePlayer(_vlc, session);

        _timer = new System.Windows.Forms.Timer { Interval = 15 };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    // ---- LibVLC callbacks (LibVLC threads: do NOT call player methods directly here) ----

    IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, _decodeBuffer);
        return IntPtr.Zero;
    }

    unsafe void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        long t0 = Stopwatch.GetTimestamp();
        lock (FrameLock)
        {
            var waited = Stopwatch.GetElapsedTime(t0);
            if (waited.TotalMilliseconds > 15) PlaybackLog.Write($"video thread waited {waited.TotalMilliseconds:0} ms for the screen paint");
            Buffer.MemoryCopy((void*)_decodeBuffer, (void*)_displayBuffer, Pitch * H, Pitch * H);
        }
        HasFrame = true;

        if (_pauseOnFirstFrame)
        {
            // We started playback only to get a frame on screen; freeze it there.
            _pauseOnFirstFrame = false;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (!_playing) _player.SetPause(true);
                _player.Mute = false;
            });
        }
        FrameReady?.Invoke(this, EventArgs.Empty);
    }

    // ---- Public controls (UI thread) ----

    /// <summary>Move the playhead. Rapid calls (scrubbing) are coalesced to avoid flooding LibVLC.</summary>
    public void Seek(TimeSpan timelineTime)
    {
        var total = TimelineOps.TotalDuration(_session.Project);
        if (timelineTime < TimeSpan.Zero) timelineTime = TimeSpan.Zero;
        if (timelineTime > total) timelineTime = total;
        Position = timelineTime;
        if (_playing) StartClock(timelineTime);
        PositionChanged?.Invoke(this, EventArgs.Empty);

        if (_sinceLastSeek.Elapsed >= SeekThrottle) ApplySeek(timelineTime);
        else _pendingSeek = timelineTime;
    }

    /// <summary>Re-show the frame at the current position (after an edit or a proxy finishing).</summary>
    public void Refresh() => ApplySeek(Position, force: true);

    public void TogglePlay()
    {
        if (_playing) Pause(); else Play();
    }

    public void Play()
    {
        PlaybackLog.Write($"play from {Position.TotalSeconds:0.000} s");
        var total = TimelineOps.TotalDuration(_session.Project);
        if (total <= TimeSpan.Zero) return;
        if (Position >= total - OneFrame()) Position = TimeSpan.Zero;
        _playing = true;
        StartClock(Position);
        _pauseOnFirstFrame = false;
        _player.Mute = false;
        if (_player.State == VLCState.Paused && _clipId is not null) _player.SetPause(false);
        else ApplySeek(Position, force: true);
        PlayingChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        if (_playing) PlaybackLog.Write($"pause at {Position.TotalSeconds:0.000} s");
        var was = _playing;
        _playing = false;
        _clock.Stop();
        if (_player.State == VLCState.Playing) _player.SetPause(true);
        _lane.StopAll();
        if (was) PlayingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Step whole frames (negative = backwards). Pauses playback.</summary>
    public void StepFrames(int frames)
    {
        Pause();
        Seek(Position + TimeSpan.FromTicks(OneFrame().Ticks * frames));
    }

    public TimeSpan OneFrame()
    {
        var fps = Locate(Position) is { } hit && _session.Project.FindMedia(hit.Clip.SourceId) is { Fps: > 0 } m ? m.Fps : 30;
        return TimeSpan.FromSeconds(1 / fps);
    }

    // ---- Internals ----

    TimelineHit? Locate(TimeSpan t)
    {
        var p = _session.Project;
        var total = TimelineOps.TotalDuration(p);
        if (total <= TimeSpan.Zero) return null;
        // At the very end, show the last frame rather than nothing.
        if (t >= total) t = total - TimeSpan.FromMilliseconds(1);
        return TimelineOps.Locate(p, t);
    }

    void ApplySeek(TimeSpan t, bool force = false)
    {
        _pendingSeek = null;
        _sinceLastSeek.Restart();

        if (Locate(t) is not { } hit)
        {
            StopPlayer();
            return;
        }

        _clipId = hit.Clip.Id;
        var state = _library.GetState(hit.Clip.SourceId);
        var media = _session.Project.FindMedia(hit.Clip.SourceId);
        PlayingMedia = media;
        // No proxy yet: play the original video rather than sit on a frozen picture. Stills keep
        // their stand-in (the photo itself), which their quick proxy replaces in a moment.
        var path = state.ProxyPath;
        PlayingOriginal = path is null && media is { IsStill: false, IsAudioOnly: false } && state.Status != ProxyStatus.Failed &&
                          File.Exists(media.OriginalPath);
        if (PlayingOriginal) path = media!.OriginalPath;
        WaitingForProxy = state.ProxyPath is null;
        if (path is null)
        {
            StopPlayer();
            return;
        }

        long ms = (long)hit.SourceTime.TotalMilliseconds;
        bool canSeekInPlace = _loadedPath == path &&
                              _player.State is VLCState.Playing or VLCState.Paused;

        if (canSeekInPlace)
        {
            PlaybackLog.Write($"seek in place to {ms} ms (playing={_playing})");
            _player.Time = ms;
            if (_playing && _player.State == VLCState.Paused) _player.SetPause(false);
            else if (!_playing && _player.State == VLCState.Playing) _player.SetPause(true);
        }
        else
        {
            PlaybackLog.Write($"load {(PlayingOriginal ? "ORIGINAL" : "proxy")} {Path.GetFileName(path)} at {hit.SourceTime.TotalMilliseconds:0} ms");
            Load(path, hit.SourceTime);
            _loadedSourceId = hit.Clip.SourceId;
            _loadedPath = path;
        }
        ResetClock(ms);
    }

    void Load(string proxyPath, TimeSpan sourceTime)
    {
        var secs = FfmpegRunner.Num(sourceTime.TotalSeconds);
        using var media = new LibVLCSharp.Shared.Media(_vlc, proxyPath, FromType.FromPath, $":start-time={secs}");
        _pauseOnFirstFrame = !_playing;
        _player.Mute = !_playing; // don't blip audio when we only want a still frame
        _ignoreVlcTimeBelowMs = (long)sourceTime.TotalMilliseconds - 500;
        _player.Play(media);
    }

    void StopPlayer()
    {
        _loadedSourceId = null;
        _loadedPath = null;
        _clipId = null;
        HasFrame = false;
        if (_player.State is not (VLCState.Stopped or VLCState.NothingSpecial)) _player.Stop();
        FrameReady?.Invoke(this, EventArgs.Empty);
    }

    void ResetClock(long ms)
    {
        _lastVlcTime = ms;
        _sinceVlcTime.Restart();
    }

    /// <summary>
    /// Play the clip at its own volume (LibVLC: 100 = as recorded, up to 200). Checked every tick
    /// rather than once per load: the audio output can reset the level when new media starts, and
    /// this also picks up slider changes live and split halves that keep playing seamlessly.
    /// </summary>
    /// <summary>
    /// Play the clip at its speed (LibVLC keeps the pitch). Checked every tick like the volume, so a new
    /// clip, a split half or a change in the panel takes effect straight away.
    /// </summary>
    void ApplyRate(Core.Model.Clip clip)
    {
        float target = (float)(double.IsFinite(clip.Speed) && clip.Speed > 0 ? clip.Speed : 1);
        if (Math.Abs(_player.Rate - target) > 0.001f)
        {
            PlaybackLog.Write($"rate {_player.Rate:0.##} -> {target:0.##}");
            _player.SetRate(target);
        }
    }

    void ApplyVolume(Core.Model.Clip clip)
    {
        int target = (int)Math.Round(clip.EffectiveVolume * 100);
        int now = _player.Volume;
        if (now >= 0 && now != target)
        {
            PlaybackLog.Write($"video volume {now} -> {target}");
            _player.Volume = target; // -1 = no audio output yet
        }
    }

    void Tick()
    {
        if (_pendingSeek is { } pending && _sinceLastSeek.Elapsed >= SeekThrottle)
            ApplySeek(pending);

        _lane.Update(Position, _playing);
        if (!_playing) return;

        // The playhead runs on a REAL-TIME clock, and the video follows it (D23). It used to follow the
        // video, which stalls for a few hundred ms at every cut while the next proxy loads: the
        // timeline then ran slower than real time, so the audio track (which can't stall) drifted
        // ahead and kept being pulled back, audibly repeating bits of music.
        var p = _session.Project;
        var total = TimelineOps.TotalDuration(p);
        var t = _clockBase + _clock.Elapsed;
        if (t >= total)
        {
            Pause();
            Position = total;
            PositionChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        Position = t;

        if (Locate(t) is { } hit)
        {
            if (hit.Clip.Id != _clipId)
            {
                var prev = _clipId is { } id ? p.FindClip(id) : null;
                bool seamless = prev is not null && prev.SourceId == hit.Clip.SourceId &&
                                Math.Abs((hit.Clip.In - prev.Out).TotalMilliseconds) < 50 && prev.Speed == hit.Clip.Speed &&
                                _loadedSourceId == hit.Clip.SourceId && _player.State == VLCState.Playing;
                if (seamless) _clipId = hit.Clip.Id; // adjacent halves of a split: keep playing
                else ApplySeek(t, force: true);       // a cut: load the next clip where the clock is NOW
            }
            else if (!WaitingForProxy || PlayingOriginal)
            {
                KeepVideoInStep(hit);
            }
            ApplyVolume(hit.Clip);
            ApplyRate(hit.Clip);
        }
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Real-time playback clock ----

    readonly Stopwatch _clock = new();
    TimeSpan _clockBase;

    void StartClock(TimeSpan at)
    {
        _clockBase = at;
        _clock.Restart();
    }

    static readonly TimeSpan VideoSettle = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan VideoDriftLimit = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// If the video has fallen behind (or run ahead of) the clock, e.g. while the clip was still
    /// loading after a cut, seek it to where the clock is. Proxies are all-keyframe, so this is a
    /// cheap jump of a few frames. LibVLC's Time only updates every few hundred ms, so estimate the
    /// video's real position by interpolating since its last update, and let a load/seek settle first.
    /// </summary>
    void KeepVideoInStep(TimelineHit hit)
    {
        if (_player.State != VLCState.Playing || _sinceLastSeek.Elapsed < VideoSettle) return;
        long vt = _player.Time;
        if (vt >= _ignoreVlcTimeBelowMs && vt != _lastVlcTime) ResetClock(vt);
        if (_lastVlcTime < 0) return;
        var extra = _sinceVlcTime.Elapsed;
        if (extra > TimeSpan.FromMilliseconds(500)) extra = TimeSpan.FromMilliseconds(500);
        // The video advances through its footage at the clip's speed.
        var video = TimeSpan.FromMilliseconds(_lastVlcTime) + hit.Clip.SourceSpan(extra);
        if ((video - hit.SourceTime).Duration() <= VideoDriftLimit) return;

        long ms = (long)hit.SourceTime.TotalMilliseconds;
        PlaybackLog.Write($"drift resync: video ~{video.TotalMilliseconds:0} ms, clock {ms} ms");
        _player.Time = ms;
        _sinceLastSeek.Restart();
        ResetClock(ms);
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _lane.Dispose();
        _player.Stop();
        _player.Dispose();
        _vlc.Dispose();
        Frame.Dispose();
        Marshal.FreeHGlobal(_decodeBuffer);
        Marshal.FreeHGlobal(_displayBuffer);
    }
}
