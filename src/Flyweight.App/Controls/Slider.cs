namespace Flyweight.App.Controls;

/// <summary>
/// Slider with a value read-out, shared by the properties panels. Mouse drags report Live then
/// Commit (one undo step); keyboard / wheel changes report Single.
/// </summary>
sealed class Slider : Panel
{
    static readonly Color Bg = Color.FromArgb(28, 28, 32);
    static readonly Color Fg = Color.Gainsboro;

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

    public new bool Enabled
    {
        get => _bar.Enabled;
        set { _bar.Enabled = value; _value.ForeColor = value ? Fg : Color.Gray; }
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
