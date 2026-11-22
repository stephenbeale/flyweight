namespace Flyweight.App.Forms;

/// <summary>Tiny prompt for a chapter marker's title. Enter = OK, Esc = cancel.</summary>
public sealed class ChapterTitleForm : Form
{
    readonly TextBox _title = new() { Width = 320 };

    public string ChapterTitle => _title.Text.Trim();

    public ChapterTitleForm(string initial, string heading = "Chapter title")
    {
        Text = heading;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        _title.Text = initial;
        var hint = new Label { AutoSize = true, ForeColor = Color.DimGray, Text = "Shown in the YouTube description, e.g. \"Setup\" or \"Results\"." };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        buttons.Controls.AddRange(new Control[] { cancel, ok });

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(_title);
        layout.Controls.Add(hint);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        AcceptButton = ok;
        CancelButton = cancel;
        Shown += (_, _) => { _title.Focus(); _title.SelectAll(); };
    }
}
