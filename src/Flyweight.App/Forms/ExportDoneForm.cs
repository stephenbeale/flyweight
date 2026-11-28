using System.Diagnostics;
using Flyweight.App.Controls;

namespace Flyweight.App.Forms;

/// <summary>
/// "Your video is ready", shown once after a successful export. Our own dark dialog (it replaced a TaskDialog, whose
/// buttons can't be coloured) so the support button can be yellow. "Show file and open YouTube Studio" is the default
/// (Enter); the coffee button opens its page and leaves the dialog open.
/// </summary>
public sealed class ExportDoneForm : Form
{
    /// <summary>True if the user chose Open YouTube Studio (the caller opens Studio, Explorer and the details file).</summary>
    public bool OpenStudio { get; private set; }

    static readonly Color Yellow = Color.FromArgb(255, 210, 31); // the icon's yellow

    public ExportDoneForm(string output, string details, string instructions, string coffeeUrl)
    {
        Text = "Export complete";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Window;
        ForeColor = Theme.Text;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(LogicalToDeviceUnits(20), LogicalToDeviceUnits(16), LogicalToDeviceUnits(20), LogicalToDeviceUnits(14));
        int wrap = LogicalToDeviceUnits(460);

        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill, BackColor = Theme.Window,
        };

        var heading = new Label
        {
            Text = "✔  Your video is ready", AutoSize = true, ForeColor = Color.FromArgb(110, 220, 130),
            Font = new Font(Font.FontFamily, 13f, FontStyle.Bold), Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(8)),
        };
        var file = new Label
        {
            Text = Path.GetFileName(output), AutoSize = true, MaximumSize = new Size(wrap, 0), ForeColor = Color.White,
            Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0),
        };
        var detailLabel = new Label { Text = details, AutoSize = true, ForeColor = Color.Silver, Margin = new Padding(0, 2, 0, LogicalToDeviceUnits(10)) };
        var body = new Label { Text = instructions, AutoSize = true, MaximumSize = new Size(wrap, 0), Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(14)) };

        // One button does both (owner, 2026-11-28): "Open YouTube Studio" already selected the file in Explorer, so a
        // separate "Show in Explorer" next to it read as a second, different step.
        var studio = Button("Show file and open YouTube Studio", 280);
        studio.Primary = true;
        studio.Click += (_, _) => { OpenStudio = true; Close(); };
        var close = Button("Close", 90);
        close.Click += (_, _) => Close();
        var buttons = Row(studio, close);

        // Support: only here, only after a successful export, never a pop-up (see DECISIONS "Support link").
        var divider = new Panel { Height = 1, Width = wrap, BackColor = Theme.Border, Margin = new Padding(0, LogicalToDeviceUnits(16), 0, LogicalToDeviceUnits(12)) };
        var enjoying = new Label
        {
            Text = "Enjoying Flyweight?", AutoSize = true, ForeColor = Color.Silver,
            Margin = new Padding(0, LogicalToDeviceUnits(9), LogicalToDeviceUnits(12), 0),
        };
        var coffee = Button("☕  Buy Steve a coffee", 200);
        coffee.Tint = Yellow;
        coffee.Font = new Font(Font, FontStyle.Bold);
        new ToolTip().SetToolTip(coffee, "Opens Buy Me a Coffee in your browser.");
        coffee.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(coffeeUrl) { UseShellExecute = true }); }
            catch { /* no browser: nothing else to do */ }
        };
        var support = Row(enjoying, coffee);

        stack.Controls.AddRange([heading, file, detailLabel, body, buttons, divider, support]);
        Controls.Add(stack);
        AcceptButton = studio;
        CancelButton = close;
        Shown += (_, _) => studio.Focus();
    }

    Theme.RoundButton Button(string text, int width) => new()
    {
        Text = text, Width = LogicalToDeviceUnits(width), Height = LogicalToDeviceUnits(36),
        Margin = new Padding(0, 0, LogicalToDeviceUnits(8), 0),
    };

    static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0), BackColor = Theme.Window,
        };
        row.Controls.AddRange(controls);
        return row;
    }
}
