using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Flyweight.Core.Editing;
using Flyweight.Core.Media;
using Flyweight.Core.Model;
using LibVLCSharp.Shared;

namespace Flyweight.App.Playback;

/// <summary>
/// Plays the overlay tracks (V2, V3…) in the preview. One LibVLC player per overlay on screen, each decoding its
/// PROXY into its own 960×540 buffer like <see cref="PlaybackEngine"/> does for the main track; PreviewCanvas then
/// draws each one into its box (<see cref="OverlayLayout"/>) over the main picture.
///
/// Driven from the engine's UI timer: <see cref="Update"/> loads overlays the playhead has reached, drops ones it
/// has left, and keeps each on the right frame: while playing by re-seeking one that has drifted, while paused by
/// seeking it to the playhead's frame (proxies are all-keyframe, so that's cheap). Stopping a player blocks, so it
/// runs on the thread pool, and the callbacks never call player methods (rule 8).
/// </summary>
public sealed class OverlayLanePlayer : IDisposable
{
    const int W = Canvas.ProxyWidth, H = Canvas.ProxyHeight, Pitch = W * 4;
    const int MaxLayers = 4;
    static readonly TimeSpan DriftLimit = TimeSpan.FromMilliseconds(250);
    static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(600);
    static readonly TimeSpan SeekThrottle = TimeSpan.FromMilliseconds(40);

    sealed class Layer
    {
        public required MediaPlayer Player;
        public required Guid SourceId;
        public required string Path;
        public required bool IsOriginal; // no proxy yet: the original, stretched to 960×540
        public required IntPtr Decode, Display;
        public required Bitmap Frame;
        public required MediaPlayer.LibVLCVideoLockCb LockCb;       // kept alive while native code holds them
        public required MediaPlayer.LibVLCVideoDisplayCb DisplayCb;
        public readonly object FrameLock = new();
        public volatile bool HasFrame;
        public volatile bool PauseOnFirstFrame;
        public readonly Stopwatch SinceSeek = Stopwatch.StartNew();
        public long ShownMs = -1; // source ms last asked for while paused
    }

    readonly LibVLC _vlc;
    readonly ProjectSession _session;
    readonly MediaLibrary _library;
    readonly Dictionary<Guid, Layer> _layers = new();
    bool _wasPlaying;

    /// <summary>Raised on a LibVLC thread when an overlay has a new frame. Marshal to the UI before painting.</summary>
    public event EventHandler? FrameReady;

    public OverlayLanePlayer(LibVLC vlc, ProjectSession session, MediaLibrary library)
    {
        _vlc = vlc;
        _session = session;
        _library = library;
    }

