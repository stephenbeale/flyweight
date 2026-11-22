using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.App.Forms;

/// <summary>
/// "New project": name, folder and output size. Create saves the empty project there straight away,
/// so New always does something visible (the title shows the new name), even when the current
/// project was already empty. The output size becomes the export dialog's default.
/// </summary>
public sealed class NewProjectForm : Form
{
    readonly TextBox _name = new() { Width = 260 };
    readonly TextBox _folder = new() { Width = 260 };
    readonly ComboBox _resolution = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };

    public ExportResolution OutputResolution => ExportResolutions.All[_resolution.SelectedIndex].Value;

    /// <summary>Full path of the project file to create (valid once the dialog returns OK).</summary>
    public string ProjectPath => Path.Combine(_folder.Text.Trim(), _name.Text.Trim() + ProjectSerializer.FileExtension);

    /// <summary>Projects go in Documents\Flyweight unless the current project lives somewhere else.</summary>
    public static string DefaultFolder(string? currentProjectPath) =>
        currentProjectPath is not null && Path.GetDirectoryName(currentProjectPath) is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Flyweight");

    public NewProjectForm(string folder)
    {
        Text = "New project";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);

        _folder.Text = folder;
        _name.Text = UnusedName(folder, "My video");
        foreach (var (_, label) in ExportResolutions.All) _resolution.Items.Add(label);
        _resolution.SelectedIndex = 0;

        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = Directory.Exists(_folder.Text) ? _folder.Text : "", UseDescriptionForTitle = true, Description = "Save the project in" };
            if (dlg.ShowDialog(this) == DialogResult.OK) _folder.Text = dlg.SelectedPath;
        };
        var hint = new Label
        {
            AutoSize = true,
            ForeColor = Color.DimGray,
            Text = "You can still pick a different size when exporting.",
        };
        var ok = new Button { Text = "Create", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };

        var layout = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill };
        void Row(int r, string label, Control c, Control? extra = null)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, r);
            layout.Controls.Add(c, 1, r);
            if (extra is not null) layout.Controls.Add(extra, 2, r);
        }
        Row(0, "Name", _name);
        Row(1, "Save in", _folder, browse);
        Row(2, "Output size", _resolution);
        layout.Controls.Add(hint, 1, 3);
        layout.SetColumnSpan(hint, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        buttons.Controls.AddRange(new Control[] { cancel, ok });
        layout.Controls.Add(buttons, 0, 4);
        layout.SetColumnSpan(buttons, 3);
        Controls.Add(layout);

        AcceptButton = ok;
        CancelButton = cancel;
        Shown += (_, _) => { _name.Focus(); _name.SelectAll(); };
        FormClosing += (_, e) =>
        {
            if (DialogResult != DialogResult.OK) return;
            if (Problem() is { } problem)
            {
                MessageBox.Show(this, problem, "New project", MessageBoxButtons.OK, MessageBoxIcon.Information);
                e.Cancel = true;
            }
        };
    }

    string? Problem()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0) return "Give the project a name.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "A name can't contain any of these: \\ / : * ? \" < > |";
        if (_folder.Text.Trim().Length == 0) return "Choose a folder to save the project in.";
        if (File.Exists(ProjectPath)) return $"There's already a project called \"{name}\" in that folder. Pick another name, or use Open… to open it.";
        return null;
    }

    /// <summary>"My video", or "My video 2", "My video 3"… if that's taken in <paramref name="folder"/>.</summary>
    static string UnusedName(string folder, string stem)
    {
        for (int n = 1; ; n++)
        {
            var name = n == 1 ? stem : $"{stem} {n}";
            if (!File.Exists(Path.Combine(folder, name + ProjectSerializer.FileExtension))) return name;
        }
    }
}
