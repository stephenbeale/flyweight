using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.App.Forms;

/// <summary>
/// "New project": name, folder, output size and whether portrait clips get blurred bars. Create saves the empty project there straight away,
/// so New always does something visible (the title shows the new name), even when the current
/// project was already empty. The output size becomes the export dialog's default.
/// </summary>
public sealed class NewProjectForm : Form
{
    readonly TextBox _name = new() { Width = 260 };
    readonly TextBox _folder = new() { Width = 260 };
    readonly ComboBox _resolution = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };

    readonly ComboBox _shape = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    readonly CheckBox _blur = new() { Text = "Fill the bars around clips of another shape with a blurred copy", AutoSize = true, Checked = true };

    public ExportResolution OutputResolution => ExportResolutions.All[_resolution.SelectedIndex].Value;

    /// <summary>Landscape 16:9, or vertical 9:16 for Shorts (D41).</summary>
    public CanvasShape Shape => _shape.SelectedIndex == 1 ? CanvasShape.Vertical : CanvasShape.Landscape;

    /// <summary>What fills the bars around clips that aren't 16:9 (D40). On by default.</summary>
    public BackgroundFill Background => _blur.Checked ? BackgroundFill.Blur : BackgroundFill.Black;

    /// <summary>Full path of the project file to create (valid once the dialog returns OK).</summary>
    /// <remarks>Inside a folder of its own named after the project, with a Thumbnails folder beside it (<see cref="ProjectFolders"/>).</remarks>
    public string ProjectPath => ProjectFolders.NewProjectPath(_folder.Text.Trim(), _name.Text.Trim());

    /// <summary>Projects go in Documents\Flyweight unless the current project lives somewhere else (then beside its folder).</summary>
    public static string DefaultFolder(string? currentProjectPath) =>
        ProjectFolders.DefaultParent(currentProjectPath, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Flyweight"));

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
        _shape.Items.AddRange(["Landscape 16:9 (YouTube video)", "Vertical 9:16 (YouTube Shorts)"]);
        _shape.SelectedIndex = 0;
        // The size labels follow the shape, so "1080p vertical (1080x1920)" says exactly what you'll get.
        void Relabel()
        {
            int keep = _resolution.SelectedIndex;
            _resolution.Items.Clear();
            foreach (var label in ExportResolutions.Labels(Shape)) _resolution.Items.Add(label);
            _resolution.SelectedIndex = Math.Max(0, keep);
        }
        _shape.SelectedIndexChanged += (_, _) => Relabel();

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
            Text = "Makes a folder named after the project, with Thumbnails and Freeze frames folders inside.\nYou can still pick a different size when exporting.",
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
        Row(2, "Shape", _shape);
        Row(3, "Output size", _resolution);
        layout.Controls.Add(hint, 1, 4);
        layout.SetColumnSpan(hint, 2);
        layout.Controls.Add(_blur, 1, 5);
        layout.SetColumnSpan(_blur, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([cancel, ok]);
        layout.Controls.Add(buttons, 0, 6);
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
            // Taken if either the new layout or an old loose project file uses the name.
            if (!File.Exists(ProjectFolders.NewProjectPath(folder, name)) && !File.Exists(Path.Combine(folder, name + ProjectSerializer.FileExtension)))
                return name;
        }
    }
}
