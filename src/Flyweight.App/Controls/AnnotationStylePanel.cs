using Flyweight.App.Rendering;
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
public sealed class AnnotationStylePanel : UserControl
{
    static readonly Color Bg = Color.FromArgb(28, 28, 32);
    static readonly Color Fg = Color.Gainsboro;
    static readonly Color InputBg = Color.FromArgb(45, 45, 52);

    // Remembered when the fill / background is switched off, so ticking it again restores them.
    static readonly Color DefaultBoxFill = Color.FromArgb(70, 255, 214, 0);
    static readonly Color DefaultTextBg = Color.FromArgb(176, 0, 0, 0);

    readonly TableLayoutPanel _table = new()
    {
        Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(8, 6, 8, 6), BackColor = Bg,
    };

    AnnotationKind? _builtFor;
    Annotation _current = new();
    bool _loading;
    Color _fill;   // full-alpha fill / background colour (kept even while "Filled" is off)
    int _fillPct;  // fill / background opacity 0–100

    // Controls (only those for the current kind are created).
    Swatch? _colour, _fillColour;
    Slider? _thickness, _fontSize, _fillOpacity;
    CheckBox? _filled;
    NumericUpDown? _duration;
    TextBox? _text;
    ComboBox? _font;

    /// <summary>Raised with a change to apply to the selected annotation.</summary>
    public event Action<Action<Annotation>, StyleEditPhase>? Edit;

    public AnnotationStylePanel()
    {
        BackColor = Bg;
        ForeColor = Fg;
        AutoScroll = true;
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(_table);
    }

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
            if (_text is not null && !_text.Focused) _text.Text = a.Text;
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
        _table.SuspendLayout();
        foreach (Control c in _table.Controls) c.Dispose();
        _table.Controls.Clear();
        _table.RowStyles.Clear();
        _table.RowCount = 0;
        _colour = _fillColour = null;
        _thickness = _fontSize = _fillOpacity = null;
        _filled = null;
        _duration = null;
        _text = null;
        _font = null;

        if (kind == AnnotationKind.Text)
        {
            Section("Text");
            _text = new TextBox { BackColor = InputBg, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, Dock = DockStyle.Fill };
            _text.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; CommitText(); } };
            _text.Leave += (_, _) => CommitText();
            Row("Text", _text);

            _font = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, BackColor = InputBg, ForeColor = Fg, FlatStyle = FlatStyle.Flat,
                Dock = DockStyle.Fill, IntegralHeight = false, MaxDropDownItems = 20,
            };
            _font.Items.AddRange(FontFamily.Families.Select(f => (object)f.Name).ToArray());
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

            Section("Background");
            AddFillRows("Background");
        }
        else
        {
            Section("Line");
            _colour = new Swatch("Line colour");
            _colour.Picked += c => Raise(a => a.StrokeColor = ColorHex.ToHex(c));
            Row("Colour", _colour);

            _thickness = new Slider(1, 60, "px");
            _thickness.Changed += (v, phase) => Raise(a => a.StrokeWidth = v, phase);
            Row("Thickness", _thickness);

            if (kind == AnnotationKind.Box)
            {
                Section("Fill");
                AddFillRows("Filled");
            }
        }

        Section("Timing");
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

        _table.ResumeLayout();
    }

    /// <summary>Fill (box) or background (text) rows. Picking a colour or opacity switches the fill on.</summary>
    void AddFillRows(string toggleLabel)
    {
        _filled = new CheckBox { Text = "On", ForeColor = Fg, AutoSize = true };
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
        var t = _text.Text;
        if (!string.IsNullOrWhiteSpace(t) && t != _current.Text) Raise(a => a.Text = t);
    }

    void Raise(Action<Annotation> edit, StyleEditPhase phase = StyleEditPhase.Single)
    {
        if (_loading) return;
        Edit?.Invoke(edit, phase);
    }

    void Section(string title)
    {
        var l = new Label
        {
            Text = title.ToUpperInvariant(), ForeColor = Color.Gray, AutoSize = true,
            Font = new Font(Font.FontFamily, 7.5f, FontStyle.Bold), Margin = new Padding(0, _table.RowCount == 0 ? 2 : 12, 0, 2),
        };
        _table.RowCount++;
        _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _table.Controls.Add(l, 0, _table.RowCount - 1);
        _table.SetColumnSpan(l, 2);
    }

    void Row(string label, Control editor)
    {
        _table.RowCount++;
        _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var l = new Label { Text = label, ForeColor = Fg, AutoSize = false, Height = 28, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        editor.Margin = new Padding(0, 3, 0, 3);
        _table.Controls.Add(l, 0, _table.RowCount - 1);
        _table.Controls.Add(editor, 1, _table.RowCount - 1);
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
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderColor = Color.FromArgb(90, 90, 100);
            Height = 24;
            Dock = DockStyle.Fill;
            Cursor = Cursors.Hand;
            TextAlign = ContentAlignment.MiddleCenter;
            Font = new Font("Consolas", 8.5f);
        }

        public void SetColour(Color c)
        {
            _colour = Color.FromArgb(255, c);
            BackColor = _colour;
            FlatAppearance.MouseOverBackColor = _colour;
            FlatAppearance.MouseDownBackColor = _colour;
            ForeColor = _colour.GetBrightness() > 0.55f ? Color.Black : Color.White;
            Text = $"#{_colour.R:X2}{_colour.G:X2}{_colour.B:X2}";
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

    /// <summary>
    /// Slider with a value read-out. Mouse drags report Live then Commit (one undo step);
    /// keyboard / wheel changes report Single.
    /// </summary>
    sealed class Slider : Panel
    {
        readonly TrackBar _bar;
        readonly Label _value;
        readonly string _unit;
        bool _setting, _mouseDown, _movedWhileDown;
        public event Action<int, StyleEditPhase>? Changed;

        public Slider(int min, int max, string unit)
        {
            _unit = unit;
            Height = 30;
            Dock = DockStyle.Fill;
            _value = new Label { Dock = DockStyle.Right, Width = 52, TextAlign = ContentAlignment.MiddleRight, ForeColor = Fg };
            _bar = new TrackBar
            {
                Dock = DockStyle.Fill, Minimum = min, Maximum = max, TickStyle = TickStyle.None, AutoSize = false, Height = 30,
                SmallChange = 1, LargeChange = Math.Max(1, (max - min) / 10), BackColor = Bg,
            };
            _bar.ValueChanged += (_, _) =>
            {
                _value.Text = $"{_bar.Value} {_unit}";
                if (_setting) return;
                if (_mouseDown) { _movedWhileDown = true; Changed?.Invoke(_bar.Value, StyleEditPhase.Live); }
                else Changed?.Invoke(_bar.Value, StyleEditPhase.Single);
            };
            _bar.MouseDown += (_, _) => { _mouseDown = true; _movedWhileDown = false; };
            _bar.MouseUp += (_, _) =>
            {
                _mouseDown = false;
                if (_movedWhileDown) Changed?.Invoke(_bar.Value, StyleEditPhase.Commit);
            };
            Controls.Add(_bar);
            Controls.Add(_value);
        }

        public void SetValue(int v)
        {
            if (_mouseDown) return; // don't fight the user's drag
            _setting = true;
            _bar.Value = Math.Clamp(v, _bar.Minimum, _bar.Maximum);
            _value.Text = $"{_bar.Value} {_unit}";
            _setting = false;
        }
    }
}
