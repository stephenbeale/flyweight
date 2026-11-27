using System.Drawing.Drawing2D;

namespace Flyweight.App.Controls;

/// <summary>
/// One place for the app's look: dark surfaces, one accent colour, rounded corners (a Bootstrap-like feel,
/// drawn with GDI+ because WinForms has no CSS). Controls take their colours and shapes from here.
/// </summary>
static class Theme
{
    public static readonly Color Window = Color.FromArgb(24, 24, 28);
    public static readonly Color Panel = Color.FromArgb(28, 28, 32);
    public static readonly Color Bar = Color.FromArgb(34, 34, 40);        // toolbar, status bar, transport
    public static readonly Color Raised = Color.FromArgb(48, 48, 56);     // hover / buttons
    public static readonly Color Pressed = Color.FromArgb(62, 62, 72);
    public static readonly Color Border = Color.FromArgb(58, 58, 66);
    public static readonly Color Text = Color.Gainsboro;
    public static readonly Color Muted = Color.Gray;
    public static readonly Color Accent = Color.FromArgb(0, 170, 255);
    public static readonly Color AccentDim = Color.FromArgb(0, 110, 170);

    public const float Radius = 6;

    /// <summary>A rectangle with rounded corners (radius shrinks to fit small shapes).</summary>
    public static GraphicsPath Rounded(RectangleF r, float radius = Radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d < 1) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void FillRounded(Graphics g, Color c, RectangleF r, float radius = Radius)
    {
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Rounded(r, radius);
        using var b = new SolidBrush(c);
        g.FillPath(b, path);
        g.SmoothingMode = old;
    }

    public static void DrawRounded(Graphics g, Color c, float width, RectangleF r, float radius = Radius)
    {
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Rounded(r, radius);
        using var pen = new Pen(c, width);
        g.DrawPath(pen, path);
        g.SmoothingMode = old;
    }

