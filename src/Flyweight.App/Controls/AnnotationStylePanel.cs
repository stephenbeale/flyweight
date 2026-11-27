using Flyweight.App.Rendering;
using Flyweight.Core.Editing;
using Flyweight.Core.Model;

namespace Flyweight.App.Controls;

/// <summary>How a style change should reach the undo stack.</summary>
public enum StyleEditPhase
{
    /// <summary>A discrete change (click, tick, typed value): one undo step.</summary>
    Single,
    /// <summary>A slider is being dragged: apply live, no undo step yet.</summary>
    Live,
    /// <summary>The slider drag ended: fold everything since the first Live into one undo step.</summary>
    Commit,
}

/// <summary>
/// Properties panel for the selected annotation: click-to-pick colour swatches and inline sliders.
/// Replaced a PropertyGrid (D18), which hid the colour palette and sliders behind small drop-down arrows.
/// It never touches the project itself: it raises <see cref="Edit"/> and MainForm routes that through
/// ProjectSession, so every change is undoable (rule 2).
/// </summary>
public sealed class AnnotationStylePanel : PropertySheet
{
    static readonly Color Fg = Theme.Text;
    static readonly Color InputBg = Color.FromArgb(46, 46, 54);

    // Remembered when the fill / background is switched off, so ticking it again restores them.
    static readonly Color DefaultBoxFill = Color.FromArgb(70, 255, 214, 0);
    static readonly Color DefaultTextBg = Color.FromArgb(176, 0, 0, 0);

    AnnotationKind? _builtFor;
    Annotation _current = new();
    bool _loading;
    Color _fill;   // full-alpha fill / background colour (kept even while "Filled" is off)
    int _fillPct;  // fill / background opacity 0–100

    // Controls (only those for the current kind are created).
    Swatch? _colour, _fillColour;
    Slider? _thickness, _fontSize, _fillOpacity;
    Theme.ToggleSwitch? _filled;
    NumericUpDown? _duration, _fadeIn, _fadeOut;
    RoundTextBox? _text;
    Theme.DarkCombo? _font;

    /// <summary>Raised with a change to apply to the selected annotation.</summary>
    public event Action<Action<Annotation>, StyleEditPhase>? Edit;

    public AnnotationStylePanel() : base("annotation") { }

    /// <summary>Show the values of <paramref name="a"/>. Cheap: rebuilds controls only when the kind changes.</summary>
    public void ShowAnnotation(Annotation a)
    {
        _current = a.Clone();
        _loading = true;
        try
        {
            if (_builtFor != a.Kind) Build(a.Kind);

            var fallback = a.Kind == AnnotationKind.Text ? DefaultTextBg : DefaultBoxFill;
            if (a.FillColor is not null || _builtFor != a.Kind)
            {
                var f = ColorHex.Parse(a.FillColor, fallback);
                _fill = Color.FromArgb(255, f);
                _fillPct = (int)Math.Round(f.A / 2.55);
            }
            _builtFor = a.Kind;

            _colour?.SetColour(ColorHex.Parse(a.StrokeColor, a.Kind == AnnotationKind.Text ? Color.White : Color.Red));
            _thickness?.SetValue((int)Math.Round(a.StrokeWidth));
            _fontSize?.SetValue((int)Math.Round(a.FontSize));
            if (_filled is not null) _filled.Checked = a.FillColor is not null;
            _fillColour?.SetColour(_fill);
            _fillOpacity?.SetValue(_fillPct);
            if (_duration is not null)
                _duration.Value = Math.Clamp((decimal)Math.Round((a.End - a.Start).TotalSeconds, 1), _duration.Minimum, _duration.Maximum);
            if (_fadeIn is not null) _fadeIn.Value = Math.Clamp((decimal)Math.Round(a.FadeIn, 2), _fadeIn.Minimum, _fadeIn.Maximum);
            if (_fadeOut is not null) _fadeOut.Value = Math.Clamp((decimal)Math.Round(a.FadeOut, 2), _fadeOut.Minimum, _fadeOut.Maximum);
            if (_text is not null && !_text.Box.Focused) _text.Box.Text = a.Text;
            if (_font is not null && !_font.DroppedDown)
            {
                if (!_font.Items.Contains(a.FontFamily)) _font.Items.Insert(0, a.FontFamily);
                _font.SelectedItem = a.FontFamily;
            }
        }
        finally { _loading = false; }
    }

    // ---------------- Building ----------------

    void Build(AnnotationKind kind)
    {
        SuspendLayout();
        ClearGroups();
        _colour = _fillColour = null;
        _thickness = _fontSize = _fillOpacity = null;
        _filled = null;
        _duration = _fadeIn = _fadeOut = null;
        _text = null;
        _font = null;

        if (kind == AnnotationKind.Text)
        {
            Group("Text");
            _text = new RoundTextBox { Dock = DockStyle.Fill, Height = LogicalToDeviceUnits(30) };
            _text.Box.TextAlign = HorizontalAlignment.Left;
            _text.Box.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; CommitText(); } };
            _text.Box.Leave += (_, _) => CommitText();
            Row("Text", _text);

