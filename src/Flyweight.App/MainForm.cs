using Flyweight.App.Controls;
using Flyweight.App.Forms;
using Flyweight.App.Playback;
using Flyweight.Core.Editing;
using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.App;

public sealed class MainForm : Form
{
    static readonly string AppDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flyweight");
    static readonly string AutosavePath = Path.Combine(AppDataDir, "autosave" + ProjectSerializer.FileExtension);
    static readonly string LockPath = Path.Combine(AppDataDir, "session.lock");
    static readonly string[] VideoExtensions = { ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".webm", ".mts", ".m2ts", ".wmv", ".gif" };
    static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tif", ".tiff" };

    readonly ProjectSession _session = new();
    readonly MediaLibrary _library = new();
    readonly PlaybackEngine _engine;

    readonly PreviewCanvas _preview = new() { Dock = DockStyle.Fill };
    readonly TimelineControl _timeline = new() { Dock = DockStyle.Fill };
    readonly MediaBinControl _bin = new() { Dock = DockStyle.Fill };
    readonly PropertyGrid _props = new() { Dock = DockStyle.Fill, ToolbarVisible = false, HelpVisible = true, Visible = false };
    readonly Label _propsEmpty = new() { Dock = DockStyle.Fill, ForeColor = Color.Gray, TextAlign = ContentAlignment.MiddleCenter,
        Text = "Select an arrow, box or text\nto edit its style.\n\nA  arrow    R  box    T  text" };
    readonly Label _timecode = new() { AutoSize = false, Width = 230, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.Gainsboro,
        Font = new Font("Consolas", 11f), Dock = DockStyle.Left };
    readonly ToolStripStatusLabel _statusText = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripButton _undoBtn = new("Undo");
    readonly ToolStripButton _redoBtn = new("Redo");
    readonly Dictionary<Tool, ToolStripButton> _toolButtons = new();
    readonly Button _playBtn = new() { Text = "▶", Width = 44, Dock = DockStyle.Left, FlatStyle = FlatStyle.Flat, ForeColor = Color.White };
    readonly System.Windows.Forms.Timer _autosave = new() { Interval = 10_000 };

    public MainForm()
    {
        Text = "Flyweight";
        Width = 1400;
        Height = 900;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(24, 24, 28);
        ForeColor = Color.Gainsboro;
        KeyPreview = true;
        AllowDrop = true;
        using (var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("Flyweight.App.flyweight.ico"))
            if (iconStream is not null) Icon = new Icon(iconStream);

        _engine = new PlaybackEngine(_session, _library);
        _preview.Bind(_session, _engine);
        _timeline.Bind(_session, _library, () => _engine.Position);

        BuildLayout();
        WireEvents();
        UpdateUi();
    }

    // ---------------- Layout ----------------

