using System.Text.Json;

namespace Flyweight.App.Controls;

/// <summary>
/// Base for the Properties panels: a scrolling stack of rounded, collapsible <see cref="PropertyGroup"/> cards.
/// Subclasses build with <see cref="Group"/>, <see cref="Row"/> and <see cref="Span"/>. The label column is sized
/// to the longest label (measured at the current DPI), so labels never get cut off at high display scaling.
/// </summary>
public abstract class PropertySheet : UserControl
{
    const int Pad = 6, Gap = 8;
    readonly List<PropertyGroup> _groups = new();
    readonly string _key;
    PropertyGroup? _current;

    protected PropertySheet(string key)
    {
        _key = key;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        AutoScroll = true;
        DoubleBuffered = true;
        Theme.DarkScrollbars(this);
    }

    protected IReadOnlyList<PropertyGroup> Groups => _groups;

    /// <summary>Start a new group: rows and spans after this go into it.</summary>
    protected PropertyGroup Group(string title)
    {
        var g = new PropertyGroup(title, $"{_key}/{title}");
        g.HeightChanged += () => PerformLayout();
        g.VisibleChanged += (_, _) => PerformLayout();
        _groups.Add(g);
        Controls.Add(g);
        _current = g;
        return g;
    }

    /// <summary>A labelled row in the current group. Returns the label, so callers can hide the row.</summary>
    protected Label Row(string label, Control editor)
    {
        var l = _current!.AddRow(label, editor);
        AlignLabels();
        return l;
    }

    /// <summary>A full-width control (note, button, name) in the current group.</summary>
    protected void Span(Control c) => _current!.AddSpan(c);

    /// <summary>Remove every group (the annotation panel rebuilds when the shape kind changes).</summary>
    protected void ClearGroups()
    {
        SuspendLayout();
        foreach (var g in _groups) { Controls.Remove(g); g.Dispose(); }
        _groups.Clear();
        _current = null;
        ResumeLayout();
    }

    /// <summary>One label width for every group, so the sliders line up down the whole panel.</summary>
    void AlignLabels()
    {
        int w = 0;
        foreach (var g in _groups) w = Math.Max(w, g.WidestLabel());
        foreach (var g in _groups) g.SetLabelWidth(w + LogicalToDeviceUnits(6));
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int pad = LogicalToDeviceUnits(Pad), gap = LogicalToDeviceUnits(Gap);
        int y = AutoScrollPosition.Y + pad;
        int w = Math.Max(120, ClientSize.Width - pad * 2);
        foreach (var g in _groups)
        {
            if (!g.Visible) continue;
            g.SetBounds(pad, y, w, g.Height);
            y += g.Height + gap;
        }
        int total = y - AutoScrollPosition.Y + pad - gap;
        if (AutoScrollMinSize.Height != total) AutoScrollMinSize = new Size(0, total);
    }
}

/// <summary>
/// A rounded card with a clickable header (▾ title) that collapses its rows. Remembers whether it was collapsed
/// (per panel and title) across runs.
/// </summary>
public sealed class PropertyGroup : Panel
{
    readonly TableLayoutPanel _body;
    readonly string _stateKey;
    string _title;
    bool _expanded, _hover;
    int _labelWidth = 92;

    public event Action? HeightChanged;

    static readonly Color Card = Color.FromArgb(36, 36, 42);
    static readonly Color CardHover = Color.FromArgb(42, 42, 50);

    int HeaderH => LogicalToDeviceUnits(32);

