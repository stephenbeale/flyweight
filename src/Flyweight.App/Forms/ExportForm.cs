using System.Diagnostics;
using Flyweight.App.Rendering;
using Flyweight.Core.Export;
using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.App.Forms;

/// <summary>"Export for YouTube": pick a resolution and a file, watch the progress bar.</summary>
public sealed class ExportForm : Form
{
    readonly Project _project;
    readonly ComboBox _resolution = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    readonly TextBox _path = new() { Width = 330 };
    readonly Button _browse = new() { Text = "Browse…", AutoSize = true };
    readonly ProgressBar _progress = new() { Width = 430, Height = 18, Maximum = 1000 };
    readonly Label _status = new() { AutoSize = true, ForeColor = Color.DimGray, Text = "H.264 / AAC MP4, YouTube's recommended upload settings." };
    readonly Button _export = new() { Text = "Export", AutoSize = true };
    readonly Button _cancel = new() { Text = "Close", AutoSize = true };
    CancellationTokenSource? _cts;

    // YouTube details (D31): saved with the project and written beside the video as "<name> - YouTube.txt".
    readonly TextBox _ytTitle = new() { Width = 430 };
    readonly TextBox _ytDescription = new() { Width = 430, Height = 110, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
    readonly TextBox _ytTags = new() { Width = 430, PlaceholderText = "comma, separated, tags" };
    readonly CheckBox _ytSave = new() { Text = "Save these beside the video as a text file, ready to paste into YouTube Studio", AutoSize = true };
    readonly Label _ytInfo = new() { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(430, 0) };
    readonly Action<YouTubeDetails>? _saveDetails;

    /// <param name="saveDetails">Stores edited YouTube details in the project (through the session, so it's undoable and saved).</param>
    public ExportForm(Project project, string? suggestedPath, Action<YouTubeDetails>? saveDetails = null)
    {
        _project = project;
        _saveDetails = saveDetails;
        Text = "Export for YouTube";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);

        foreach (var (_, label) in ExportResolutions.All) _resolution.Items.Add(label);
        _resolution.SelectedIndex = (int)project.OutputResolution; // the size chosen at New project
        _path.Text = suggestedPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "My video.mp4");