    /// <summary>Call every tick with the timeline playhead.</summary>
    public void Update(TimeSpan position, bool playing)
    {
        var p = _session.Project;
        var want = TimelineOps.OverlaysAt(p, position)
            .Where(o => p.FindMedia(o.SourceId) is { IsAudioOnly: false })
            .TakeLast(MaxLayers) // the topmost, if someone stacks more than we decode at once
            .ToDictionary(o => o.Id);

        foreach (var id in _layers.Keys.Where(id => !want.ContainsKey(id) || _layers[id].SourceId != want[id].SourceId ||
                                                   _layers[id].Path != PathFor(want[id].SourceId)).ToList())
            Stop(id);

        foreach (var o in want.Values)
        {
            var expected = o.In + (position - o.Start);
            long ms = (long)expected.TotalMilliseconds;
            if (!_layers.TryGetValue(o.Id, out var layer))
            {
                if (PathFor(o.SourceId) is not { } path) continue;
                _layers[o.Id] = Start(o, path, expected, playing);
                continue;
            }
            var player = layer.Player;

            int volume = (int)Math.Round(o.EffectiveVolume * 100);
            if (player.Volume >= 0 && player.Volume != volume) player.Volume = volume;
            if (player.Mute == playing) player.Mute = !playing; // silent while paused / scrubbing

            if (playing)
            {
                if (player.State == VLCState.Paused) { player.SetPause(false); layer.SinceSeek.Restart(); }
                else if (player.State == VLCState.Ended) { Restart(layer, expected); }
                else if (layer.SinceSeek.Elapsed > SettleTime && player.State == VLCState.Playing &&
                         (TimeSpan.FromMilliseconds(player.Time) - expected).Duration() > DriftLimit)
                {
                    PlaybackLog.Write($"overlay drift resync: {player.Time} -> {ms} ms");
                    player.Time = ms;
                    layer.SinceSeek.Restart();
                }
                layer.ShownMs = -1;
            }
            else
            {
                if (_wasPlaying && player.State == VLCState.Playing) player.SetPause(true);
                // Paused: show exactly the playhead's frame. Seeking a paused player redraws it.
                if (ms != layer.ShownMs && layer.SinceSeek.Elapsed >= SeekThrottle && !layer.PauseOnFirstFrame)
                {
                    if (player.State is VLCState.Paused or VLCState.Playing)
                    {
                        player.Time = ms;
                        layer.ShownMs = ms;
                        layer.SinceSeek.Restart();
                    }
                    else if (player.State is VLCState.Ended or VLCState.Stopped or VLCState.Error)
                        Restart(layer, expected);
                    // Opening / buffering: try again next tick.
                }
            }
        }
        _wasPlaying = playing;
    }

    /// <summary>The file to play for this media: its proxy, else (still optimising) the original video. Null = nothing yet.</summary>
    string? PathFor(Guid sourceId)
    {
        var st = _library.GetState(sourceId);
        if (st.ProxyPath is { } proxy) return proxy;
        var m = _session.Project.FindMedia(sourceId);
        return m is { IsStill: false, IsAudioOnly: false } && st.Status != ProxyStatus.Failed && File.Exists(m.OriginalPath) ? m.OriginalPath : null;
    }

    Layer Start(OverlayClip o, string path, TimeSpan sourceTime, bool playing)
    {
        var player = new MediaPlayer(_vlc);
        var decode = Marshal.AllocHGlobal(Pitch * H);
        var display = Marshal.AllocHGlobal(Pitch * H);
        Layer layer = null!;
        MediaPlayer.LibVLCVideoLockCb lockCb = (_, planes) =>
        {
            Marshal.WriteIntPtr(planes, decode);
            return IntPtr.Zero;
        };
        MediaPlayer.LibVLCVideoDisplayCb displayCb = (_, _) => OnDisplay(layer);
        layer = new Layer
        {
            Player = player, SourceId = o.SourceId, Path = path, IsOriginal = _library.GetState(o.SourceId).ProxyPath is null,
            Decode = decode, Display = display, Frame = new Bitmap(W, H, Pitch, PixelFormat.Format32bppRgb, display),
            LockCb = lockCb, DisplayCb = displayCb,
        };
        player.SetVideoFormat("RV32", W, H, Pitch);
        player.SetVideoCallbacks(lockCb, null, displayCb);
        PlaybackLog.Write($"overlay load {System.IO.Path.GetFileName(path)} at {sourceTime.TotalMilliseconds:0} ms");
        Load(layer, sourceTime, playing);
        player.Volume = (int)Math.Round(o.EffectiveVolume * 100);
        return layer;
    }

    void Load(Layer layer, TimeSpan sourceTime, bool playing)
    {
        using var media = new LibVLCSharp.Shared.Media(_vlc, layer.Path, FromType.FromPath, $":start-time={FfmpegRunner.Num(sourceTime.TotalSeconds)}");
        layer.PauseOnFirstFrame = !playing;
        layer.Player.Mute = !playing;
        layer.ShownMs = (long)sourceTime.TotalMilliseconds;
        layer.SinceSeek.Restart();
        layer.Player.Play(media);
    }

