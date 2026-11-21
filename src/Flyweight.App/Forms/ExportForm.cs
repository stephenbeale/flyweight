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
        using var dlg = new SaveFileDialog { Filter = "MP4 video|*.mp4", FileName = Path.GetFileName(_path.Text), InitialDirectory = Path.GetDirectoryName(_path.Text) };
        if (dlg.ShowDialog(this) == DialogResult.OK) _path.Text = dlg.FileName;
    }

    async Task ExportAsync()
    {
        var output = _path.Text.Trim();
        if (string.IsNullOrEmpty(output)) { Browse(); return; }
        if (!output.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) output += ".mp4";

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
            var plan = ExportPlan.Build(_project, settings, overlays, output);
            var sw = Stopwatch.StartNew();
            var progress = new Progress<double>(p =>
            {
                _progress.Value = (int)(p * 1000);
                var eta = p > 0.02 ? TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds * (1 - p) / p) : (TimeSpan?)null;
                _status.Text = $"Exporting… {p:P0}" + (eta is { } e ? $"  ·  about {e:m\\:ss} left" : "");
            });
            _status.Text = "Exporting…";
            await plan.RunAsync(progress, _cts.Token);

            _progress.Value = 1000;
            _status.Text = $"Done in {sw.Elapsed:m\\:ss}.";
            // Chapter markers → a ready-to-paste YouTube chapters file beside the video (D20).
            if (_project.Markers.Count > 0)
            {
                var chapters = YouTubeChapters.Build(_project);
                if (chapters.Text.Length > 0)
                {
                    try
                    {
                        File.WriteAllText(YouTubeChapters.TextPathBesideVideo(output), chapters.Text + Environment.NewLine);
                        _status.Text += " Chapters saved beside it" + (chapters.YouTubeWillShowChapters ? "." : " (check the warnings in YouTube chapters…).");
                    }
                    catch { /* the video is what matters; chapters can be re-saved from the toolbar */ }
                }
            }
            if (MessageBox.Show(this, $"Exported to\n{output}\n\nShow it in Explorer?", "Export complete",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                Process.Start("explorer.exe", $"/select,\"{output}\"");
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Cancelled.";
            TryDelete(output);
        }
        catch (Exception ex)
        {
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

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
