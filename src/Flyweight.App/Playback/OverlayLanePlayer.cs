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
/// PROXY into its own buffer like <see cref="PlaybackEngine"/> does for the main track; PreviewCanvas then draws each
/// one into its box (<see cref="OverlayLayout"/>) over the main picture.
///
/// Driven from the engine's UI timer: <see cref="Update"/> loads overlays the playhead has reached, drops ones it
/// has left, and keeps each on the right frame: while playing by re-seeking one that has drifted, while paused by
/// seeking it to the playhead's frame (proxies are all-keyframe, so that's cheap). Play/pause is re-checked EVERY
/// tick, not just when it changes, so a player that was still opening at that moment can't miss it and keep running.
/// Stopping a player blocks, so it runs on the thread pool, and the callbacks never call player methods (rule 8).
///
/// Smoothness: decoder threads only flag new frames (the engine repaints once per tick); small overlays decode at
/// half size; the UI copies a frame out under the lock and draws it after, so drawing never stalls a decoder.
/// </summary>
public sealed class OverlayLanePlayer : IDisposable
{
    const int MaxLayers = 4;
    static readonly TimeSpan DriftLimit = TimeSpan.FromMilliseconds(300);
    static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(800);
    static readonly TimeSpan SeekThrottle = TimeSpan.FromMilliseconds(40);

    sealed class Layer
    {
        public required MediaPlayer Player;
        public required Guid SourceId;
        public required string Path;
        public required bool IsOriginal; // no proxy yet: the original, stretched to the buffer
        public required int W, H;
        public required IntPtr Decode, Display;
        public required Bitmap Frame;    // over Display; written by the decoder under FrameLock
        public required Bitmap Snap;     // UI-only copy that gets drawn
        public required MediaPlayer.LibVLCVideoLockCb LockCb;       // kept alive while native code holds them
        public required MediaPlayer.LibVLCVideoDisplayCb DisplayCb;
        public readonly object FrameLock = new();
        public volatile bool HasFrame;
        public volatile bool FreshFrame;  // a frame arrived since the last Snap copy
        public volatile bool PauseOnFirstFrame;
        public readonly Stopwatch SinceSeek = Stopwatch.StartNew();
        public readonly PlayerClock Clock = new();
        public long ShownMs = -1;         // source ms last asked for while paused
        public int Volume = -2;           // last values we set, so we don't query LibVLC every tick
        public bool? Muted;
        public bool HasSnap;
    }

    readonly LibVLC _vlc;
    readonly ProjectSession _session;
    readonly MediaLibrary _library;
    readonly Dictionary<Guid, Layer> _layers = new();
    volatile bool _playing;

    /// <summary>Raised on a LibVLC thread when an overlay has a new frame. Only set a flag here.</summary>
    public event EventHandler? FrameReady;

    public OverlayLanePlayer(LibVLC vlc, ProjectSession session, MediaLibrary library)
    {
        _vlc = vlc;
        _session = session;
        _library = library;
    }

    /// <summary>Decode size for an overlay: half size is plenty for a picture-in-picture, full size for big ones.</summary>
    static (int W, int H) DecodeSize(OverlayClip o) =>
        o.Scale * Math.Max(1, o.Zoom) <= 0.5 // zoomed in, a small box shows a magnified part: decode at full size
            ? (Canvas.ProxyWidth / 2, Canvas.ProxyHeight / 2) : (Canvas.ProxyWidth, Canvas.ProxyHeight);

