using System.Globalization;

namespace Flyweight.App.Controls;

/// <summary>
/// Slider with a value box, shared by the properties panels. Mouse drags report Live then Commit (one undo
/// step); keyboard / focused-wheel changes and a number typed into the box (Enter, or clicking away) report Single.
/// The track is drawn by hand (thin rounded track, accent fill, round thumb) to match the dark theme.
/// </summary>
sealed class Slider : Panel
{
    readonly Track _bar;
    readonly RoundTextBox _value;
    readonly string _unit;
    public event Action<int, StyleEditPhase>? Changed;

    public Slider(int min, int max, string unit)
    {
        _unit = unit;
        Height = LogicalToDeviceUnits(30);
        Dock = DockStyle.Fill;
        // The read-out doubles as an input: click it and type a number.
        _value = new RoundTextBox { Dock = DockStyle.Right, Width = LogicalToDeviceUnits(42) };
        _bar = new Track(min, max) { Dock = DockStyle.Fill };
        _bar.ValueChanged += phase =>
        {
            if (!_value.Box.Focused) ShowValue();
            if (phase is { } p) Changed?.Invoke(_bar.Value, p);
        };

        var box = _value.Box;
        box.Enter += (_, _) => { box.Text = _bar.Value.ToString(CultureInfo.CurrentCulture); BeginInvoke(box.SelectAll); };
        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; CommitTyped(); _bar.Focus(); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; ShowValue(); _bar.Focus(); }
        };
        box.Leave += (_, _) => CommitTyped();

        Controls.Add(_bar);
        Controls.Add(new Panel { Dock = DockStyle.Right, Width = LogicalToDeviceUnits(4) });
        Controls.Add(_value);
        ShowValue();
    }

    /// <summary>Apply a typed number: "150", "150 %", "-20" all work; out of range is clamped; junk is ignored.</summary>
    void CommitTyped()
    {
        var text = _value.Box.Text.Replace(_unit, "", StringComparison.OrdinalIgnoreCase).Replace("×", "").Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var d) && double.IsFinite(d))
            _bar.SetValue((int)Math.Round(d), StyleEditPhase.Single); // one undo step
        ShowValue();
    }

    void ShowValue() => _value.Box.Text = $"{_bar.Value}{_unit}"; // "100%", "12px": no space, so the narrow box fits

    public new bool Enabled
    {
        get => _bar.Enabled;
        set { _bar.Enabled = value; _value.SetEnabled(value); }
    }

    public void SetValue(int v)
    {
        if (_bar.Dragging || _value.Box.Focused) return; // don't fight the user's drag or typing
        _bar.SetValue(v, null);
        ShowValue();
    }

    /// <summary>The track itself. Raises ValueChanged with the undo phase, or null for a programmatic set.</summary>
    sealed class Track : Control
    {
        readonly int _min, _max;
        int _value;
        bool _hover, _moved;
        public bool Dragging { get; private set; }
        public int Value => _value;
        public event Action<StyleEditPhase?>? ValueChanged;

        public Track(int min, int max)
        {
            _min = min;
            _max = Math.Max(min + 1, max);
            _value = min;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public void SetValue(int v, StyleEditPhase? phase)
        {
            v = Math.Clamp(v, _min, _max);
            if (v == _value) return;
            _value = v;
            Invalidate();
            ValueChanged?.Invoke(phase);
        }

        int Inset => LogicalToDeviceUnits(8);

        int ValueAt(int x)
        {
            float w = Math.Max(1, Width - Inset * 2);
            return _min + (int)Math.Round(Math.Clamp((x - Inset) / w, 0, 1) * (_max - _min));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Enabled) return;
            Focus();
            Dragging = true;
            _moved = false;
            Capture = true;
            int v = ValueAt(e.X);
            if (v != _value) { _moved = true; SetValue(v, StyleEditPhase.Live); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!Dragging) return;
            int v = ValueAt(e.X);
            if (v != _value) { _moved = true; SetValue(v, StyleEditPhase.Live); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!Dragging) return;
            Dragging = false;
            Capture = false;
            if (_moved) ValueChanged?.Invoke(StyleEditPhase.Commit);
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override bool IsInputKey(Keys k) => k is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(k);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int big = Math.Max(1, (_max - _min) / 10);
            int? v = e.KeyCode switch
            {
                Keys.Left or Keys.Down => _value - 1,
                Keys.Right or Keys.Up => _value + 1,
                Keys.PageDown => _value - big,
                Keys.PageUp => _value + big,
                Keys.Home => _min,
                Keys.End => _max,
                _ => null,
            };
            if (v is { } nv) { e.Handled = true; e.SuppressKeyPress = true; SetValue(nv, StyleEditPhase.Single); }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            // Only when focused, so scrolling the panel past a slider doesn't change it.
            if (!Focused) { base.OnMouseWheel(e); return; }
            if (e is HandledMouseEventArgs h) h.Handled = true;
            SetValue(_value + Math.Sign(e.Delta), StyleEditPhase.Single);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            float inset = Inset, cy = Height / 2f, th = LogicalToDeviceUnits(4);
            float w = Math.Max(1, Width - inset * 2);
            float frac = (_value - _min) / (float)(_max - _min);
            float tx = inset + frac * w;
            var track = new RectangleF(inset, cy - th / 2, w, th);
            Theme.FillRounded(g, Color.FromArgb(58, 58, 68), track, th / 2);
            var accent = Enabled ? Theme.Accent : Color.FromArgb(80, 80, 90);
            if (tx - inset > 0.5f) Theme.FillRounded(g, accent, new RectangleF(inset, track.Y, tx - inset, th), th / 2);

            float r = LogicalToDeviceUnits(Dragging || _hover ? 7 : 6);
            var knob = new RectangleF(tx - r, cy - r, r * 2, r * 2);
            if (Focused && Enabled)
                Theme.FillRounded(g, Color.FromArgb(60, Theme.Accent), RectangleF.Inflate(knob, 3, 3), r + 3);
            Theme.FillRounded(g, Enabled ? Color.White : Color.FromArgb(110, 110, 120), knob, r);
            if (Enabled) Theme.DrawRounded(g, accent, 2, knob, r);
        }
    }
}

/// <summary>A borderless text box inside a rounded, dark field.</summary>
sealed class RoundTextBox : Panel
{
    public TextBox Box { get; }
    bool _enabled = true;

    public RoundTextBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Box = new TextBox
        {
            BorderStyle = BorderStyle.None, BackColor = Fill, ForeColor = Theme.Text, TextAlign = HorizontalAlignment.Right,
        };
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        Controls.Add(Box);
    }

    static readonly Color Fill = Color.FromArgb(46, 46, 54);

    public void SetEnabled(bool on)
    {
        _enabled = on;
        Box.Enabled = on;
        Box.ForeColor = on ? Theme.Text : Theme.Muted;
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int px = LogicalToDeviceUnits(5);
        int h = Box.PreferredHeight;
        Box.SetBounds(px, Math.Max(0, (Height - h) / 2), Math.Max(10, Width - px * 2), h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        var r = new RectangleF(0.5f, LogicalToDeviceUnits(3) + 0.5f, Width - 1.5f, Height - LogicalToDeviceUnits(6) - 1.5f);
        Theme.FillRounded(g, Fill, r, LogicalToDeviceUnits(5));
        Theme.DrawRounded(g, Box.Focused ? Theme.Accent : _enabled ? Color.FromArgb(66, 66, 76) : Color.FromArgb(52, 52, 60), 1, r,
            LogicalToDeviceUnits(5));
    }
}
