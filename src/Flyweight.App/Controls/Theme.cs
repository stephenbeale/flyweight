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

    /// <summary>Flat button with rounded corners and a soft hover, for the transport bar and panels.</summary>
    public sealed class RoundButton : Button
    {
        bool _hover, _down;

        public bool Primary { get; set; }

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
                       : Primary ? (_down ? AccentDim : _hover ? Color.FromArgb(40, 190, 255) : Accent)
                       : _down ? Pressed : _hover ? Raised : Color.FromArgb(40, 40, 47);
            FillRounded(g, fill, r);
            if (!Primary && Enabled) DrawRounded(g, Border, 1, r);
            TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), !Enabled ? Muted : Primary ? Color.White : ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }
}