    /// <summary>Call every tick with the timeline playhead.</summary>
    public void Update(TimeSpan position, bool playing)
    {
        _playing = playing;
        var p = _session.Project;
        var want = TimelineOps.OverlaysAt(p, position)
            .Where(o => p.FindMedia(o.SourceId) is { IsAudioOnly: false })
            .TakeLast(MaxLayers) // the topmost, if someone stacks more than we decode at once
            .ToDictionary(o => o.Id);

        foreach (var id in _layers.Keys.Where(id => !want.TryGetValue(id, out var o) || _layers[id].SourceId != o.SourceId ||
                                                   _layers[id].Path != PathFor(o.SourceId) ||
                                                   (_layers[id].W, _layers[id].H) != DecodeSize(o)).ToList())
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
            var state = player.State; // one native call per tick per overlay

            int volume = (int)Math.Round(o.EffectiveVolume * 100);
            if (layer.Volume != volume) { player.Volume = volume; layer.Volume = volume; }
            if (layer.Muted != !playing) { player.Mute = !playing; layer.Muted = !playing; } // silent while paused / scrubbing

            if (playing)
            {
                layer.ShownMs = -1;
                if (state == VLCState.Paused)
                {
                    player.SetPause(false);
                    layer.SinceSeek.Restart();
                    layer.Clock.Reset(player.Time);
                }
                else if (state is VLCState.Ended or VLCState.Stopped or VLCState.Error) Load(layer, expected, playing: true);
                else if (state == VLCState.Playing && layer.SinceSeek.Elapsed > SettleTime &&
                         layer.Clock.Estimate(player.Time) is { } at && Math.Abs(at - ms) > DriftLimit.TotalMilliseconds)
                {
                    PlaybackLog.Write($"overlay drift resync: ~{at} -> {ms} ms");
                    player.Time = ms;
                    layer.SinceSeek.Restart();
                    layer.Clock.Reset(ms);
                }
            }
            else
            {
                if (layer.PauseOnFirstFrame) continue; // still loading: its first frame pauses it
                if (state == VLCState.Playing) player.SetPause(true); // every tick until it really is paused
                // Paused: show exactly the playhead's frame. Seeking a paused player redraws it.
                if (ms != layer.ShownMs && layer.SinceSeek.Elapsed >= SeekThrottle)
                {
                    if (state is VLCState.Paused or VLCState.Playing)
                    {
                        player.Time = ms;
                        layer.ShownMs = ms;
                        layer.SinceSeek.Restart();
                    }
                    else if (state is VLCState.Ended or VLCState.Stopped or VLCState.Error)
                        Load(layer, expected, playing: false);
                    // Opening / buffering: try again next tick.
                }
            }
        }
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
        var (w, h) = DecodeSize(o);
        int pitch = w * 4;
        var player = new MediaPlayer(_vlc);
        var decode = Marshal.AllocHGlobal(pitch * h);
        var display = Marshal.AllocHGlobal(pitch * h);
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
            W = w, H = h, Decode = decode, Display = display,
            Frame = new Bitmap(w, h, pitch, PixelFormat.Format32bppRgb, display),
            Snap = new Bitmap(w, h, PixelFormat.Format32bppRgb),
            LockCb = lockCb, DisplayCb = displayCb,
        };
        player.SetVideoFormat("RV32", (uint)w, (uint)h, (uint)pitch);
        player.SetVideoCallbacks(lockCb, null, displayCb);
        PlaybackLog.Write($"overlay load {System.IO.Path.GetFileName(path)} ({w}×{h}) at {sourceTime.TotalMilliseconds:0} ms");
        Load(layer, sourceTime, playing);
        return layer;
    }

    void Load(Layer layer, TimeSpan sourceTime, bool playing)
    {
        using var media = new LibVLCSharp.Shared.Media(_vlc, layer.Path, FromType.FromPath, $":start-time={FfmpegRunner.Num(sourceTime.TotalSeconds)}");
        layer.PauseOnFirstFrame = !playing;
        layer.Player.Mute = !playing;
        layer.Muted = !playing;
        layer.Volume = -2; // the audio output can reset it on new media: set it again next tick
        layer.ShownMs = (long)sourceTime.TotalMilliseconds;
        layer.SinceSeek.Restart();
        layer.Clock.Reset(layer.ShownMs);
        layer.Player.Play(media);
    }

    // LibVLC thread: copy the frame out, never call player methods here (rule 8).
    unsafe void OnDisplay(Layer layer)
    {
        int bytes = layer.W * 4 * layer.H;
        lock (layer.FrameLock)
            Buffer.MemoryCopy((void*)layer.Decode, (void*)layer.Display, bytes, bytes);
        layer.HasFrame = true;
        layer.FreshFrame = true;
        if (layer.PauseOnFirstFrame)
        {
            var player = layer.Player;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { if (!_playing) player.SetPause(true); } catch { }
                layer.PauseOnFirstFrame = false; // only now may Update seek it (it is paused, or playing on purpose)
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
            if (_layers.TryGetValue(o.Id, out var layer) && layer.HasFrame && Snapshot(layer) is { } snap)
            {
                // The proxy is the media fitted into the buffer with bars; draw just the picture part.
                var full = new RectangleF(0, 0, layer.W, layer.H);
                var pic = layer.IsOriginal ? full : Fit(OverlayLayout.Aspect(media), full);
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.CompositingMode = CompositingMode.SourceCopy; // opaque video: no blending needed
                if (!OverlayLayout.IsZoomed(o))
                    g.DrawImage(snap, dest, pic, GraphicsUnit.Pixel);
                else
                {
                    // Zoom = crop: the same part of the picture the export crops (OverlayLayout.CropRect).
                    var (cx, cy, cw, ch) = OverlayLayout.CropRect(o);
                    var src = new RectangleF(pic.X + (float)(cx * pic.Width), pic.Y + (float)(cy * pic.Height),
                                             (float)(cw * pic.Width), (float)(ch * pic.Height));
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    using var attrs = new ImageAttributes();
                    attrs.SetWrapMode(WrapMode.TileFlipXY); // edge pixels repeat instead of fading to transparent
                    g.DrawImage(snap, Rectangle.Round(dest), src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
                    g.PixelOffsetMode = PixelOffsetMode.Default;
                }
                g.CompositingMode = CompositingMode.SourceOver;
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

    /// <summary>The latest frame as a UI-owned bitmap: a quick copy under the lock, so the decoder never waits for a draw.</summary>
    static unsafe Bitmap? Snapshot(Layer layer)
    {
        if (layer.FreshFrame || !layer.HasSnap)
        {
            var rect = new Rectangle(0, 0, layer.W, layer.H);
            var dst = layer.Snap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                int bytes = layer.W * 4 * layer.H;
                lock (layer.FrameLock)
                {
                    layer.FreshFrame = false;
                    if (dst.Stride == layer.W * 4) Buffer.MemoryCopy((void*)layer.Display, (void*)dst.Scan0, bytes, bytes);
                    else
                        for (int y = 0; y < layer.H; y++)
                            Buffer.MemoryCopy((byte*)layer.Display + y * layer.W * 4, (byte*)dst.Scan0 + y * dst.Stride, layer.W * 4, layer.W * 4);
                }
            }
            finally { layer.Snap.UnlockBits(dst); }
            layer.HasSnap = true;
        }
        return layer.Snap;
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
        layer.Snap.Dispose();
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