    void BuildLayout()
    {
        // System-rendered (light) toolbar: set its colours explicitly, otherwise items inherit the
        // form's dark-theme Gainsboro text and come out pale grey on the light strip.
        var tools = new ToolStrip
        {
            GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System, Padding = new Padding(6, 2, 6, 2),
            BackColor = SystemColors.Control, ForeColor = Color.Black,
        };
        ToolStripButton Btn(string text, string tip, Action click)
        {
            var b = new ToolStripButton(text) { ToolTipText = tip, DisplayStyle = ToolStripItemDisplayStyle.Text, ForeColor = Color.Black };
            b.Click += (_, _) => click();
            return b;
        }
        _undoBtn.Click += (_, _) => _session.Undo();
        _redoBtn.Click += (_, _) => _session.Redo();
        _undoBtn.ToolTipText = "Undo (Ctrl+Z)";
        _redoBtn.ToolTipText = "Redo (Ctrl+Y)";
        _undoBtn.ForeColor = _redoBtn.ForeColor = Color.Black; // still greys out when disabled

        tools.Items.AddRange(new ToolStripItem[]
        {
            Btn("New", "New project", NewProject),
            Btn("Open…", "Open project (Ctrl+O)", OpenProject),
            Btn("Save", "Save project (Ctrl+S)", () => SaveProject(false)),
            new ToolStripSeparator(),
            Btn("Import…", "Import video or images (Ctrl+I)", ImportDialog),
            new ToolStripSeparator(),
            _undoBtn, _redoBtn,
            new ToolStripSeparator(),
        });
        foreach (var (tool, label, tip) in new[]
        {
            (Tool.Select, "Select", "Select / move (V)"),
            (Tool.Arrow, "Arrow", "Draw an arrow (A)"),
            (Tool.Box, "Box", "Draw a box (R)"),
            (Tool.Text, "Text", "Add text (T)"),
        })
        {
            var b = Btn(label, tip, () => _preview.Tool = tool);
            _toolButtons[tool] = b;
            tools.Items.Add(b);
        }
        tools.Items.AddRange(new ToolStripItem[]
        {
            new ToolStripSeparator(),
            Btn("Split", "Split clip at playhead (S)", Split),
            Btn("Freeze", "Freeze this frame: hold it for a few seconds (F)", () => _ = FreezeFrameAsync()),
            Btn("Delete", "Delete selection (Del)", DeleteSelection),
            Btn("Fit", "Zoom timeline to fit (Ctrl+0)", () => _timeline.ZoomToFit()),
            new ToolStripSeparator(),
            Btn("Export for YouTube…", "Export MP4 (Ctrl+E)", Export),
        });

        // Transport bar under the preview
        var transport = new Panel { Dock = DockStyle.Bottom, Height = 36, BackColor = Color.FromArgb(32, 32, 38), Padding = new Padding(8, 4, 8, 4) };
        Button TBtn(string text, string tip, Action click)
        {
            var b = new Button { Text = text, Width = 40, Dock = DockStyle.Left, FlatStyle = FlatStyle.Flat, ForeColor = Color.White };
            b.FlatAppearance.BorderSize = 0;
            new ToolTip().SetToolTip(b, tip);
            b.Click += (_, _) => { click(); _preview.Focus(); };
            return b;
        }
        _playBtn.FlatAppearance.BorderSize = 0;
        _playBtn.Click += (_, _) => { _engine.TogglePlay(); _preview.Focus(); };
        transport.Controls.Add(_timecode);
        transport.Controls.Add(TBtn("⏭", "End (End)", () => _engine.Seek(TimelineOps.TotalDuration(_session.Project))));
        transport.Controls.Add(TBtn("|▶", "Next frame (→)", () => _engine.StepFrames(1)));
        transport.Controls.Add(_playBtn);
        transport.Controls.Add(TBtn("◀|", "Previous frame (←)", () => _engine.StepFrames(-1)));
        transport.Controls.Add(TBtn("⏮", "Start (Home)", () => _engine.Seek(TimeSpan.Zero)));

        var previewPanel = new Panel { Dock = DockStyle.Fill };
        previewPanel.Controls.Add(_preview);
        previewPanel.Controls.Add(transport);

        var propsPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(28, 28, 32) };
        propsPanel.Controls.Add(_props);
        propsPanel.Controls.Add(_propsEmpty);
        propsPanel.Controls.Add(new Label { Text = "Properties", Dock = DockStyle.Top, Height = 26, Padding = new Padding(8, 6, 0, 0), ForeColor = Color.Silver, BackColor = Color.FromArgb(38, 38, 44) });