    /// <summary>Dark toolbar / menu / status bar: rounded highlight on hover, accent for the checked tool.</summary>
    public sealed class ToolRenderer : ToolStripProfessionalRenderer
    {
        public ToolRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(e.ToolStrip is ToolStripDropDown ? Raised : Bar);
            e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is ToolStripDropDown)
                using (var pen = new Pen(Border)) e.Graphics.DrawRectangle(pen, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        }

        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e) => Highlight(e);
        protected override void OnRenderSplitButtonBackground(ToolStripItemRenderEventArgs e)
        {
            Highlight(e);
            var sb = (ToolStripSplitButton)e.Item;
            var arrow = sb.DropDownButtonBounds;
            DrawArrow(new ToolStripArrowRenderEventArgs(e.Graphics, sb, arrow, Text, ArrowDirection.Down));
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            FillRounded(e.Graphics, Pressed, new RectangleF(3, 1, e.Item.Width - 6, e.Item.Height - 2), 4);
        }

        static void Highlight(ToolStripItemRenderEventArgs e)
        {
            var checkedTool = e.Item is ToolStripButton { Checked: true };
            // A button that opened a dialog never gets "mouse left", so WinForms keeps it Selected/Pressed after the
            // dialog closes. Only highlight while the pointer is really over it.
            bool under = e.ToolStrip is { } ts && e.Item.Bounds.Contains(ts.PointToClient(Cursor.Position));
            Color? c = checkedTool ? AccentDim : under && e.Item.Pressed ? Pressed : under && e.Item.Selected && e.Item.Enabled ? Raised : null;
            if (c is { } col) FillRounded(e.Graphics, col, new RectangleF(1, 2, e.Item.Width - 2, e.Item.Height - 4), 5);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = !e.Item.Enabled ? Color.FromArgb(105, 105, 115) : e.Item is ToolStripButton { Checked: true } ? Color.White : Text;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Border);
            if (e.Vertical) e.Graphics.DrawLine(pen, e.Item.Width / 2, 6, e.Item.Width / 2, e.Item.Height - 6);
            else e.Graphics.DrawLine(pen, 8, e.Item.Height / 2, e.Item.Width - 8, e.Item.Height / 2);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Text;
            base.OnRenderArrow(e);
        }

        sealed class DarkColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Raised;
            public override Color ImageMarginGradientBegin => Raised;
            public override Color ImageMarginGradientMiddle => Raised;
            public override Color ImageMarginGradientEnd => Raised;
            public override Color MenuBorder => Border;
            public override Color MenuItemBorder => Color.Transparent;
            public override Color SeparatorDark => Border;
            public override Color SeparatorLight => Border;
            public override Color StatusStripGradientBegin => Bar;
            public override Color StatusStripGradientEnd => Bar;
        }
    }

    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);

    /// <summary>Windows' dark scrollbars (and dark list chrome) for a control, applied whenever its handle is (re)created.</summary>
    public static void DarkScrollbars(Control c)
    {
        void Apply() { try { _ = SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { } } // older Windows: stays light
        if (c.IsHandleCreated) Apply();
        c.HandleCreated += (_, _) => Apply();
    }

    /// <summary>A drop-down list drawn dark (WinForms' flat combo ignores BackColor for the closed box).</summary>
    public sealed class DarkCombo : ComboBox
    {
        static readonly Color Field = Color.FromArgb(46, 46, 54);

        public DarkCombo()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            DrawMode = DrawMode.OwnerDrawFixed;
            FlatStyle = FlatStyle.Standard; // Flat paints the closed box itself, in white
            BackColor = Field;
            ForeColor = Theme.Text;
            ItemHeight = LogicalToDeviceUnits(20);
            DarkScrollbars(this); // the drop-down list's scrollbar
        }

        bool _hover;
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }

        /// <summary>The closed box is painted by Windows' theme (white) whatever BackColor says: paint over it.</summary>
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != 0x000F /* WM_PAINT */ || !IsHandleCreated) return;
            using var g = Graphics.FromHwnd(Handle);
            g.Clear(Parent?.BackColor ?? Theme.Panel);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float radius = LogicalToDeviceUnits(5);
            FillRounded(g, _hover && Enabled ? Color.FromArgb(54, 54, 64) : Field, r, radius);
            DrawRounded(g, Focused ? Accent : Color.FromArgb(66, 66, 76), 1, r, radius);
            int arrowW = LogicalToDeviceUnits(18);
            var textRect = new Rectangle(LogicalToDeviceUnits(7), 0, Width - arrowW - LogicalToDeviceUnits(7), Height);
            TextRenderer.DrawText(g, SelectedIndex >= 0 ? GetItemText(SelectedItem) : "", Font, textRect, Enabled ? Theme.Text : Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            // A small chevron.
            float cx = Width - arrowW / 2f - LogicalToDeviceUnits(3), cy = Height / 2f, a = LogicalToDeviceUnits(4);
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Enabled ? Color.Silver : Muted, 1.6f);
            g.DrawLines(pen, new[] { new PointF(cx - a, cy - a / 2), new PointF(cx, cy + a / 2), new PointF(cx + a, cy - a / 2) });
            g.SmoothingMode = old;
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            var g = e.Graphics;
            bool selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using (var b = new SolidBrush(selected ? AccentDim : Field)) g.FillRectangle(b, e.Bounds);
            if (e.Index >= 0)
                TextRenderer.DrawText(g, GetItemText(Items[e.Index]), Font, Rectangle.Inflate(e.Bounds, -4, 0),
                    Enabled ? Theme.Text : Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>A CheckBox drawn as an on/off pill switch. Same events and Checked as a CheckBox.</summary>
    public sealed class ToggleSwitch : CheckBox
    {
        bool _hover;

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            AutoSize = false;
            Height = LogicalToDeviceUnits(26);
            Cursor = Cursors.Hand;
            ForeColor = Theme.Text;
        }

        /// <summary>Hover text, e.g. the keyboard shortcut.</summary>
        public string? Tip { set { if (value is not null) new ToolTip().SetToolTip(this, value); } }

        public override Size GetPreferredSize(Size proposed)
        {
            var t = TextRenderer.MeasureText(base.Text, Font);
            return new Size(LogicalToDeviceUnits(44) + t.Width, LogicalToDeviceUnits(26));
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Width = GetPreferredSize(Size.Empty).Width; }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Panel);
            float h = LogicalToDeviceUnits(18), w = LogicalToDeviceUnits(34), y = (Height - h) / 2f;
            var pill = new RectangleF(1, y, w, h);
            Color fill = !Enabled ? Color.FromArgb(50, 50, 58) : Checked ? (_hover ? Color.FromArgb(40, 190, 255) : Accent)
                       : _hover ? Color.FromArgb(78, 78, 90) : Color.FromArgb(66, 66, 76);
            FillRounded(g, fill, pill, h / 2);
            float k = h - LogicalToDeviceUnits(4);
            float kx = Checked ? pill.Right - k - LogicalToDeviceUnits(2) : pill.X + LogicalToDeviceUnits(2);
            FillRounded(g, Enabled ? Color.White : Color.FromArgb(120, 120, 130), new RectangleF(kx, y + LogicalToDeviceUnits(2), k, k), k / 2);
            if (Focused && ShowFocusCues) DrawRounded(g, Color.FromArgb(120, Accent), 1, RectangleF.Inflate(pill, 2, 2), h / 2 + 2);
            var tr = new Rectangle((int)(pill.Right + LogicalToDeviceUnits(8)), 0, Width, Height);
            TextRenderer.DrawText(g, base.Text, Font, tr, Enabled ? ForeColor : Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>The title strip at the top of a side panel (Media, Properties), with optional muted text on the right.</summary>
    public sealed class PanelHeader : Control
    {
        string _detail = "";

        public PanelHeader(string title)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            base.Text = title;
            Dock = DockStyle.Top;
            Height = LogicalToDeviceUnits(34);
            BackColor = Theme.Panel;
        }

        public string Detail
        {
            get => _detail;
            set { if (_detail != value) { _detail = value; Invalidate(); } }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            int pad = LogicalToDeviceUnits(12);
            using var bold = new Font(Font.FontFamily, 9.5f, FontStyle.Bold);
            var r = new Rectangle(pad, 0, Width - pad * 2, Height);
            TextRenderer.DrawText(g, base.Text, bold, r, Color.FromArgb(225, 225, 232), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (_detail.Length > 0)
            {
                // A small rounded badge on the right (e.g. how many files are in the bin).
                var size = TextRenderer.MeasureText(_detail, Font);
                int bw = size.Width + LogicalToDeviceUnits(6), bh = LogicalToDeviceUnits(20);
                var badge = new RectangleF(Width - pad - bw, (Height - bh) / 2f, bw, bh);
                FillRounded(g, Raised, badge, bh / 2f);
                TextRenderer.DrawText(g, _detail, Font, Rectangle.Round(badge), Color.Silver,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            using var pen = new Pen(Color.FromArgb(44, 44, 52));
            g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        }
    }

    /// <summary>Flat button with rounded corners and a soft hover, for the transport bar and panels.</summary>
    public sealed class RoundButton : Button
    {
        bool _hover, _down;

        public bool Primary { get; set; }

        /// <summary>A custom fill (e.g. the yellow support button), with dark text. Overrides Primary.</summary>
        public Color? Tint { get; set; }

        public RoundButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            ForeColor = Theme.Text;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Panel);
            var r = new RectangleF(2, 2, Width - 4, Height - 4);
            Color fill = !Enabled ? Panel
                       : Tint is { } t ? (_down ? ControlPaint.Dark(t, 0.1f) : _hover ? ControlPaint.Light(t, 0.25f) : t)
                       : Primary ? (_down ? AccentDim : _hover ? Color.FromArgb(40, 190, 255) : Accent)
                       : _down ? Pressed : _hover ? Raised : Color.FromArgb(40, 40, 47);
            FillRounded(g, fill, r);
            if (!Primary && Tint is null && Enabled) DrawRounded(g, Border, 1, r);
            TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), !Enabled ? Muted : Tint is not null ? Color.FromArgb(24, 24, 28) : Primary ? Color.White : ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }
}