    public PropertyGroup(string title, string stateKey)
    {
        _title = title;
        _stateKey = stateKey;
        _expanded = !UiState.IsCollapsed(stateKey);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Card;
        ForeColor = Theme.Text;
        _body = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = Card, Visible = _expanded,
            Padding = new Padding(0, 0, 0, 2),
        };
        _body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, _labelWidth));
        _body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _body.SizeChanged += (_, _) => UpdateHeight();
        Controls.Add(_body);
        Padding = new Padding(LogicalToDeviceUnits(8), HeaderH, LogicalToDeviceUnits(8), LogicalToDeviceUnits(6));
        UpdateHeight();
    }

    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; Invalidate(); } }
    }

    public bool Expanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            _body.Visible = value;
            UiState.SetCollapsed(_stateKey, !value);
            UpdateHeight();
            Invalidate();
        }
    }

    internal Label AddRow(string label, Control editor)
    {
        _body.RowCount++;
        _body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var l = new Label
        {
            Text = label, ForeColor = Theme.Text, AutoSize = false, Height = LogicalToDeviceUnits(30), Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, BackColor = Card, Margin = new Padding(0),
        };
        editor.Margin = new Padding(0, 3, 0, 3);
        _body.Controls.Add(l, 0, _body.RowCount - 1);
        _body.Controls.Add(editor, 1, _body.RowCount - 1);
        return l;
    }

    internal void AddSpan(Control c)
    {
        _body.RowCount++;
        _body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (c is Label { AutoSize: true } l) l.BackColor = Card;
        _body.Controls.Add(c, 0, _body.RowCount - 1);
        _body.SetColumnSpan(c, 2);
        FitSpans();
    }

    internal int WidestLabel()
    {
        int w = 0;
        foreach (Control c in _body.Controls)
            if (_body.GetColumn(c) == 0 && _body.GetColumnSpan(c) == 1 && c is Label l && l.Text.Length > 0)
                w = Math.Max(w, TextRenderer.MeasureText(l.Text, l.Font).Width);
        return w;
    }

    internal void SetLabelWidth(int w)
    {
        if (_labelWidth == w) return;
        _labelWidth = w;
        _body.ColumnStyles[0].Width = w;
    }

    /// <summary>Wrapping notes follow the card's width.</summary>
    void FitSpans()
    {
        int w = Math.Max(60, ClientSize.Width - Padding.Horizontal);
        foreach (Control c in _body.Controls)
            if (c is Label { AutoSize: true } l && _body.GetColumnSpan(c) == 2) l.MaximumSize = new Size(w, 0);
    }

    void UpdateHeight()
    {
        int h = HeaderH + (_expanded ? _body.Height + Padding.Bottom : 0);
        if (Height == h) return;
        Height = h;
        HeightChanged?.Invoke();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        FitSpans();
    }

    bool InHeader(Point p) => p.Y < HeaderH;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool h = InHeader(e.Location);
        Cursor = h ? Cursors.Hand : Cursors.Default;
        if (h != _hover) { _hover = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover) { _hover = false; Invalidate(); }
    }

    bool _pressed;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _pressed = e.Button == MouseButtons.Left && InHeader(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_pressed && e.Button == MouseButtons.Left && InHeader(e.Location)) Expanded = !Expanded;
        _pressed = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Panel);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        float radius = LogicalToDeviceUnits(8);
        Theme.FillRounded(g, Card, r, radius);
        if (_hover)
        {
            // Lighten just the header strip, keeping the card's rounded top corners.
            var state = g.Save();
            g.SetClip(new RectangleF(0, 0, Width, HeaderH));
            Theme.FillRounded(g, CardHover, r, radius);
            g.Restore(state);
        }
        Theme.DrawRounded(g, Color.FromArgb(52, 52, 60), 1, r, radius);

        int x = LogicalToDeviceUnits(12);

        // The collapse / expand button on the right: a rounded square with a drawn chevron (down = open, right =
        // folded), lit in the accent colour while the pointer is over the header.
        float bs = LogicalToDeviceUnits(22);
        var btn = new RectangleF(Width - x - bs + LogicalToDeviceUnits(4), (HeaderH - bs) / 2f, bs, bs);
        Theme.FillRounded(g, _hover ? Theme.AccentDim : Color.FromArgb(52, 52, 62), btn, LogicalToDeviceUnits(6));
        float cx = btn.X + bs / 2, cy = btn.Y + bs / 2, a = LogicalToDeviceUnits(9) / 2f;
        var old = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var pen = new Pen(_hover ? Color.White : Color.FromArgb(215, 215, 225), LogicalToDeviceUnits(2)))
        {
            pen.StartCap = pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
            var pts = _expanded
                ? new[] { new PointF(cx - a, cy - a / 2), new PointF(cx, cy + a / 2), new PointF(cx + a, cy - a / 2) }
                : new[] { new PointF(cx - a / 2, cy - a), new PointF(cx + a / 2, cy), new PointF(cx - a / 2, cy + a) };
            g.DrawLines(pen, pts);
        }
        g.SmoothingMode = old;

        using var bold = new Font(Font.FontFamily, 8.5f, FontStyle.Bold);
        var text = new Rectangle(x, 0, (int)btn.X - x - LogicalToDeviceUnits(6), HeaderH);
        TextRenderer.DrawText(g, _title.ToUpperInvariant(), bold, text, _expanded || _hover ? Color.FromArgb(215, 215, 225) : Color.FromArgb(160, 160, 170),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (_expanded)
        {
            using var pen = new Pen(Color.FromArgb(48, 48, 56));
            g.DrawLine(pen, x, HeaderH - 4, Width - x, HeaderH - 4);
        }
    }
}

/// <summary>Small UI preferences kept between runs (which property groups are collapsed).</summary>
static class UiState
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flyweight", "ui-state.json");
    static HashSet<string>? _collapsed;

    static HashSet<string> Collapsed
    {
        get
        {
            if (_collapsed is not null) return _collapsed;
            try
            {
                _collapsed = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(FilePath)) ?? new()
                    : new();
            }
            catch { _collapsed = new(); } // a damaged file just means everything starts open
            return _collapsed;
        }
    }

    public static bool IsCollapsed(string key) => Collapsed.Contains(key);

    public static void SetCollapsed(string key, bool collapsed)
    {
        if (!(collapsed ? Collapsed.Add(key) : Collapsed.Remove(key))) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Collapsed));
        }
        catch { } // not worth interrupting editing over
    }
}
