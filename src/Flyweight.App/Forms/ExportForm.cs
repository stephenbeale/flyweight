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

    public ExportForm(Project project, string? suggestedPath)
    {
        _project = project;
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
        layout.Controls.Add(_progress, 0, 2);
        layout.SetColumnSpan(_progress, 2);
        layout.Controls.Add(_status, 0, 3);
        layout.SetColumnSpan(_status, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        buttons.Controls.AddRange(new Control[] { _cancel, _export });
        layout.Controls.Add(buttons, 0, 4);
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
            ShowDone(output, size, sw.Elapsed, plan.UsedEncoder);
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

    /// <summary>
    /// "Export complete": go straight on to uploading. Open YouTube Studio opens the upload site in the browser and
    /// selects the file in Explorer, ready to drag into Studio's upload box.
    /// </summary>
    void ShowDone(string output, double sizeMb, TimeSpan took, string encoder)
    {
        var studio = new TaskDialogButton("Open YouTube Studio");
        var explorer = new TaskDialogButton("Show in Explorer");
        var page = new TaskDialogPage
        {
            Caption = "Export complete",
            Heading = "Your video is ready",
            Text = $"{Path.GetFileName(output)}\n{sizeMb:0} MB · {took:m\\:ss} · {encoder}\n\n" +
                   "Open YouTube Studio to upload it: the file will be selected in Explorer so you can drag it into Studio.",
            Icon = TaskDialogIcon.ShieldSuccessGreenBar,
            Buttons = { studio, explorer, TaskDialogButton.Close },
            DefaultButton = studio,
        };
        var choice = TaskDialog.ShowDialog(this, page);
        try
        {
            if (choice == studio)
            {
                Process.Start("explorer.exe", $"/select,\"{output}\"");
                Process.Start(new ProcessStartInfo(YouTubeStudioUrl) { UseShellExecute = true }); // default browser
            }
            else if (choice == explorer)
            {
                Process.Start("explorer.exe", $"/select,\"{output}\"");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't open that:\n\n{ex.Message}\n\nYouTube Studio is at {YouTubeStudioUrl}", "Export complete",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
