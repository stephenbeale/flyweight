using System.Drawing.Drawing2D;
using Flyweight.App.Playback;
using Flyweight.App.Rendering;
using Flyweight.Core.Editing;
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
    Tool _tool = Tool.Select;

    // Drag state
    enum DragMode { None, Create, Move, Handle1, Handle2, Corner12, Corner21 }
    DragMode _drag;
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
    }

    public Guid? SelectedAnnotationId { get; private set; }
    public event EventHandler? SelectionChanged;
    public event EventHandler? ToolChanged;
    public bool IsEditingText => _editor is not null;

    public Tool Tool
    {
        get => _tool;
        set { _tool = value; Cursor = value == Tool.Select ? Cursors.Default : Cursors.Cross; ToolChanged?.Invoke(this, EventArgs.Empty); }
    }

    public void Bind(ProjectSession session, PlaybackEngine engine)
    {
        _session = session;
        _engine = engine;
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
            lock (_engine.FrameLock) g.DrawImage(_engine.Frame, canvas);
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
        foreach (var a in TimelineOps.VisibleAt(_session.Project, _engine.Position))
        {
            if (a.Id == _editingId) continue; // the inline editor is showing it
            AnnotationRenderer.Draw(g, a, canvas);
        }
        if (_creating is not null) AnnotationRenderer.Draw(g, _creating, canvas);
        g.ResetClip();

        if (Selected() is { } sel && IsVisibleNow(sel)) DrawSelection(g, sel, canvas);
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

    Annotation? Selected() => SelectedAnnotationId is { } id ? _session.Project.FindAnnotation(id) : null;

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
        Select(hit?.Id);
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
                var over = TimelineOps.VisibleAt(_session.Project, _engine.Position)
                    .Any(a => AnnotationRenderer.HitTest(a, CanvasRect, e.Location));
                Cursor = over ? Cursors.SizeAll : Cursors.Default;
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
            if (p.FindAnnotation(id) is not { } a) return;
            if (string.IsNullOrWhiteSpace(text)) p.Annotations.Remove(a); // empty text = delete
            else a.Text = text;
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
