using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Flyweight.App.Playback;
using Flyweight.App.Rendering;
using Flyweight.Core.Editing;
using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.App.Controls;

public enum Tool { Select, Arrow, Box, Text }

/// <summary>
/// Shows the current frame letterboxed to 16:9, draws annotations on top, and lets you
/// create, select, move and resize them with the mouse.
/// </summary>
public sealed class PreviewCanvas : Control
{
    const float HandleSize = 9;

    ProjectSession _session = null!;
    PlaybackEngine _engine = null!;
    MediaLibrary _library = null!;
    Tool _tool = Tool.Select;

    // Short on-screen notice (e.g. why F did nothing), cleared by a timer.
    string? _notice;
    readonly System.Windows.Forms.Timer _noticeTimer = new() { Interval = 3000 };

    // Stand-in picture while a clip's proxy is still being made (one cached at a time).
    string? _standInPath;
    Image? _standIn;

    // Drag state
    enum DragMode { None, Create, Move, Handle1, Handle2, Corner12, Corner21, MoveOverlay }
    DragMode _drag;
    (Guid Id, double X, double Y) _overlayOrigin;
    PointF _dragStartNorm;
    Annotation? _dragOrigin;      // copy of the annotation before the drag
    Annotation? _creating;        // ghost shown while drawing a new shape

    TextBox? _editor;
    Guid? _editingId;