    /// <summary>Reached the end of its file (e.g. a still's proxy, or scrubbing back after it ended): load it again.</summary>
    void Restart(Layer layer, TimeSpan sourceTime) => Load(layer, sourceTime, _wasPlaying);

    // LibVLC thread: copy the frame out, never call player methods here (rule 8).
    unsafe void OnDisplay(Layer layer)
    {
        lock (layer.FrameLock)
            Buffer.MemoryCopy((void*)layer.Decode, (void*)layer.Display, Pitch * H, Pitch * H);
        layer.HasFrame = true;
        if (layer.PauseOnFirstFrame)
        {
            layer.PauseOnFirstFrame = false;
            var player = layer.Player;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { if (!_wasPlaying) player.SetPause(true); } catch { }
            });
        }
        FrameReady?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Draw the overlays showing at <paramref name="position"/> into the preview's 16:9 <paramref name="canvas"/>,
    /// bottom track first. One still optimising shows as a dark box so you can still place it. UI thread.
    /// </summary>
    public void Draw(Graphics g, RectangleF canvas, Project p, TimeSpan position, Guid? selectedId, Font font)
    {
        foreach (var o in TimelineOps.OverlaysAt(p, position))
        {
            if (p.FindMedia(o.SourceId) is not { IsAudioOnly: false } media) continue;
            var dest = Dest(canvas, o, media);
            if (_layers.TryGetValue(o.Id, out var layer) && layer.HasFrame)
            {
                // The proxy is the media fitted into 960×540 with bars; draw just the picture part.
                var src = layer.IsOriginal ? new RectangleF(0, 0, W, H) : Fit(OverlayLayout.Aspect(media), new RectangleF(0, 0, W, H));
                g.InterpolationMode = InterpolationMode.Bilinear;
                lock (layer.FrameLock)
                    g.DrawImage(layer.Frame, dest, src, GraphicsUnit.Pixel);
            }
            else
            {
                using var b = new SolidBrush(Color.FromArgb(200, 30, 30, 36));
                g.FillRectangle(b, dest);
                TextRenderer.DrawText(g, "Optimising…", font, Rectangle.Round(dest), Color.Gray,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            if (o.Id == selectedId)
            {
                using var pen = new Pen(Color.FromArgb(0, 170, 255), 2f) { DashStyle = DashStyle.Dash };
                g.DrawRectangle(pen, dest.X, dest.Y, dest.Width, dest.Height);
            }
        }
    }

    /// <summary>Where an overlay lands in the preview canvas (screen pixels).</summary>
    public static RectangleF Dest(RectangleF canvas, OverlayClip o, SourceMedia? media)
    {
        var (x, y, w, h) = OverlayLayout.Rect(o, media);
        return new RectangleF(canvas.X + (float)(x * canvas.Width), canvas.Y + (float)(y * canvas.Height),
            (float)(w * canvas.Width), (float)(h * canvas.Height));
    }

    static RectangleF Fit(double aspect, RectangleF box)
    {
        float w = box.Width, h = (float)(w / aspect);
        if (h > box.Height) { h = box.Height; w = (float)(h * aspect); }
        return new RectangleF(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
    }

    void Stop(Guid id)
    {
        if (!_layers.Remove(id, out var layer)) return;
        ThreadPool.QueueUserWorkItem(_ => Release(layer));
    }

    static void Release(Layer layer)
    {
        try { layer.Player.Stop(); } catch { }
        layer.Player.Dispose();
        lock (layer.FrameLock) layer.Frame.Dispose();
        Marshal.FreeHGlobal(layer.Decode);
        Marshal.FreeHGlobal(layer.Display);
    }

    public void StopAll()
    {
        foreach (var id in _layers.Keys.ToList()) Stop(id);
    }

    public void Dispose()
    {
        foreach (var layer in _layers.Values) Release(layer);
        _layers.Clear();
    }
}
