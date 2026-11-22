using System.Globalization;

namespace Flyweight.App.Controls;

/// <summary>
/// Slider with a value box, shared by the properties panels. Mouse drags report Live then Commit (one undo
/// step); keyboard / wheel changes and a number typed into the box (Enter, or clicking away) report Single.
/// </summary>
sealed class Slider : Panel
{
    static readonly Color Bg = Color.FromArgb(28, 28, 32);
    static readonly Color Fg = Color.Gainsboro;
    static readonly Color BoxBg = Color.FromArgb(40, 40, 47);

    readonly TrackBar _bar;
    readonly TextBox _value;
    readonly string _unit;
    bool _setting, _mouseDown, _movedWhileDown;
    public event Action<int, StyleEditPhase>? Changed;

    public Slider(int min, int max, string unit)
    {
        _unit = unit;
        Height = 30;
        Dock = DockStyle.Fill;
        // The read-out doubles as an input: click it and type a number.
        _value = new TextBox
        {
            Dock = DockStyle.Right, Width = 56, TextAlign = HorizontalAlignment.Right, ForeColor = Fg, BackColor = BoxBg,
            BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(4, 4, 0, 4),
        };
        _bar = new TrackBar
        {
            Dock = DockStyle.Fill, Minimum = min, Maximum = max, TickStyle = TickStyle.None, AutoSize = false, Height = 30,
            SmallChange = 1, LargeChange = Math.Max(1, (max - min) / 10), BackColor = Bg,
        };
        _bar.ValueChanged += (_, _) =>
        {
            if (!_value.Focused) ShowValue();
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

        _value.Enter += (_, _) => { _value.Text = _bar.Value.ToString(CultureInfo.CurrentCulture); BeginInvoke(_value.SelectAll); };
        _value.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; CommitTyped(); _bar.Focus(); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; ShowValue(); _bar.Focus(); }
        };
        _value.Leave += (_, _) => CommitTyped();

        Controls.Add(_bar);
        Controls.Add(_value);
        ShowValue();
    }

    /// <summary>Apply a typed number: "150", "150 %", "-20" all work; out of range is clamped; junk is ignored.</summary>
    void CommitTyped()
    {
        var text = _value.Text.Replace(_unit, "", StringComparison.OrdinalIgnoreCase).Replace("×", "").Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var d) && double.IsFinite(d))
        {
            int v = (int)Math.Clamp(Math.Round(d), _bar.Minimum, _bar.Maximum);
            if (v != _bar.Value) _bar.Value = v; // raises Changed(Single): one undo step
        }
        ShowValue();
    }

    void ShowValue() => _value.Text = _unit.Length == 0 ? $"{_bar.Value}" : $"{_bar.Value} {_unit}";

    public new bool Enabled
    {
        get => _bar.Enabled;
        set { _bar.Enabled = _value.Enabled = value; _value.ForeColor = value ? Fg : Color.Gray; }
    }

    public void SetValue(int v)
    {
        if (_mouseDown || _value.Focused) return; // don't fight the user's drag or typing
        _setting = true;
        _bar.Value = Math.Clamp(v, _bar.Minimum, _bar.Maximum);
        ShowValue();
        _setting = false;
    }
}
