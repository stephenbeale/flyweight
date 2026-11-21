using Flyweight.Core.Export;
using Flyweight.Core.Model;

namespace Flyweight.App.Forms;

/// <summary>
/// Shows the YouTube chapter list built from the markers, with YouTube's rules checked,
/// and lets you copy it to the clipboard or save it as a .txt next to your footage.
/// </summary>
public sealed class ChaptersForm : Form
{
    readonly ChapterList _chapters;
    readonly string _defaultTxt;
    readonly Label _status = new() { AutoSize = true, ForeColor = Color.DimGray };

    public ChaptersForm(Project project, string? projectFilePath)
    {
        _chapters = YouTubeChapters.Build(project);
        _defaultTxt = YouTubeChapters.DefaultTextPath(project, projectFilePath);

        Text = "YouTube chapters";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);

        var intro = new Label
        {
            AutoSize = true, MaximumSize = new Size(440, 0),
            Text = "Paste these into your YouTube video's description. YouTube turns them into chapters automatically.",
        };
        var text = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = 440, Height = 200,
            Font = new Font("Consolas", 10.5f), Text = _chapters.Text, BackColor = SystemColors.Window,
        };

        var check = new Label { AutoSize = true, MaximumSize = new Size(440, 0) };
        if (_chapters.YouTubeWillShowChapters)
        {
            check.Text = "✔ Meets YouTube's chapter rules.";
            check.ForeColor = Color.ForestGreen;
        }
        else
        {
            check.Text = "⚠ YouTube won't show these as chapters yet:\n• " + string.Join("\n• ", _chapters.Warnings);
            check.ForeColor = Color.DarkOrange;
        }

        var copy = new Button { Text = "Copy to clipboard", AutoSize = true, Enabled = _chapters.Text.Length > 0 };
        var save = new Button { Text = "Save .txt…", AutoSize = true, Enabled = _chapters.Text.Length > 0 };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        copy.Click += (_, _) => { Clipboard.SetText(_chapters.Text); _status.Text = "Copied. Paste it into your YouTube description."; };
        save.Click += (_, _) => SaveTxt();

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        buttons.Controls.AddRange(new Control[] { close, save, copy });

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        foreach (var c in new Control[] { intro, text, check, _status, buttons }) layout.Controls.Add(c);
        Controls.Add(layout);
        CancelButton = close;
        AcceptButton = copy;
    }

    void SaveTxt()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "Text file|*.txt",
            InitialDirectory = Path.GetDirectoryName(_defaultTxt),
            FileName = Path.GetFileName(_defaultTxt),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dlg.FileName, _chapters.Text + Environment.NewLine);
            _status.Text = $"Saved {Path.GetFileName(dlg.FileName)}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Couldn't save", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
