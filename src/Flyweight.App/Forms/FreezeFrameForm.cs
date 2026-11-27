using Flyweight.Core.Editing;
using Flyweight.Core.Model;

namespace Flyweight.App.Forms;

/// <summary>"Freeze this frame": how long to hold it.</summary>
public sealed class FreezeFrameForm : Form
{
    readonly NumericUpDown _seconds = new()
    {
        Minimum = 0.5m,
        Maximum = (decimal)SourceMedia.StillMaxDuration.TotalSeconds,
        DecimalPlaces = 1,
        Increment = 0.5m,
        Value = (decimal)TimelineOps.DefaultFreezeLength.TotalSeconds,
        Width = 80,
    };

    readonly CheckBox _copyAnnotations = new()
    {
        Text = "Include the arrows, boxes and text on screen (for the whole hold)", Checked = true, AutoSize = true,
    };

    public TimeSpan Length => TimeSpan.FromSeconds((double)_seconds.Value);
    public bool CopyAnnotations => _copyAnnotations.Checked;

    public FreezeFrameForm()
    {
        Text = "Freeze frame";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);

        var hint = new Label
        {
            AutoSize = true,
            ForeColor = Color.DimGray,
            Text = "Holds the frame at the playhead, then the clip carries on.",
        };
        var ok = new Button { Text = "Freeze", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };

        var layout = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label { Text = "Hold for", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(_seconds, 1, 0);
        layout.Controls.Add(new Label { Text = "seconds", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 0);
        layout.Controls.Add(_copyAnnotations, 0, 1);
        layout.SetColumnSpan(_copyAnnotations, 3);
        layout.Controls.Add(hint, 0, 2);
        layout.SetColumnSpan(hint, 3);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([cancel, ok]);
        layout.Controls.Add(buttons, 0, 3);
        layout.SetColumnSpan(buttons, 3);
        Controls.Add(layout);

        AcceptButton = ok;
        CancelButton = cancel;
        Shown += (_, _) => { _seconds.Select(); _seconds.Select(0, _seconds.Text.Length); };
    }
}
