using Flyweight.Core.Media;

namespace Flyweight.App.Forms;

/// <summary>"New project": choose the output size. It becomes the export dialog's default.</summary>
public sealed class NewProjectForm : Form
{
    readonly ComboBox _resolution = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };

    public ExportResolution OutputResolution => ExportResolutions.All[_resolution.SelectedIndex].Value;

    public NewProjectForm()
    {
        Text = "New project";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);

        foreach (var (_, label) in ExportResolutions.All) _resolution.Items.Add(label);
        _resolution.SelectedIndex = 0;

        var hint = new Label
        {
            AutoSize = true,
            ForeColor = Color.DimGray,
            Text = "You can still pick a different size when exporting.",
        };
        var ok = new Button { Text = "Create", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };

        var layout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label { Text = "Output size", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(_resolution, 1, 0);
        layout.Controls.Add(hint, 0, 1);
        layout.SetColumnSpan(hint, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        buttons.Controls.AddRange(new Control[] { cancel, ok });
        layout.Controls.Add(buttons, 0, 2);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);

        AcceptButton = ok;
        CancelButton = cancel;
    }
}