    public PreviewCanvas()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.FromArgb(18, 18, 20);
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); _notice = null; Invalidate(); };
    }

    /// <summary>Show a message over the top of the preview for a few seconds.</summary>
    public void ShowNotice(string message)
    {
        _notice = message;
        _noticeTimer.Stop();
        _noticeTimer.Start();
        Invalidate();
    }

    public Guid? SelectedAnnotationId { get; private set; }

    /// <summary>The overlay selected on the timeline (MainForm keeps it in step): drawn with a dashed outline.</summary>
    public Guid? SelectedOverlayId { get; set; }

    /// <summary>An overlay was clicked in the picture (null = clicked off every overlay and annotation).</summary>
    public event Action<Guid?>? OverlayClicked;

    public event EventHandler? SelectionChanged;
    public event EventHandler? ToolChanged;
    public bool IsEditingText => _editor is not null;

    public Tool Tool
    {
        get => _tool;
        set { _tool = value; Cursor = value == Tool.Select ? Cursors.Default : Cursors.Cross; ToolChanged?.Invoke(this, EventArgs.Empty); }
    }

    public void Bind(ProjectSession session, PlaybackEngine engine, MediaLibrary library)
    {
        _session = session;
        _engine = engine;
        _library = library;
        _engine.FrameReady += (_, _) => { if (IsHandleCreated) BeginInvoke(Invalidate); };
        _engine.PositionChanged += (_, _) => Invalidate();
    }

    public void Select(Guid? annotationId)
    {
        if (SelectedAnnotationId == annotationId) return;
        CommitTextEdit();
        SelectedAnnotationId = annotationId;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    /// <summary>The 16:9 rectangle the video occupies inside the control.</summary>
    public RectangleF CanvasRect
    {
        get
        {
            float w = ClientSize.Width, h = ClientSize.Height;
            float cw = Math.Min(w, h * 16f / 9f), ch = cw * 9f / 16f;
            return new RectangleF((w - cw) / 2, (h - ch) / 2, cw, ch);
        }
    }

    PointF ToNorm(Point p)
    {
        var c = CanvasRect;
        return new PointF(Math.Clamp((p.X - c.X) / c.Width, 0, 1), Math.Clamp((p.Y - c.Y) / c.Height, 0, 1));
    }

    // ---------------- Painting ----------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_session is null) return;
        var canvas = CanvasRect;

        using (var b = new SolidBrush(Color.Black)) g.FillRectangle(b, canvas);

        if (_engine.HasFrame)
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            var look = TimelineOps.Locate(_session.Project, _engine.Position)?.Clip;
            lock (_engine.FrameLock)
            {
                Bitmap frame = _engine.Frame;
                if (_engine.PlayingOriginal && _engine.PlayingMedia is { Width: > 0, Height: > 0 } pm &&
                    Math.Abs(pm.Width / (double)pm.Height - 16.0 / 9) > 0.01)
                    frame = Letterboxed(frame, pm.Width, pm.Height);
                if (look is not null && ClipLook.HasTone(look)) frame = Toned(frame, look);
                if (look is null || !ClipLook.IsZoomed(look))
                {
                    // Fast path, as before zoom existed: GDI+'s ImageAttributes draw is several times
                    // slower, which showed as lag when the window was maximised during playback.
                    g.DrawImage(frame, canvas);
                }
                else
                {
                    var (x, y, w, h) = ClipLook.ViewRect(look);
                    var src = new RectangleF((float)(x * frame.Width), (float)(y * frame.Height), (float)(w * frame.Width), (float)(h * frame.Height));
                    g.PixelOffsetMode = PixelOffsetMode.Half; // no half-pixel shift when drawing a zoomed part
                    using var attrs = new ImageAttributes();
                    attrs.SetWrapMode(WrapMode.TileFlipXY); // edge pixels repeat instead of fading to transparent
                    g.DrawImage(frame, Rectangle.Round(canvas), src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
                    g.PixelOffsetMode = PixelOffsetMode.Default;
                }
            }
            if (_engine.PlayingOriginal && _engine.PlayingMedia is { } om)
                Badge(g, canvas, $"Optimising {_library.GetState(om.Id).Progress:P0}: playing the original for now");
        }
        else if (_engine.WaitingForProxy && StandIn() is { } img)
        {
            // Proxy not ready: show the photo itself (stills) or the thumbnail (videos) so you can keep working.
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(img, Fit(img.Size, canvas));
            Badge(g, canvas, "Optimising for playback…");
        }
        else
        {
            var msg = _session.Project.Timeline.Count == 0 ? "Drop video files here, or click Import"
                    : _engine.WaitingForProxy ? "Optimising clip for editing…"
                    : "";
            TextRenderer.DrawText(g, msg, Font, Rectangle.Round(canvas), Color.Gray,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        g.SetClip(canvas);
        // Overlay tracks (picture-in-picture / cutaways) over the main picture, under the annotations (as in the export).
        _engine.Overlays.Draw(g, canvas, _session.Project, _engine.Position, SelectedOverlayId, Font);
        foreach (var a in TimelineOps.VisibleAt(_session.Project, _engine.Position))
        {
            if (a.Id == _editingId) continue; // the inline editor is showing it
            AnnotationRenderer.Draw(g, a, canvas);
        }
        if (_creating is not null) AnnotationRenderer.Draw(g, _creating, canvas);
        g.ResetClip();

        if (Selected() is { } sel && IsVisibleNow(sel)) DrawSelection(g, sel, canvas);

        if (_notice is { } notice)
        {
            var size = TextRenderer.MeasureText(g, notice, Font);
            var r = new Rectangle((int)(canvas.X + (canvas.Width - size.Width) / 2) - 14, (int)canvas.Y + 14, size.Width + 28, size.Height + 14);
            Theme.FillRounded(g, Color.FromArgb(225, 20, 20, 24), r, 8);
            Theme.DrawRounded(g, Color.FromArgb(120, Theme.Accent), 1, r, 8);
            TextRenderer.DrawText(g, notice, Font, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    void Badge(Graphics g, RectangleF canvas, string text)
    {
        var size = TextRenderer.MeasureText(g, text, Font);
        var badge = new Rectangle((int)canvas.X + 10, (int)canvas.Bottom - 34, size.Width + 22, 24);
        Theme.FillRounded(g, Color.FromArgb(185, 0, 0, 0), badge, 12); // pill
        TextRenderer.DrawText(g, text, Font, badge, Color.Gainsboro, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    Bitmap? _boxed;

    /// <summary>
    /// LibVLC stretches an original to fill 960×540. Squeeze it back to its own shape with black bars,
    /// like the proxy's fit/pad, so zoom and annotations line up the same. Call while holding FrameLock.
    /// </summary>
    Bitmap Letterboxed(Bitmap frame, int mediaW, int mediaH)
    {
        if (_boxed is null || _boxed.Size != frame.Size)
        {
            _boxed?.Dispose();
            _boxed = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppRgb);
        }
        using var g = Graphics.FromImage(_boxed);
        g.Clear(Color.Black);
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.DrawImage(frame, Fit(new Size(mediaW, mediaH), new RectangleF(0, 0, frame.Width, frame.Height)));
        return _boxed;
    }

    Bitmap? _toned;
    byte[]? _toneTable;
    (double B, double C) _toneKey = (double.NaN, double.NaN);

    /// <summary>
    /// The frame with the clip's brightness/contrast curve applied (ClipLook's table, the same values the
    /// export's lutrgb produces). A 960×540 pass is ~1–2 ms. Call while holding the engine's FrameLock.
    /// </summary>
    unsafe Bitmap Toned(Bitmap frame, Clip look)
    {
        if (_toneKey != (look.Brightness, look.Contrast) || _toneTable is null)
        {
            _toneTable = ClipLook.ToneTable(look);
            _toneKey = (look.Brightness, look.Contrast);
        }
        if (_toned is null || _toned.Size != frame.Size)
        {
            _toned?.Dispose();
            _toned = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppRgb);
        }
        var rect = new Rectangle(Point.Empty, frame.Size);
        var src = frame.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        var dst = _toned.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            fixed (byte* t = _toneTable)
            {
                for (int yy = 0; yy < rect.Height; yy++)
                {
                    byte* s = (byte*)src.Scan0 + yy * src.Stride;
                    byte* d = (byte*)dst.Scan0 + yy * dst.Stride;
                    for (int xx = 0; xx < rect.Width; xx++, s += 4, d += 4)
                    {
                        d[0] = t[s[0]];
                        d[1] = t[s[1]];
                        d[2] = t[s[2]];
                        d[3] = 255;
                    }
                }
            }
        }
        finally
        {
            frame.UnlockBits(src);
            _toned.UnlockBits(dst);
        }
        return _toned;
    }

    /// <summary>Picture for the clip under the playhead while its proxy is being made, or null.</summary>
    Image? StandIn()
    {
        if (TimelineOps.Locate(_session.Project, _engine.Position) is not { } hit ||
            _session.Project.FindMedia(hit.Clip.SourceId) is not { } media) return null;
        var path = media.IsStill ? media.OriginalPath : _library.GetState(media.Id).ThumbnailPath;
        if (path is null || !File.Exists(path)) return null;
        if (path == _standInPath) return _standIn;

        _standIn?.Dispose();
        _standIn = null;
        _standInPath = path;
        try
        {
            using var fs = File.OpenRead(path);
            using var src = Image.FromStream(fs);
            ApplyExifOrientation(src);
            // Keep at most ~1080p in memory: a 12 MP photo is 48 MB as a bitmap.
            float scale = Math.Min(1f, Math.Min(1920f / src.Width, 1080f / src.Height));
            _standIn = new Bitmap(src, Math.Max(1, (int)(src.Width * scale)), Math.Max(1, (int)(src.Height * scale)));
        }
        catch (Exception) { /* unreadable by GDI+ (e.g. HEIC): fall back to the text message */ }
        return _standIn;
    }

    /// <summary>Phone photos store rotation in EXIF tag 0x0112; GDI+ doesn't apply it on its own.</summary>
    static void ApplyExifOrientation(Image img)
    {
        if (!img.PropertyIdList.Contains(0x0112)) return;
        var rf = img.GetPropertyItem(0x0112)?.Value?[0] switch
        {
            2 => RotateFlipType.RotateNoneFlipX,
            3 => RotateFlipType.Rotate180FlipNone,
            4 => RotateFlipType.Rotate180FlipX,
            5 => RotateFlipType.Rotate90FlipX,
            6 => RotateFlipType.Rotate90FlipNone,
            7 => RotateFlipType.Rotate270FlipX,
            8 => RotateFlipType.Rotate270FlipNone,
            _ => (RotateFlipType?)null,
        };
        if (rf is { } r) img.RotateFlip(r);
    }

    /// <summary>Letterbox/pillarbox <paramref name="size"/> into <paramref name="box"/>, like Canvas.FitFilter.</summary>
    static RectangleF Fit(Size size, RectangleF box)
    {
        float s = Math.Min(box.Width / size.Width, box.Height / size.Height);
        float w = size.Width * s, h = size.Height * s;
        return new RectangleF(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
    }

    void DrawSelection(Graphics g, Annotation a, RectangleF canvas)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(0, 170, 255), 1.5f) { DashStyle = DashStyle.Dash };
        if (a.Kind == AnnotationKind.Text)
        {
            var r = AnnotationRenderer.TextBounds(a, canvas);
            g.DrawRectangle(pen, r.X - 2, r.Y - 2, r.Width + 4, r.Height + 4);
            return;
        }
        var p1 = AnnotationRenderer.ToCanvas(canvas, a.X1, a.Y1);
        var p2 = AnnotationRenderer.ToCanvas(canvas, a.X2, a.Y2);
        if (a.Kind == AnnotationKind.Box)
        {
            var r = AnnotationRenderer.RectFrom(p1, p2);
            g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
        }
        foreach (var hp in Handles(a, canvas)) DrawHandle(g, hp);
    }

    static void DrawHandle(Graphics g, PointF p)
    {
        var r = new RectangleF(p.X - HandleSize / 2, p.Y - HandleSize / 2, HandleSize, HandleSize);
        g.FillRectangle(Brushes.White, r);
        using var pen = new Pen(Color.FromArgb(0, 170, 255), 1.5f);
        g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
    }

    /// <summary>Handles in order: P1, P2, then for boxes the other two corners (X1,Y2) and (X2,Y1).</summary>
    static PointF[] Handles(Annotation a, RectangleF canvas) => a.Kind switch
    {
        AnnotationKind.Arrow => new[] { AnnotationRenderer.ToCanvas(canvas, a.X1, a.Y1), AnnotationRenderer.ToCanvas(canvas, a.X2, a.Y2) },
        AnnotationKind.Box => new[]
        {
            AnnotationRenderer.ToCanvas(canvas, a.X1, a.Y1), AnnotationRenderer.ToCanvas(canvas, a.X2, a.Y2),
            AnnotationRenderer.ToCanvas(canvas, a.X1, a.Y2), AnnotationRenderer.ToCanvas(canvas, a.X2, a.Y1),
        },
        _ => Array.Empty<PointF>(),
    };

    /// <summary>Resize cursor when hovering a handle of the selected annotation, else null.</summary>
    Cursor? HandleCursorAt(Point p)
    {
        if (Selected() is not { } sel || !IsVisibleNow(sel)) return null;
        var hs = Handles(sel, CanvasRect);
        foreach (var h in hs)
        {
            if (Distance(h, p) > HandleSize) continue;
            if (sel.Kind != AnnotationKind.Box) return Cursors.Cross; // arrow end: free movement
            // Box corner: diagonal depends on which corner it is relative to the box centre.
            float cx = hs.Average(q => q.X), cy = hs.Average(q => q.Y);
            return (h.X - cx) * (h.Y - cy) > 0 ? Cursors.SizeNWSE : Cursors.SizeNESW;
        }
        return null;
    }

    Annotation? Selected() => SelectedAnnotationId is { } id ? _session.Project.FindAnnotation(id) : null;

    /// <summary>The topmost overlay showing under a point, if any.</summary>
    OverlayClip? OverlayAt(Point pt)
    {
        var p = _session.Project;
        return Enumerable.Reverse(TimelineOps.OverlaysAt(p, _engine.Position))
            .FirstOrDefault(o => OverlayLanePlayer.Dest(CanvasRect, o, p.FindMedia(o.SourceId)).Contains(pt));
    }

    bool IsVisibleNow(Annotation a) => TimelineOps.VisibleAt(_session.Project, _engine.Position).Any(v => v.Id == a.Id);

    // ---------------- Mouse ----------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left || _session is null) return;
        CommitTextEdit();
        _engine.Pause();

        var canvas = CanvasRect;
        var norm = ToNorm(e.Location);
        _dragStartNorm = norm;

        if (_tool is Tool.Arrow or Tool.Box)
        {
            if (TimelineOps.Locate(_session.Project, _engine.Position) is null) return;
            _drag = DragMode.Create;
            _creating = new Annotation
            {
                Kind = _tool == Tool.Arrow ? AnnotationKind.Arrow : AnnotationKind.Box,
                X1 = norm.X, Y1 = norm.Y, X2 = norm.X, Y2 = norm.Y,
            };
            return;
        }

        if (_tool == Tool.Text)
        {
            Annotation? created = null;
            _session.Execute(p => created = TimelineOps.CreateAnnotation(p, _engine.Position, AnnotationKind.Text, norm.X, norm.Y, 0, 0));
            Tool = Tool.Select;
            if (created is not null)
            {
                Select(created.Id);
                BeginTextEdit(created.Id, selectAll: true);
            }
            return;
        }

        // Select tool: handles of the current selection first, then topmost annotation.
        if (Selected() is { } sel && IsVisibleNow(sel))
        {
            var hs = Handles(sel, canvas);
            for (int i = 0; i < hs.Length; i++)
            {
                if (Distance(hs[i], e.Location) <= HandleSize)
                {
                    _drag = i switch { 0 => DragMode.Handle1, 1 => DragMode.Handle2, 2 => DragMode.Corner12, _ => DragMode.Corner21 };
                    _dragOrigin = sel.Clone();
                    _session.BeginEdit();
                    return;
                }
            }
        }

        var hit = TimelineOps.VisibleAt(_session.Project, _engine.Position).Reverse()
            .FirstOrDefault(a => AnnotationRenderer.HitTest(a, canvas, e.Location));
        if (hit is null && OverlayAt(e.Location) is { } ov)
        {
            // Drag an overlay around the picture (annotations are on top, so they win the click).
            Select(null);
            OverlayClicked?.Invoke(ov.Id);
            _drag = DragMode.MoveOverlay;
            _overlayOrigin = (ov.Id, ov.X, ov.Y);
            _session.BeginEdit();
            return;
        }
        Select(hit?.Id);
        if (hit is null) OverlayClicked?.Invoke(null);
        if (hit is not null)
        {
            _drag = DragMode.Move;
            _dragOrigin = hit.Clone();
            _session.BeginEdit();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_session is null) return;
        var norm = ToNorm(e.Location);

        if (_drag == DragMode.None)
        {
            if (_tool == Tool.Select)
            {
                Cursor = HandleCursorAt(e.Location) ?? (TimelineOps.VisibleAt(_session.Project, _engine.Position)
                    .Any(a => AnnotationRenderer.HitTest(a, CanvasRect, e.Location)) || OverlayAt(e.Location) is not null
                    ? Cursors.SizeAll : Cursors.Default);
            }
            return;
        }

        if (_drag == DragMode.Create && _creating is not null)
        {
            _creating.X2 = norm.X;
            _creating.Y2 = norm.Y;
            Invalidate();
            return;
        }

        if (_drag == DragMode.MoveOverlay)
        {
            // Unclamped mouse position, so the overlay follows the pointer even near the edges.
            var c = CanvasRect;
            double ox = _overlayOrigin.X + (e.X - c.X) / c.Width - _dragStartNorm.X;
            double oy = _overlayOrigin.Y + (e.Y - c.Y) / c.Height - _dragStartNorm.Y;
            var oid = _overlayOrigin.Id;
            _session.Preview(p => TimelineOps.SetOverlayLayout(p, oid, ox, oy, null));
            return;
        }

        if (_dragOrigin is not { } o || SelectedAnnotationId is not { } id) return;
        float dx = norm.X - _dragStartNorm.X, dy = norm.Y - _dragStartNorm.Y;
        var mode = _drag;
        _session.Preview(p =>
        {
            if (p.FindAnnotation(id) is not { } a) return;
            switch (mode)
            {
                case DragMode.Move:
                    // Clamp so the shape can't be dragged off-canvas.
                    float minX = Math.Min(o.X1, o.Kind == AnnotationKind.Text ? o.X1 : o.X2);
                    float maxX = Math.Max(o.X1, o.Kind == AnnotationKind.Text ? o.X1 : o.X2);
                    float minY = Math.Min(o.Y1, o.Kind == AnnotationKind.Text ? o.Y1 : o.Y2);
                    float maxY = Math.Max(o.Y1, o.Kind == AnnotationKind.Text ? o.Y1 : o.Y2);
                    float cdx = Math.Clamp(dx, -minX, 1 - maxX), cdy = Math.Clamp(dy, -minY, 1 - maxY);
                    a.X1 = o.X1 + cdx; a.Y1 = o.Y1 + cdy; a.X2 = o.X2 + cdx; a.Y2 = o.Y2 + cdy;
                    break;
                case DragMode.Handle1: a.X1 = norm.X; a.Y1 = norm.Y; break;
                case DragMode.Handle2: a.X2 = norm.X; a.Y2 = norm.Y; break;
                case DragMode.Corner12: a.X1 = norm.X; a.Y2 = norm.Y; break;
                case DragMode.Corner21: a.X2 = norm.X; a.Y1 = norm.Y; break;
            }
            TimelineOps.SyncLinked(p, id);
        });
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag == DragMode.Create && _creating is { } c)
        {
            var canvas = CanvasRect;
            float pxW = Math.Abs(c.X2 - c.X1) * canvas.Width, pxH = Math.Abs(c.Y2 - c.Y1) * canvas.Height;
            _creating = null;
            if (pxW + pxH > 8) // ignore accidental clicks
            {
                Annotation? created = null;
                _session.Execute(p =>
                {
                    created = TimelineOps.CreateAnnotation(p, _engine.Position, c.Kind, c.X1, c.Y1, c.X2, c.Y2);
                    if (created is { Kind: AnnotationKind.Box }) created.StrokeColor = "#FFFFD600"; // yellow highlight box by default
                });
                if (created is not null) Select(created.Id);
            }
            Tool = Tool.Select;
            Invalidate();
        }
        else if (_drag != DragMode.None)
        {
            _session.CommitEdit();
        }
        _drag = DragMode.None;
        _dragOrigin = null;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (Selected() is { Kind: AnnotationKind.Text } a && AnnotationRenderer.HitTest(a, CanvasRect, e.Location))
            BeginTextEdit(a.Id, selectAll: false);
    }

    static float Distance(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // ---------------- Inline text editing ----------------

    public void BeginTextEdit(Guid id, bool selectAll)
    {
        if (_session.Project.FindAnnotation(id) is not { Kind: AnnotationKind.Text } a) return;
        CommitTextEdit();
        var canvas = CanvasRect;
        var bounds = AnnotationRenderer.TextBounds(a, canvas);
        var font = AnnotationRenderer.CreateFont(a, canvas);

        _editingId = id;
        _editor = new TextBox
        {
            Multiline = true,
            Text = a.Text.Replace("\n", Environment.NewLine),
            Font = font,
            ForeColor = ColorHex.Parse(a.StrokeColor, Color.White),
            BackColor = Color.FromArgb(40, 40, 40),
            BorderStyle = BorderStyle.FixedSingle,
            Location = Point.Round(bounds.Location),
            Size = new Size((int)Math.Max(bounds.Width + 40, 160), (int)Math.Max(bounds.Height + 10, font.Height + 12)),
        };
        _editor.KeyDown += (_, ke) =>
        {
            if (ke.KeyCode == Keys.Enter && !ke.Shift) { ke.SuppressKeyPress = true; CommitTextEdit(); }
            else if (ke.KeyCode == Keys.Escape) { ke.SuppressKeyPress = true; CancelTextEdit(); }
        };
        _editor.TextChanged += (_, _) =>
        {
            if (_editor is null) return;
            using var g = _editor.CreateGraphics();
            var sz = g.MeasureString(_editor.Text + "W", _editor.Font);
            _editor.Width = (int)Math.Max(160, sz.Width + 20);
            _editor.Height = (int)Math.Max(_editor.Font.Height + 12, sz.Height + 10);
        };
        _editor.LostFocus += (_, _) => CommitTextEdit();
        Controls.Add(_editor);
        _editor.Focus();
        if (selectAll) _editor.SelectAll(); else _editor.SelectionStart = _editor.TextLength;
        Invalidate();
    }

    public void CommitTextEdit()
    {
        if (_editor is null || _editingId is not { } id) return;
        var text = _editor.Text.Replace("\r\n", "\n").TrimEnd();
        CloseEditor();
        _session.Execute(p =>
        {
            if (p.FindAnnotation(id) is null) return;
            if (string.IsNullOrWhiteSpace(text)) TimelineOps.DeleteAnnotation(p, id); // empty text = delete
            else { p.FindAnnotation(id)!.Text = text; TimelineOps.SyncLinked(p, id); }
        });
    }

    void CancelTextEdit() => CloseEditor();

    void CloseEditor()
    {
        var ed = _editor;
        _editor = null;
        _editingId = null;
        if (ed is not null)
        {
            var font = ed.Font;
            Controls.Remove(ed);
            ed.Dispose();
            font.Dispose();
        }
        Focus();
        Invalidate();
    }
}