        var centreRight = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2, SplitterWidth = 4 };
        centreRight.Panel1.Controls.Add(previewPanel);
        centreRight.Panel2.Controls.Add(propsPanel);

        var top = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = 4 };
        top.Panel1.Controls.Add(_bin);
        top.Panel2.Controls.Add(centreRight);

        var main = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2, SplitterWidth = 4 };
        main.Panel1.Controls.Add(top);
        main.Panel2.Controls.Add(_timeline);

        var status = new StatusStrip { SizingGrip = true };
        status.Items.Add(_statusText);

        Controls.Add(main);
        Controls.Add(tools);
        Controls.Add(status);

        // SplitContainer distances can only be set once the controls have a size.
        Load += (_, _) =>
        {
            main.SplitterDistance = Math.Max(200, main.Height - 210);
            top.SplitterDistance = 280;
            centreRight.SplitterDistance = Math.Max(300, centreRight.Width - 290);
        };
    }

    // ---------------- Events ----------------

    void WireEvents()
    {
        _session.Changed += (_, _) => { _engine.Refresh(); UpdateUi(); };
        _library.StateChanged += (_, id) => { if (IsHandleCreated) BeginInvoke(() => OnMediaStateChanged(id)); };
        _engine.PositionChanged += (_, _) =>
        {
            UpdateTimecode();
            if (_engine.IsPlaying) _timeline.EnsurePlayheadVisible(); else _timeline.Invalidate();
            _preview.Invalidate();
        };

        _timeline.SeekRequested += (_, t) => { _engine.Pause(); _engine.Seek(t); };
        _timeline.AnnotationSelected += (_, _) => _preview.Select(_timeline.SelectedAnnotationId);
        _timeline.ClipSelected += (_, _) => { if (_timeline.SelectedClipId is not null) _preview.Select(null); };
        _preview.SelectionChanged += (_, _) =>
        {
            _timeline.SelectedAnnotationId = _preview.SelectedAnnotationId;
            if (_preview.SelectedAnnotationId is not null) _timeline.SelectedClipId = null;
            _timeline.Invalidate();
            UpdatePropertyGrid();
        };
        _preview.ToolChanged += (_, _) => UpdateToolButtons();

        _props.PropertyValueChanged += (_, _) =>
        {
            if (_props.SelectedObject is not AnnotationProperties vm || _preview.SelectedAnnotationId is not { } id) return;
            _session.Execute(p => { if (p.FindAnnotation(id) is { } a) vm.ApplyTo(a); });
        };

        _bin.AddToTimelineRequested += (_, id) => _session.Execute(p =>
        {
            if (p.FindMedia(id) is { } m) TimelineOps.Append(p, m);
        });

        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += async (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files)
            {
                var project = files.FirstOrDefault(f => f.EndsWith(ProjectSerializer.FileExtension, StringComparison.OrdinalIgnoreCase));
                if (project is not null) { if (ConfirmDiscard()) LoadProject(project); }
                else await ImportAsync(files.Where(IsImportable));
            }
        };

        _autosave.Tick += (_, _) => Autosave();
        _autosave.Start();

        Shown += (_, _) => OfferCrashRecovery();
        FormClosing += (_, e) =>
        {
            _preview.CommitTextEdit();
            if (!ConfirmDiscard()) { e.Cancel = true; return; }
            _engine.Dispose();
            TryDelete(LockPath);
            TryDelete(AutosavePath);
        };
    }

    void OnMediaStateChanged(Guid id)
    {
        _bin.Sync(_session.Project, _library);
        _timeline.Invalidate();
        var st = _library.GetState(id);
        if (st.Status == ProxyStatus.Ready) _engine.Refresh(); // the clip under the playhead may just have become playable
        if (st.Status == ProxyStatus.Failed && _session.Project.FindMedia(id) is { } m)
            _statusText.Text = $"Couldn't optimise {m.DisplayName}: {st.Error}";
        else
            UpdateStatus();
    }

    // ---------------- Keyboard ----------------

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Let text boxes (inline text editor, property grid) have their keys.
        if (_preview.IsEditingText || _props.ContainsFocus || ActiveControl is TextBoxBase)
            return base.ProcessCmdKey(ref msg, keyData);

        switch (keyData)
        {
            case Keys.Space: _engine.TogglePlay(); return true;
            case Keys.K: _engine.Pause(); return true;
            case Keys.L: _engine.Play(); return true;
            case Keys.J: _engine.Seek(_engine.Position - TimeSpan.FromSeconds(1)); return true;
            case Keys.Left: _engine.StepFrames(-1); return true;
            case Keys.Right: _engine.StepFrames(1); return true;
            case Keys.Shift | Keys.Left: _engine.Seek(_engine.Position - TimeSpan.FromSeconds(1)); return true;
            case Keys.Shift | Keys.Right: _engine.Seek(_engine.Position + TimeSpan.FromSeconds(1)); return true;
            case Keys.Home: _engine.Seek(TimeSpan.Zero); return true;
            case Keys.End: _engine.Seek(TimelineOps.TotalDuration(_session.Project)); return true;
            case Keys.S: Split(); return true;
            case Keys.F: _ = FreezeFrameAsync(); return true;
            case Keys.Delete: case Keys.Back: DeleteSelection(); return true;
            case Keys.V: case Keys.Escape: _preview.Tool = Tool.Select; return true;
            case Keys.A: _preview.Tool = Tool.Arrow; return true;
            case Keys.R: _preview.Tool = Tool.Box; return true;
            case Keys.T: _preview.Tool = Tool.Text; return true;
            case Keys.Control | Keys.Z: _session.Undo(); return true;
            case Keys.Control | Keys.Y: case Keys.Control | Keys.Shift | Keys.Z: _session.Redo(); return true;
            case Keys.Control | Keys.S: SaveProject(false); return true;
            case Keys.Control | Keys.Shift | Keys.S: SaveProject(true); return true;
            case Keys.Control | Keys.O: OpenProject(); return true;
            case Keys.Control | Keys.I: ImportDialog(); return true;
            case Keys.Control | Keys.E: Export(); return true;
            case Keys.Control | Keys.D0: _timeline.ZoomToFit(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---------------- Commands ----------------

    void Split()
    {
        _engine.Pause();
        _session.Execute(p => TimelineOps.SplitAt(p, _engine.Position));
    }

    async Task FreezeFrameAsync()
    {
        _engine.Pause();
        var at = _engine.Position;
        var p = _session.Project;
        if (TimelineOps.Locate(p, at) is not { } hit || p.FindMedia(hit.Clip.SourceId) is not { } media)
        {
            _statusText.Text = "Move the playhead onto a clip first, then press F.";
            return;
        }
        if (media.IsStill)
        {
            _statusText.Text = "That's already a still image: drag its right edge to hold it longer.";
            return;
        }

        using var dlg = new FreezeFrameForm();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _statusText.Text = "Grabbing frame…";
        SourceMedia still;
        try
        {
            var png = await _library.ExtractFrameAsync(media, hit.SourceTime);
            still = await _library.ProbeAsync(png);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't grab that frame:

{ex.Message}", "Freeze frame", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            UpdateStatus();
            return;
        }
        _session.Execute(proj => TimelineOps.InsertFreezeFrame(proj, at, still, dlg.Length));
        StartProxy(still);
        UpdateStatus();
    }

    void DeleteSelection()
    {
        if (_preview.SelectedAnnotationId is { } aid)
        {
            _session.Execute(p => p.Annotations.RemoveAll(a => a.Id == aid));
            _preview.Select(null);
        }
        else if (_timeline.SelectedClipId is { } cid)
        {
            _session.Execute(p => TimelineOps.Delete(p, cid));
            _timeline.SelectedClipId = null;
        }
        else
        {
            _statusText.Text = "Click a clip or annotation first, then press Delete.";
        }
    }

    void ImportDialog()
    {
        using var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = "Import video or images",
            Filter = "Video and images|" + string.Join(";", VideoExtensions.Concat(ImageExtensions).Select(e => "*" + e)) +
                     "|Video|" + string.Join(";", VideoExtensions.Select(e => "*" + e)) +
                     "|Images|" + string.Join(";", ImageExtensions.Select(e => "*" + e)) + "|All files|*.*",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) _ = ImportAsync(dlg.FileNames);
    }

    async Task ImportAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            _statusText.Text = $"Reading {Path.GetFileName(path)}…";
            SourceMedia media;
            try { media = await _library.ProbeAsync(path); }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't read {Path.GetFileName(path)}:\n\n{ex.Message}", "Import", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                continue;
            }
            _session.Execute(p => { p.Media.Add(media); TimelineOps.Append(p, media); });
            if (_session.Project.Timeline.Count == 1) _timeline.ZoomToFit();
            StartProxy(media);
        }
        UpdateStatus();
    }

    void StartProxy(SourceMedia media) => _ = Task.Run(async () =>
    {
        try { await _library.EnsureProxyAsync(media); }
        catch (Exception) { /* status is recorded as Failed in the library */ }
    });

    void Export()
    {
        _engine.Pause();
        _preview.CommitTextEdit();
        if (_session.Project.Timeline.Count == 0) { MessageBox.Show(this, "Add some clips to the timeline first.", "Export"); return; }
        var suggested = _session.FilePath is { } fp ? Path.ChangeExtension(fp, ".mp4") : null;
        using var dlg = new ExportForm(_session.Project, suggested);
        dlg.ShowDialog(this);
    }

    void NewProject()
    {
        if (!ConfirmDiscard()) return;
        using var dlg = new NewProjectForm();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _engine.Pause();
        _session.New(dlg.OutputResolution);
        _engine.Seek(TimeSpan.Zero);
    }

    void OpenProject()
    {
        if (!ConfirmDiscard()) return;
        using var dlg = new OpenFileDialog { Filter = $"Flyweight project|*{ProjectSerializer.FileExtension}" };
        if (dlg.ShowDialog(this) == DialogResult.OK) LoadProject(dlg.FileName);
    }

    void LoadProject(string path, bool asRecovery = false)
    {
        Project project;
        try { project = ProjectSerializer.Load(path); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't open the project:\n\n{ex.Message}", "Open", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _engine.Pause();
        _session.Load(project, asRecovery ? null : path);
        var missing = project.Media.Where(m => !File.Exists(m.OriginalPath)).Select(m => m.OriginalPath).ToList();
        if (missing.Count > 0)
            MessageBox.Show(this, "These source files are missing:\n\n" + string.Join("\n", missing), "Missing media",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        foreach (var m in project.Media.Where(m => File.Exists(m.OriginalPath))) StartProxy(m); // instant if cached
        _engine.Seek(TimeSpan.Zero);
        _timeline.ZoomToFit();
    }

    bool SaveProject(bool saveAs)
    {
        _preview.CommitTextEdit();
        var path = _session.FilePath;
        if (saveAs || path is null)
        {
            using var dlg = new SaveFileDialog { Filter = $"Flyweight project|*{ProjectSerializer.FileExtension}", FileName = "My video" + ProjectSerializer.FileExtension };
            if (dlg.ShowDialog(this) != DialogResult.OK) return false;
            path = dlg.FileName;
        }
        try
        {
            _session.Save(path);
            _statusText.Text = $"Saved {Path.GetFileName(path)}";
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    bool ConfirmDiscard()
    {
        if (!_session.IsDirty) return true;
        var r = MessageBox.Show(this, "Save changes to this project?", "Flyweight", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        return r == DialogResult.No || (r == DialogResult.Yes && SaveProject(false));
    }

    // ---------------- Autosave & crash recovery ----------------

    void Autosave()
    {
        if (!_session.IsDirty) return;
        try
        {
            _session.Autosave(AutosavePath);
            _statusText.Text = $"Autosaved {DateTime.Now:HH:mm:ss}";
        }
        catch { /* never let autosave interrupt editing */ }
    }

    void OfferCrashRecovery()
    {
        Directory.CreateDirectory(AppDataDir);
        bool uncleanExit = File.Exists(LockPath);
        File.WriteAllText(LockPath, Environment.ProcessId.ToString());
        if (!uncleanExit || !File.Exists(AutosavePath)) return;

        var r = MessageBox.Show(this,
            $"Flyweight didn't close properly last time.\n\nRecover the autosaved project from {File.GetLastWriteTime(AutosavePath):g}?",
            "Recover work", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r == DialogResult.Yes) LoadProject(AutosavePath, asRecovery: true);
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    // ---------------- UI refresh ----------------

    void UpdateUi()
    {
        var name = _session.FilePath is { } p ? Path.GetFileNameWithoutExtension(p) : "Untitled";
        Text = $"{name}{(_session.IsDirty ? " •" : "")} · Flyweight";
        _undoBtn.Enabled = _session.CanUndo;
        _redoBtn.Enabled = _session.CanRedo;

        // Drop selections that no longer exist (e.g. after undo).
        if (_preview.SelectedAnnotationId is { } aid && _session.Project.FindAnnotation(aid) is null) _preview.Select(null);
        if (_timeline.SelectedClipId is { } cid && _session.Project.FindClip(cid) is null) _timeline.SelectedClipId = null;

        _bin.Sync(_session.Project, _library);
        _timeline.Invalidate();
        _preview.Invalidate();
        UpdatePropertyGrid();
        UpdateToolButtons();
        UpdateTimecode();
        UpdateStatus();
    }

    void UpdatePropertyGrid()
    {
        var a = _preview.SelectedAnnotationId is { } id ? _session.Project.FindAnnotation(id) : null;
        if (a is null)
        {
            _props.SelectedObject = null;
            _props.Visible = false;
            _propsEmpty.Visible = true;
            return;
        }
        // Don't rebuild while the user is typing in the grid; it would reset their cursor.
        if (_props.ContainsFocus && _props.SelectedObject is AnnotationProperties) return;
        _props.SelectedObject = AnnotationProperties.For(a);
        _props.Visible = true;
        _propsEmpty.Visible = false;
    }

    void UpdateToolButtons()
    {
        foreach (var (tool, b) in _toolButtons) b.Checked = _preview.Tool == tool;
    }

    void UpdateTimecode()
    {
        var total = TimelineOps.TotalDuration(_session.Project);
        _timecode.Text = $"{Tc(_engine.Position)} / {Tc(total)}";
        _playBtn.Text = _engine.IsPlaying ? "❚❚" : "▶";
    }

    /// <summary>h:mm:ss.ff where ff = frame number within the second.</summary>
    string Tc(TimeSpan t)
    {
        double fps = _session.Project.Media.FirstOrDefault()?.Fps is > 0 and var f ? f : 30;
        int frame = (int)Math.Floor(t.Milliseconds / 1000.0 * fps);
        return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{frame:00}";
    }

    void UpdateStatus()
    {
        var busy = _session.Project.Media.Count(m => _library.GetState(m.Id).Status is ProxyStatus.Queued or ProxyStatus.Optimising);
        if (busy > 0) _statusText.Text = $"Optimising {busy} clip{(busy == 1 ? "" : "s")} for smooth editing…";
        else if (_statusText.Text is { } s && (s.StartsWith("Optimising") || s.StartsWith("Reading"))) _statusText.Text = "Ready";
    }

    static bool IsImportable(string path) =>
        VideoExtensions.Concat(ImageExtensions).Contains(Path.GetExtension(path).ToLowerInvariant());
}
