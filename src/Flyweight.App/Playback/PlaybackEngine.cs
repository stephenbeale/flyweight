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

    // Keep delegates in fields so the GC can't collect them while native code holds them.
    readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
    readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb;

    Guid? _loadedSourceId;
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

    /// <summary>Playhead position in TIMELINE time.</summary>
    public TimeSpan Position { get; private set; }

    public bool IsPlaying => _playing;

    /// <summary>Raised on a LibVLC thread when a new frame is ready. Marshal to the UI before painting.</summary>
    public event EventHandler? FrameReady;

    /// <summary>Raised on the UI thread whenever <see cref="Position"/> changes.</summary>
    public event EventHandler? PositionChanged;

    public PlaybackEngine(ProjectSession session, MediaLibrary library)
    {
        _session = session;
        _library = library;
        _vlc = new LibVLC("--quiet", "--no-osd", "--no-video-title-show", "--no-snapshot-preview");
        _player = new MediaPlayer(_vlc);

        _decodeBuffer = Marshal.AllocHGlobal(Pitch * H);
        _displayBuffer = Marshal.AllocHGlobal(Pitch * H);
        Frame = new Bitmap(W, H, Pitch, PixelFormat.Format32bppRgb, _displayBuffer);

        _lockCb = OnLock;
        _displayCb = OnDisplay;
        _player.SetVideoFormat("RV32", W, H, Pitch);
        _player.SetVideoCallbacks(_lockCb, null, _displayCb);

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
        lock (FrameLock)
            Buffer.MemoryCopy((void*)_decodeBuffer, (void*)_displayBuffer, Pitch * H, Pitch * H);
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
        var total = TimelineOps.TotalDuration(_session.Project);
        if (total <= TimeSpan.Zero) return;
        if (Position >= total - OneFrame()) Position = TimeSpan.Zero;
        _playing = true;
        _pauseOnFirstFrame = false;
        _player.Mute = false;
        if (_player.State == VLCState.Paused && _clipId is not null) _player.SetPause(false);
        else ApplySeek(Position, force: true);
    }

    public void Pause()
    {
        _playing = false;
        if (_player.State == VLCState.Playing) _player.SetPause(true);
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
        WaitingForProxy = state.ProxyPath is null;
        if (WaitingForProxy)
        {
            StopPlayer();
            return;
        }

        long ms = (long)hit.SourceTime.TotalMilliseconds;
        bool canSeekInPlace = _loadedSourceId == hit.Clip.SourceId &&
                              _player.State is VLCState.Playing or VLCState.Paused;

        if (canSeekInPlace)
        {
            _player.Time = ms;
            if (_playing && _player.State == VLCState.Paused) _player.SetPause(false);
            else if (!_playing && _player.State == VLCState.Playing) _player.SetPause(true);
        }
        else
        {
            Load(state.ProxyPath!, hit.SourceTime);
            _loadedSourceId = hit.Clip.SourceId;
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

    void Tick()
    {
        if (_pendingSeek is { } pending && _sinceLastSeek.Elapsed >= SeekThrottle)
            ApplySeek(pending);

        if (!_playing) return;

        var p = _session.Project;
        var clip = _clipId is { } id ? p.FindClip(id) : null;
        if (clip is null)
        {
            // Clip edited away under us, or waiting for a proxy: re-resolve.
            if (!WaitingForProxy) ApplySeek(Position, force: true);
            return;
        }

        // LibVLC's Time only updates every few hundred ms; interpolate between updates for a smooth playhead.
        TimeSpan src;
        if (_player.State == VLCState.Ended)
        {
            src = clip.Out;
        }
        else
        {
            long vt = _player.Time;
            if (vt >= _ignoreVlcTimeBelowMs && vt != _lastVlcTime) ResetClock(vt);
            var extra = _player.State == VLCState.Playing ? _sinceVlcTime.Elapsed : TimeSpan.Zero;
            if (extra > TimeSpan.FromMilliseconds(500)) extra = TimeSpan.FromMilliseconds(500);
            src = TimeSpan.FromMilliseconds(_lastVlcTime) + extra;
        }

        int idx = p.Timeline.IndexOf(clip);
        var clipStart = TimelineOps.ClipStart(p, idx);

        if (src >= clip.Out)
        {
            var nextStart = clipStart + clip.Duration;
            if (idx + 1 >= p.Timeline.Count)
            {
                Pause();
                Position = nextStart;
                PositionChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            var next = p.Timeline[idx + 1];
            if (next.SourceId == clip.SourceId && Math.Abs((next.In - clip.Out).TotalMilliseconds) < 50 && _player.State != VLCState.Ended)
            {
                _clipId = next.Id; // adjacent halves of a split: keep playing seamlessly
                Position = nextStart + (src - next.In);
            }
            else
            {
                ApplySeek(nextStart, force: true);
                Position = nextStart;
            }
        }
        else
        {
            var into = src - clip.In;
            Position = clipStart + (into < TimeSpan.Zero ? TimeSpan.Zero : into);
        }
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _player.Stop();
        _player.Dispose();
        _vlc.Dispose();
        Frame.Dispose();
        Marshal.FreeHGlobal(_decodeBuffer);
        Marshal.FreeHGlobal(_displayBuffer);
    }
}