        var layout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label { Text = "Resolution", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(_resolution, 1, 0);
        layout.Controls.Add(new Label { Text = "Save to", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        var pathRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        pathRow.Controls.AddRange(new Control[] { _path, _browse });
        layout.Controls.Add(pathRow, 1, 1);

        // YouTube details
        var yt = project.YouTube;
        _ytTitle.Text = yt.Title.Length > 0 ? yt.Title : Path.GetFileNameWithoutExtension(_path.Text);
        _ytDescription.Text = yt.Description.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        _ytTags.Text = yt.Tags;
        _ytSave.Checked = yt.SaveTextFile;
        var ytHeading = new Label { Text = "YOUTUBE DETAILS", AutoSize = true, ForeColor = Color.DimGray, Font = new Font(Font.FontFamily, 7.5f, FontStyle.Bold), Margin = new Padding(3, 12, 3, 2) };
        layout.Controls.Add(ytHeading, 0, 2);
        layout.SetColumnSpan(ytHeading, 2);
        layout.Controls.Add(new Label { Text = "Title", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        layout.Controls.Add(_ytTitle, 1, 3);
        layout.Controls.Add(new Label { Text = "Description", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(3, 6, 3, 3) }, 0, 4);
        layout.Controls.Add(_ytDescription, 1, 4);
        layout.Controls.Add(new Label { Text = "Tags", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 5);
        layout.Controls.Add(_ytTags, 1, 5);
        layout.Controls.Add(_ytInfo, 1, 6);
        layout.Controls.Add(_ytSave, 1, 7);
        foreach (var box in new[] { _ytTitle, _ytDescription, _ytTags }) box.TextChanged += (_, _) => UpdateYouTubeInfo();
        UpdateYouTubeInfo();

        layout.Controls.Add(_progress, 0, 8);
        layout.SetColumnSpan(_progress, 2);
        layout.Controls.Add(_status, 0, 9);
        layout.SetColumnSpan(_status, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        buttons.Controls.AddRange(new Control[] { _cancel, _export });
        layout.Controls.Add(buttons, 0, 10);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);

        AcceptButton = _export;
        _browse.Click += (_, _) => Browse();
        _export.Click += async (_, _) => await ExportAsync();
        _cancel.Click += (_, _) => { if (_cts is not null) _cts.Cancel(); else Close(); };
        FormClosing += (_, e) => { if (_cts is not null) { e.Cancel = true; _cts.Cancel(); } };
    }

    void Browse()
    {
        // No overwrite prompt here: ExportAsync asks (replace / keep both) when the export starts.
        using var dlg = new SaveFileDialog { Filter = "MP4 video|*.mp4", FileName = Path.GetFileName(_path.Text), InitialDirectory = Path.GetDirectoryName(_path.Text), OverwritePrompt = false };
        if (dlg.ShowDialog(this) == DialogResult.OK) _path.Text = dlg.FileName;
    }

    async Task ExportAsync()
    {
        var output = _path.Text.Trim();
        if (string.IsNullOrEmpty(output)) { Browse(); return; }
        if (!output.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) output += ".mp4";
        if (File.Exists(output))
        {
            if (AskAboutExisting(output) is not { } chosen) return;
            output = chosen;
            _path.Text = output;
        }
        // Write to a temporary file beside the real one and only swap it in when the export succeeds, so a cancel
        // or failure never destroys an existing video at that path (it used to overwrite, then delete on cancel).
        var working = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, Path.GetFileNameWithoutExtension(output) + ".exporting.mp4");

        var details = CurrentDetails();
        _saveDetails?.Invoke(details); // remembered in the project, even if the export is cancelled
        var settings = new ExportSettings { Resolution = ExportResolutions.All[_resolution.SelectedIndex].Value };
        var (w, h) = settings.Size;
        var temp = Path.Combine(Path.GetTempPath(), "Flyweight-export-" + Guid.NewGuid().ToString("N"));

        _cts = new CancellationTokenSource();
        _export.Enabled = _resolution.Enabled = _path.Enabled = _browse.Enabled = false;
        _cancel.Text = "Cancel";
        _status.Text = "Rendering annotations…";
        try
        {
            // Same renderer as the preview → WYSIWYG overlays.
            var overlays = OverlayRenderer.RenderAll(_project, w, h, temp);
            var plan = ExportPlan.Build(_project, settings, overlays, working);
            var sw = Stopwatch.StartNew();
            var progress = new Progress<double>(p =>
            {
                if (p == 0) sw.Restart(); // the CPU fallback starts again from 0%
                _progress.Value = (int)(p * 1000);
                var eta = p > 0.02 ? TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds * (1 - p) / p) : (TimeSpan?)null;
                _status.Text = $"Exporting… {p:P0}" + (eta is { } e ? $"  ·  about {e:m\\:ss} left" : "");
            });
            _status.Text = "Exporting…";
            await plan.RunAsync(progress, _cts.Token);
            File.Move(working, output, overwrite: true); // the user already agreed to replace it

            _progress.Value = 1000;
            var size = new FileInfo(output).Length / (1024.0 * 1024.0);
            _status.Text = $"Done in {sw.Elapsed:m\\:ss}  ·  {size:0} MB  ·  {plan.UsedEncoder}";
            // Title, description (+ chapters from the markers) and tags, ready to paste into Studio (D31).
            string? detailsFile = null;
            if (details.SaveTextFile)
            {
                try { detailsFile = YouTubeText.Save(details, output, _project.Markers.Count > 0 ? YouTubeChapters.Build(_project) : null); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the video is what matters */ }
            }
            ShowDone(output, size, sw.Elapsed, plan.UsedEncoder, detailsFile);
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Cancelled.";
            TryDelete(working);
        }
        catch (Exception ex)
        {
            TryDelete(working);
            _status.Text = "Export failed.";
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
            _cts.Dispose();
            _cts = null;
            _export.Enabled = _resolution.Enabled = _path.Enabled = _browse.Enabled = true;
            _cancel.Text = "Close";
        }
    }

    /// <summary>The file is already there: replace it, keep both (a numbered name), or stop. Null = stop.</summary>
    string? AskAboutExisting(string output)
    {
        var both = UniquePath(output);
        var replace = new TaskDialogButton("Replace it");
        var keep = new TaskDialogButton($"Keep both: save as \"{Path.GetFileName(both)}\"");
        var page = new TaskDialogPage
        {
            Caption = "Export for YouTube",
            Heading = $"\"{Path.GetFileName(output)}\" already exists",
            Text = $"In {Path.GetDirectoryName(output)}.",
            Icon = TaskDialogIcon.Warning,
            Buttons = { keep, replace, TaskDialogButton.Cancel },
            DefaultButton = keep,
        };
        var choice = TaskDialog.ShowDialog(this, page);
        return choice == replace ? output : choice == keep ? both : null;
    }

    /// <summary>"name (2).mp4", "name (3).mp4"… the first that doesn't exist yet.</summary>
    static string UniquePath(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    const string YouTubeStudioUrl = "https://studio.youtube.com/";

    /// <summary>Optional tip jar. Only ever shown as a small footnote after a finished export (DECISIONS: support link).</summary>
    public const string BuyMeACoffeeUrl = "https://buymeacoffee.com/stephenbeale";

    /// <summary>
    /// "Export complete": go straight on to uploading. Open YouTube Studio opens the upload site in the browser and
    /// selects the file in Explorer, ready to drag into Studio's upload box.
    /// </summary>
    YouTubeDetails CurrentDetails() => new()
    {
        Title = _ytTitle.Text.Trim(),
        Description = _ytDescription.Text.Replace("\r\n", "\n"),
        Tags = _ytTags.Text.Trim(),
        SaveTextFile = _ytSave.Checked,
    };

    /// <summary>Character counts, the chapters that will be added, and anything YouTube would reject.</summary>
    void UpdateYouTubeInfo()
    {
        var d = CurrentDetails();
        var chapters = _project.Markers.Count > 0 ? YouTubeChapters.Build(_project) : null;
        var lines = chapters?.Text ?? "";
        var parts = new List<string>
        {
            $"Title {d.Title.Length}/{YouTubeText.MaxTitle}",
            $"Description {YouTubeText.FullDescription(d, lines).Length}/{YouTubeText.MaxDescription}",
            $"Tags {YouTubeText.TagsLength(d.Tags)}/{YouTubeText.MaxTags}",
        };
        var text = string.Join("  ·  ", parts);
        text += chapters is { Chapters.Count: > 0 }
            ? $"\n{chapters.Chapters.Count} chapters from your markers are added below the description" +
              (chapters.YouTubeWillShowChapters ? "." : $" (YouTube won't show them yet: {chapters.Warnings[0]})")
            : "\nNo chapters: press M on the timeline to add chapter markers.";
        var problems = YouTubeText.Problems(d, lines);
        if (problems.Count > 0) text += "\n⚠ " + string.Join("\n⚠ ", problems);
        _ytInfo.Text = text;
        _ytInfo.ForeColor = problems.Count > 0 ? Color.DarkOrange : Color.DimGray;
    }

    void ShowDone(string output, double sizeMb, TimeSpan took, string encoder, string? detailsFile)
    {
        string instructions = "Open YouTube Studio to upload it: the file will be selected in Explorer so you can drag it into Studio" +
                              (detailsFile is null ? "." : ", and your title, description and tags open in a text file to paste in.");
        using var done = new ExportDoneForm(output, $"{sizeMb:0} MB · {took:m\\:ss} · {encoder}", instructions, BuyMeACoffeeUrl);
        done.ShowDialog(this);
        try
        {
            if (done.OpenStudio)
            {
                Process.Start("explorer.exe", $"/select,\"{output}\"");
                if (detailsFile is not null) Process.Start(new ProcessStartInfo(detailsFile) { UseShellExecute = true }); // title/description to paste
                Process.Start(new ProcessStartInfo(YouTubeStudioUrl) { UseShellExecute = true }); // default browser
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't open that:\n\n{ex.Message}\n\nYouTube Studio is at {YouTubeStudioUrl}", "Export complete",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