            _font = new Theme.DarkCombo { Dock = DockStyle.Fill, IntegralHeight = false, MaxDropDownItems = 20 };
            _font.Items.AddRange([.. FontFamily.Families.Select(f => (object)f.Name)]);
            _font.SelectedIndexChanged += (_, _) =>
            {
                if (_font.SelectedItem is string name) Raise(a => a.FontFamily = name);
            };
            Row("Font", _font);

            _fontSize = new Slider(8, 200, "px");
            _fontSize.Changed += (v, phase) => Raise(a => a.FontSize = v, phase);
            Row("Size", _fontSize);

            _colour = new Swatch("Text colour");
            _colour.Picked += c => Raise(a => a.StrokeColor = ColorHex.ToHex(c));
            Row("Colour", _colour);

            Group("Background");
            AddFillRows("Background");
        }
        else
        {
            Group("Line");
            _colour = new Swatch("Line colour");
            _colour.Picked += c => Raise(a => a.StrokeColor = ColorHex.ToHex(c));
            Row("Colour", _colour);

            _thickness = new Slider(1, 60, "px");
            _thickness.Changed += (v, phase) => Raise(a => a.StrokeWidth = v, phase);
            Row("Thickness", _thickness);

            if (kind == AnnotationKind.Box)
            {
                Group("Fill");
                AddFillRows("Filled");
            }
        }

        Group("Timing");
        _duration = new NumericUpDown
        {
            Minimum = 0.1m, Maximum = 3600, DecimalPlaces = 1, Increment = 0.5m,
            BackColor = InputBg, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, Width = 80,
        };
        _duration.ValueChanged += (_, _) =>
        {
            var secs = (double)_duration.Value;
            Raise(a => a.End = a.Start + TimeSpan.FromSeconds(secs)); // TimelineOps clamps to the clip on display
        };
        Row("Duration (s)", _duration);

        NumericUpDown FadeBox(Action<Annotation, double> set)
        {
            var box = new NumericUpDown
            {
                Minimum = 0, Maximum = (decimal)TimelineOps.MaxFade, DecimalPlaces = 2, Increment = 0.25m,
                BackColor = InputBg, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, Width = 80,
            };
            box.ValueChanged += (_, _) => { var v = (double)box.Value; Raise(a => set(a, v)); };
            return box;
        }
        _fadeIn = FadeBox((a, v) => a.FadeIn = v);
        _fadeOut = FadeBox((a, v) => a.FadeOut = v);
        Row("Fade in (s)", _fadeIn);
        Row("Fade out (s)", _fadeOut);

        ResumeLayout();
        PerformLayout();
    }

    /// <summary>Fill (box) or background (text) rows. Picking a colour or opacity switches the fill on.</summary>
    void AddFillRows(string toggleLabel)
    {
        _filled = new Theme.ToggleSwitch { Text = "On" };
        _filled.CheckedChanged += (_, _) =>
        {
            var on = _filled.Checked;
            Raise(a => a.FillColor = on ? FillHex() : null);
        };
        Row(toggleLabel, _filled);

        _fillColour = new Swatch("Fill colour");
        _fillColour.Picked += c =>
        {
            _fill = c;
            Raise(a => a.FillColor = FillHex());
        };
        Row("Colour", _fillColour);

        _fillOpacity = new Slider(0, 100, "%");
        _fillOpacity.Changed += (v, phase) =>
        {
            _fillPct = v;
            Raise(a => a.FillColor = FillHex(), phase);
        };
        Row("Opacity", _fillOpacity);
    }

    string FillHex() => ColorHex.ToHex(Color.FromArgb((int)Math.Round(Math.Clamp(_fillPct, 0, 100) * 2.55), _fill));

    void CommitText()
    {
        if (_text is null || _loading) return;
        var t = _text.Box.Text;
        if (!string.IsNullOrWhiteSpace(t) && t != _current.Text) Raise(a => a.Text = t);
    }

    void Raise(Action<Annotation> edit, StyleEditPhase phase = StyleEditPhase.Single)
    {
        if (_loading) return;
        Edit?.Invoke(edit, phase);
    }

    // ---------------- Editors ----------------

    /// <summary>A colour swatch button: click it to open the colour palette.</summary>
    sealed class Swatch : Button
    {
        readonly string _title;
        Color _colour;
        public event Action<Color>? Picked;

        public Swatch(string title)
        {
            _title = title;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Height = LogicalToDeviceUnits(26);
            Dock = DockStyle.Fill;
            Cursor = Cursors.Hand;
            TextAlign = ContentAlignment.MiddleCenter;
            Font = new Font("Consolas", 8.5f);
        }

        public void SetColour(Color c)
        {
            _colour = Color.FromArgb(255, c);
            ForeColor = _colour.GetBrightness() > 0.55f ? Color.Black : Color.White;
            Text = $"#{_colour.R:X2}{_colour.G:X2}{_colour.B:X2}";
            Invalidate();
        }

        bool _hover;
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Panel);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float radius = LogicalToDeviceUnits(5);
            Theme.FillRounded(g, _colour, r, radius);
            Theme.DrawRounded(g, _hover ? Color.White : Color.FromArgb(90, 90, 100), 1, r, radius);
            TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            using var dlg = new ColorDialog { Color = _colour, FullOpen = true, AnyColor = true };
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            SetColour(dlg.Color);
            Picked?.Invoke(_colour);
        }

        public override string ToString() => _title;
    }
}
