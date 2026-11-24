using System.Globalization;
using Flyweight.Core.Editing;

namespace Flyweight.App.Controls;

/// <summary>
/// Clip speed: type any value ("1.08", "1.08x", "108%"; Enter or clicking away applies it, clamped to 0.25–4),
/// press ↑/↓ for the next/previous preset, or pick a preset from the ▾ menu. Raises <see cref="Picked"/> once per change.
/// </summary>
sealed class SpeedBox : Panel
{
    readonly RoundTextBox _box = new() { Dock = DockStyle.Fill };
    readonly Theme.RoundButton _menuButton = new() { Text = "▾", Dock = DockStyle.Right };
    readonly ContextMenuStrip _menu = new() { Renderer = new Theme.ToolRenderer() };
    double _value = 1;

    public event Action<double>? Picked;

    public SpeedBox()
    {
        Height = LogicalToDeviceUnits(30);
        Width = LogicalToDeviceUnits(110);
        _box.Box.TextAlign = HorizontalAlignment.Left;
        _menuButton.Width = LogicalToDeviceUnits(30);
        new ToolTip().SetToolTip(_box.Box, "Type any speed, e.g. 1.08 (0.25 to 4). ↑/↓ step through the presets.");

        foreach (var sp in TimelineOps.SpeedPresets)
        {
            var v = sp;
            _menu.Items.Add(Format(v), null, (_, _) => Apply(v));
        }
        _menuButton.Click += (_, _) => _menu.Show(_menuButton, new Point(0, _menuButton.Height));

        var box = _box.Box;
        box.Enter += (_, _) => BeginInvoke(box.SelectAll);
        box.KeyDown += (_, e) =>
        {
            switch (e.KeyCode)
            {
                case Keys.Enter: e.SuppressKeyPress = true; CommitTyped(); break;
                case Keys.Escape: e.SuppressKeyPress = true; Show(_value); break;
                case Keys.Up: e.SuppressKeyPress = true; Step(+1); break;
                case Keys.Down: e.SuppressKeyPress = true; Step(-1); break;
            }
        };
        box.Leave += (_, _) => CommitTyped();

        Controls.Add(_box);
        Controls.Add(new Panel { Dock = DockStyle.Right, Width = LogicalToDeviceUnits(4) });
        Controls.Add(_menuButton);
        Show(1);
    }

    static string Format(double v) => $"{v:0.##}×";

    /// <summary>Show a value without raising <see cref="Picked"/> (leaves the box alone while it's being typed in).</summary>
    public void SetValue(double v)
    {
        _value = v;
        if (!_box.Box.Focused) Show(v);
    }

    public new bool Enabled
    {
        get => _box.Box.Enabled;
        set { _box.SetEnabled(value); _menuButton.Enabled = value; }
    }

    void Show(double v) => _box.Box.Text = Format(v);

    /// <summary>"1.08", "1,08", "1.08x", "1.08×" and "108%" all work; junk is ignored.</summary>
    internal static double? Parse(string text)
    {
        var t = text.Trim().TrimEnd('x', 'X', '×').Trim();
        bool percent = t.EndsWith('%');
        if (percent) t = t.TrimEnd('%').Trim();
        if (!double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d) || d <= 0)
            return null;
        if (percent) d /= 100;
        return Math.Round(Math.Clamp(d, TimelineOps.MinSpeed, TimelineOps.MaxSpeed), 3);
    }

    void CommitTyped()
    {
        if (Parse(_box.Box.Text) is { } v) Apply(v);
        else Show(_value);
    }

    void Step(int dir)
    {
        var presets = TimelineOps.SpeedPresets;
        double next = dir > 0
            ? presets.FirstOrDefault(p => p > _value + 1e-6, presets[^1])
            : presets.LastOrDefault(p => p < _value - 1e-6, presets[0]);
        Apply(next);
        _box.Box.SelectAll();
    }

    void Apply(double v)
    {
        Show(v);
        if (Math.Abs(v - _value) < 1e-6) return;
        _value = v;
        Picked?.Invoke(v);
    }
}
