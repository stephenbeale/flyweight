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
    static readonly string[] AudioExtensions = { ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma" };

    readonly ProjectSession _session = new();
    readonly MediaLibrary _library = new();
    readonly PlaybackEngine _engine;

    readonly PreviewCanvas _preview = new() { Dock = DockStyle.Fill };
    readonly TimelineControl _timeline = new() { Dock = DockStyle.Fill };
    readonly MediaBinControl _bin = new() { Dock = DockStyle.Fill };
    readonly AnnotationStylePanel _props = new() { Dock = DockStyle.Fill, Visible = false };
    readonly ClipPropertiesPanel _clipProps = new() { Dock = DockStyle.Fill, Visible = false };
    readonly Label _propsEmpty = new() { Dock = DockStyle.Fill, ForeColor = Color.Gray, TextAlign = ContentAlignment.MiddleCenter,
        Text = "Select an arrow, box or text\nto edit its style,\nor a clip for its volume.\n\nA  arrow    R  box    T  text\nM  mute clip" };
    // AutoSize: a fixed pixel width clipped "/ total" once Windows display scaling enlarged the font.
    readonly Label _timecode = new() { AutoSize = true, Padding = new Padding(12, 0, 12, 0), TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.Gainsboro,
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
        _preview.Bind(_session, _engine, _library);
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
            Btn("Import…", "Import video, images or audio (Ctrl+I)", ImportDialog),
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
        Button TBtn(string text, string tip, Action click, int width = 46)
        {
            var b = new Button { Text = text, Width = width, Dock = DockStyle.Left, FlatStyle = FlatStyle.Flat, ForeColor = Color.White };
            b.FlatAppearance.BorderSize = 0;
            new ToolTip().SetToolTip(b, tip);
            b.Click += (_, _) => { click(); _preview.Focus(); };
            return b;
        }
        _playBtn.FlatAppearance.BorderSize = 0;
        _playBtn.Click += (_, _) => { _engine.TogglePlay(); _preview.Focus(); };
        transport.Controls.Add(_timecode);
        // Docked Left, so the LAST added is leftmost: ⏮  |◀◀  ◀|  ▶  |▶  ▶▶|  ⏭  timecode
        transport.Controls.Add(TBtn("⏭", "End (End)", () => _engine.Seek(TimelineOps.TotalDuration(_session.Project))));
        transport.Controls.Add(TBtn("▶▶|", "Next clip (↓)", NextClip, width: 56));
        transport.Controls.Add(TBtn("|▶", "Next frame (→)", () => _engine.StepFrames(1)));
        transport.Controls.Add(_playBtn);
        transport.Controls.Add(TBtn("◀|", "Previous frame (←)", () => _engine.StepFrames(-1)));
        transport.Controls.Add(TBtn("|◀◀", "Previous clip (↑)", PreviousClip, width: 56));
        transport.Controls.Add(TBtn("⏮", "Start (Home)", () => _engine.Seek(TimeSpan.Zero)));

        var previewPanel = new Panel { Dock = DockStyle.Fill };
        previewPanel.Controls.Add(_preview);
        previewPanel.Controls.Add(transport);

        var propsPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(28, 28, 32) };
        propsPanel.Controls.Add(_props);
        propsPanel.Controls.Add(_clipProps);
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
            main.SplitterDistance = Math.Max(200, main.Height - 250); // ruler + video + audio track + annotation lanes
            top.SplitterDistance = 280;
            centreRight.SplitterDistance = Math.Max(300, centreRight.Width - 290);
        };
    }

    // ---------------- Events ----------------

    void WireEvents()
    {
        _session.Changed += (_, _) =>
        {
            // Only re-seek the player when the CLIPS changed. Annotation/style edits are drawn on top, and
            // re-seeking on every mouse move of an annotation drag queued dozens of VLC seeks (laggy play after).
            var sig = TimelineSignature(_session.Project);
            if (sig != _timelineSig) { _timelineSig = sig; _engine.Refresh(); }
            UpdateUi();
        };
        _library.StateChanged += (_, id) => { if (IsHandleCreated) BeginInvoke(() => OnMediaStateChanged(id)); };
        _engine.PositionChanged += (_, _) =>
        {
            UpdateTimecode();
            // Any playhead move (play, step, seek, clip jump) brings it back into view. Scrolling or
            // zooming the timeline alone doesn't move the playhead, so it doesn't fight the user.
            _timeline.EnsurePlayheadVisible();
            _preview.Invalidate();
        };
        _engine.PlayingChanged += (_, _) => UpdateTimecode(); // flips the ▶ / ❚❚ button

        _timeline.SeekRequested += (_, t) => { _engine.Pause(); _engine.Seek(t); };
        _timeline.AnnotationSelected += (_, _) => _preview.Select(_timeline.SelectedAnnotationId);
        _timeline.ClipSelected += (_, _) =>
        {
            if (_timeline.SelectedClipId is not null) _preview.Select(null);
            UpdatePropertyGrid();
        };
        _preview.SelectionChanged += (_, _) =>
        {
            _timeline.SelectedAnnotationId = _preview.SelectedAnnotationId;
            if (_preview.SelectedAnnotationId is not null) _timeline.SelectedClipId = _timeline.SelectedAudioId = null;
            _timeline.Invalidate();
            UpdatePropertyGrid();
        };
        _preview.ToolChanged += (_, _) => UpdateToolButtons();

        _props.Edit += (edit, phase) =>
        {
            if (_preview.SelectedAnnotationId is not { } id) return;
            void Apply(Project p)
            {
                if (p.FindAnnotation(id) is not { } a) return;
                edit(a);
                TimelineOps.SyncLinked(p, id); // same look on every piece of a run across cuts
            }
            switch (phase)
            {
                case StyleEditPhase.Single: _session.Execute(Apply); break;
                case StyleEditPhase.Live: _session.BeginEdit(); _session.Preview(Apply); break;
                case StyleEditPhase.Commit: _session.Preview(Apply); _session.CommitEdit(); break;
            }
        };

        // Volume / mute for whichever is selected: an audio-track item or a video clip.
        void ApplySound(Action<Project, Guid, bool> set, StyleEditPhase phase)
        {
            Action<Project>? apply =
                _timeline.SelectedAudioId is { } aid ? p => set(p, aid, true)
                : _timeline.SelectedClipId is { } cid ? p => set(p, cid, false)
                : null;
            if (apply is null) return;
            switch (phase)
            {
                case StyleEditPhase.Single: _session.Execute(apply); break;
                case StyleEditPhase.Live: _session.BeginEdit(); _session.Preview(apply); break;
                case StyleEditPhase.Commit: _session.Preview(apply); _session.CommitEdit(); break;
            }
        }
        _clipProps.VolumeChanged += (v, phase) => ApplySound((p, id, isAudio) =>
        {
            if (isAudio) TimelineOps.SetAudioVolume(p, id, v); else TimelineOps.SetClipVolume(p, id, v);
        }, phase);
        _clipProps.MutedChanged += on => ApplySound((p, id, isAudio) =>
        {
            if (isAudio) TimelineOps.SetAudioMuted(p, id, on); else TimelineOps.SetClipMuted(p, id, on);
        }, StyleEditPhase.Single);

        // Zoom / brightness / contrast for the selected video clip.
        _clipProps.LookChanged += (which, v, phase) =>
        {
            if (_timeline.SelectedClipId is not { } cid) return;
            Action<Project> apply = which switch
            {
                LookSetting.Zoom => p => TimelineOps.SetClipZoom(p, cid, v),
                LookSetting.PanX => p => TimelineOps.SetClipPan(p, cid, v, null),
                LookSetting.PanY => p => TimelineOps.SetClipPan(p, cid, null, v),
                LookSetting.Brightness => p => TimelineOps.SetClipBrightness(p, cid, v),
                _ => p => TimelineOps.SetClipContrast(p, cid, v),
            };
            switch (phase)
            {
                case StyleEditPhase.Single: _session.Execute(apply); break;
                case StyleEditPhase.Live: _session.BeginEdit(); _session.Preview(apply); break;
                case StyleEditPhase.Commit: _session.Preview(apply); _session.CommitEdit(); break;
            }
            ShowClipUnderPlayhead(cid);
        };
        _clipProps.LookReset += () =>
        {
            if (_timeline.SelectedClipId is not { } cid) return;
            _session.Execute(p => TimelineOps.ResetClipLook(p, cid));
            ShowClipUnderPlayhead(cid);
        };

        _timeline.AudioSelected += (_, _) =>
        {
            if (_timeline.SelectedAudioId is not null) _preview.Select(null);
            UpdatePropertyGrid();
        };
        _timeline.AudioMenuRequested += ShowAudioMenu;

        _bin.AddToTimelineRequested += (_, id) =>
        {
            if (_session.Project.FindMedia(id) is { IsAudioOnly: true } audio) { AddAudioAtPlayhead(audio); return; }
            _session.Execute(p =>
            {
                if (p.FindMedia(id) is { } m) TimelineOps.Append(p, m);
            });
        };
        _bin.InsertRequested += (mediaId, after) => InsertMedia(mediaId, CurrentClipId(), after);
        _bin.RemoveRequested += RemoveMedia;
        _timeline.ClipMenuRequested += ShowClipMenu;

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

        Shown += (_, _) =>
        {
            // "Flyweight.exe My video.flyweight" (Open with…, or a shortcut) opens that project.
            var arg = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(f => f.EndsWith(ProjectSerializer.FileExtension, StringComparison.OrdinalIgnoreCase));
            if (arg is not null && File.Exists(arg)) LoadProject(arg);
            else OfferCrashRecovery();
        };
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
        _preview.Invalidate(); // a thumbnail may have arrived for the stand-in picture
        var st = _library.GetState(id);
        if (st.Status == ProxyStatus.Ready) _engine.Refresh(); // the clip under the playhead may just have become playable
        if (st.Status == ProxyStatus.Failed && _session.Project.FindMedia(id) is { } m)
            // First line only: the full ffmpeg log is in the bin item's tooltip. (A multi-line status
            // used to swallow the whole window.)
            _statusText.Text = $"Couldn't optimise {m.DisplayName}: {FirstLine(st.Error)}  (hover it in the media bin for details)";
        else
            UpdateStatus();
    }

    static string FirstLine(string? text) => (text ?? "").Split('\n', 2)[0].Trim();

    // ---------------- Keyboard ----------------

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Let text inputs (inline text editor, panel text/number/font boxes) have their keys. Only the
        // focused control itself counts: a swatch or slider keeping focus mustn't kill the shortcuts.
        if (_preview.IsEditingText || FocusedControl() is TextBoxBase or UpDownBase or ComboBox)
            return base.ProcessCmdKey(ref msg, keyData);

        switch (keyData)
        {
            case Keys.Space: _engine.TogglePlay(); return true;
            case Keys.K: _engine.Pause(); return true;
            case Keys.L: _engine.Play(); return true;
            case Keys.J: _engine.Seek(_engine.Position - TimeSpan.FromSeconds(1)); return true;
            case Keys.Up: PreviousClip(); return true;
            case Keys.Down: NextClip(); return true;
            case Keys.Left: _engine.StepFrames(-1); return true;
            case Keys.Right: _engine.StepFrames(1); return true;
            case Keys.Shift | Keys.Left: _engine.Seek(_engine.Position - TimeSpan.FromSeconds(1)); return true;
            case Keys.Shift | Keys.Right: _engine.Seek(_engine.Position + TimeSpan.FromSeconds(1)); return true;
            case Keys.Home: _engine.Seek(TimeSpan.Zero); return true;
            case Keys.End: _engine.Seek(TimelineOps.TotalDuration(_session.Project)); return true;
            case Keys.S: Split(); return true;
            case Keys.F: _ = FreezeFrameAsync(); return true;
            // Ctrl+M too: Windows turns it into Enter, which in the media bin re-added the selected file.
            case Keys.M: case Keys.Control | Keys.M: ToggleMute(CurrentClipId()); return true;
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

    /// <summary>The innermost focused control (Form.ActiveControl stops at the first container).</summary>
    Control? FocusedControl()
    {
        Control? c = ActiveControl;
        while (c is ContainerControl { ActiveControl: { } inner }) c = inner;
        // NumericUpDown focuses an inner edit box; report the UpDownBase itself.
        return c?.Parent is UpDownBase ud ? ud : c;
    }

    // ---------------- Commands ----------------

    void NextClip()
    {
        _engine.Pause();
        _engine.Seek(TimelineOps.NextClipBoundary(_session.Project, _engine.Position));
        _timeline.EnsurePlayheadVisible();
    }

    void PreviousClip()
    {
        _engine.Pause();
        _engine.Seek(TimelineOps.PreviousClipBoundary(_session.Project, _engine.Position));
        _timeline.EnsurePlayheadVisible();
    }

    /// <summary>The selected clip, else the clip under the playhead.</summary>
    Guid? CurrentClipId() =>
        _timeline.SelectedClipId is { } id && _session.Project.FindClip(id) is not null ? id
        : TimelineOps.Locate(_session.Project, _engine.Position)?.Clip.Id;

    /// <summary>Insert a bin item before/after <paramref name="anchorClipId"/> (or at the end if none), then jump to it.</summary>
    void InsertMedia(Guid mediaId, Guid? anchorClipId, bool after)
    {
        if (_session.Project.FindMedia(mediaId) is { IsAudioOnly: true } audio) { AddAudioAtPlayhead(audio); return; }
        _engine.Pause();
        Clip? inserted = null;
        _session.Execute(p =>
        {
            if (p.FindMedia(mediaId) is not { } m) return;
            int anchor = anchorClipId is { } a ? p.Timeline.FindIndex(c => c.Id == a) : -1;
            int index = anchor < 0 ? p.Timeline.Count : after ? anchor + 1 : anchor;
            inserted = TimelineOps.InsertMedia(p, m, index);
        });
        if (inserted is null) return;
        _timeline.SelectedClipId = inserted.Id;
        _engine.Seek(TimelineOps.ClipStart(_session.Project, inserted.Id));
        _timeline.EnsurePlayheadVisible();
        _timeline.Invalidate();
    }

    void ShowClipMenu(Guid clipId, Point screen)
    {
        var menu = new ContextMenuStrip();
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        var before = new ToolStripMenuItem("Insert before");
        var after = new ToolStripMenuItem("Insert after");
        foreach (var m in _session.Project.Media)
        {
            var id = m.Id;
            before.DropDownItems.Add(m.DisplayName, null, (_, _) => InsertMedia(id, clipId, after: false));
            after.DropDownItems.Add(m.DisplayName, null, (_, _) => InsertMedia(id, clipId, after: true));
        }
        if (_session.Project.Media.Count == 0)
        {
            before.Enabled = after.Enabled = false;
            before.ToolTipText = after.ToolTipText = "Import some media first";
        }
        menu.Items.Add(before);
        menu.Items.Add(after);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Split at playhead (S)", null, (_, _) => Split());
        menu.Items.Add("Delete clip (Del)", null, (_, _) => DeleteSelection());
        menu.Items.Add(new ToolStripSeparator());
        var clip = _session.Project.FindClip(clipId);
        var mute = new ToolStripMenuItem("Mute clip (M)", null, (_, _) => ToggleMute(clipId))
        {
            Checked = clip?.Muted == true,
            Enabled = clip is not null && _session.Project.FindMedia(clip.SourceId)?.HasAudio == true,
        };
        menu.Items.Add(mute);
        menu.Show(screen);
    }

    /// <summary>Put an audio file on the audio track at the playhead (it stays at that timeline time) and select it.</summary>
    void AddAudioAtPlayhead(SourceMedia media)
    {
        _engine.Pause();
        var at = _engine.Position >= TimelineOps.TotalDuration(_session.Project) ? TimeSpan.Zero : _engine.Position;
        AudioItem? item = null;
        _session.Execute(p => item = TimelineOps.AddAudio(p, media, at));
        if (item is null) return;
        _timeline.SelectedClipId = null;
        _timeline.SelectedAnnotationId = null;
        _timeline.SelectedAudioId = item.Id;
        _preview.Select(null);
        UpdatePropertyGrid();
        _timeline.Invalidate();
        _statusText.Text = $"Added {media.DisplayName} to the audio track at {TimelineControl.FormatTime(at, tenths: true)}. Drag it to move it.";
    }

    void ShowAudioMenu(Guid audioId, Point screen)
    {
        if (_session.Project.FindAudio(audioId) is not { } a) return;
        var menu = new ContextMenuStrip();
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Items.Add("Move to playhead", null, (_, _) => _session.Execute(p => TimelineOps.MoveAudio(p, audioId, _engine.Position)));
        menu.Items.Add("Jump to its start", null, (_, _) => { _engine.Pause(); _engine.Seek(a.Start); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Mute (M)", null, (_, _) => ToggleMute(null)) { Checked = a.Muted });
        menu.Items.Add("Remove from audio track (Del)", null, (_, _) => DeleteSelection());
        menu.Show(screen);
    }

    /// <summary>Mute/unmute the selected audio item, else <paramref name="clipId"/>.</summary>
    void ToggleMute(Guid? clipId)
    {
        if (_timeline.SelectedAudioId is { } aid && _session.Project.FindAudio(aid) is { } item)
        {
            bool m = !item.Muted;
            _session.Execute(p => TimelineOps.SetAudioMuted(p, aid, m));
            var n = _session.Project.FindMedia(item.SourceId)?.DisplayName ?? "audio";
            _statusText.Text = m ? $"Muted {n}" : $"Unmuted {n}";
            return;
        }
        if (clipId is not { } id || _session.Project.FindClip(id) is not { } c) return;
        bool muted = !c.Muted;
        _session.Execute(p => TimelineOps.SetClipMuted(p, id, muted));
        var name = _session.Project.FindMedia(c.SourceId)?.DisplayName ?? "clip";
        _statusText.Text = muted ? $"Muted {name}" : $"Unmuted {name}";
    }

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
        // At the very end of the timeline (End key / ▶▶), freeze the last frame.
        var total = TimelineOps.TotalDuration(p);
        if (p.Timeline.Count > 0 && at >= total) at = total - _engine.OneFrame();
        if (TimelineOps.Locate(p, at) is not { } hit || p.FindMedia(hit.Clip.SourceId) is not { } media)
        {
            _preview.ShowNotice("Move the playhead onto a clip first, then press F.");
            return;
        }
        if (media.IsStill)
        {
            _preview.ShowNotice("That's already a still image: drag its right edge to hold it longer.");
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
            MessageBox.Show(this, $"Couldn't grab that frame:\n\n{ex.Message}", "Freeze frame", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            UpdateStatus();
            return;
        }
        _session.Execute(proj => TimelineOps.InsertFreezeFrame(proj, at, still, dlg.Length, dlg.CopyAnnotations));
        StartProxy(still);
        UpdateStatus();
    }

    /// <summary>Remove a file from the bin; if the timeline uses it, confirm first (it takes those clips too). Undoable.</summary>
    void RemoveMedia(Guid mediaId)
    {
        var p = _session.Project;
        if (p.FindMedia(mediaId) is not { } m) return;
        int uses = TimelineOps.UsageCount(p, mediaId);
        if (uses > 0)
        {
            var r = MessageBox.Show(this,
                $"{m.DisplayName} is used {uses} time{(uses == 1 ? "" : "s")} on the timeline.\n\n" +
                "Remove it from the project, along with those clips? (The file on disk isn't deleted, and you can Undo.)",
                "Remove from project", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (r != DialogResult.OK) return;
        }
        _engine.Pause();
        _session.Execute(proj => TimelineOps.RemoveMedia(proj, mediaId));
        if (_timeline.SelectedClipId is { } cid && _session.Project.FindClip(cid) is null) _timeline.SelectedClipId = null;
        if (_timeline.SelectedAudioId is { } aid && _session.Project.FindAudio(aid) is null) _timeline.SelectedAudioId = null;
        UpdatePropertyGrid();
        _engine.Refresh();
        _statusText.Text = $"Removed {m.DisplayName} from the project";
    }

    void DeleteSelection()
    {
        if (_bin.ListFocused && _bin.SelectedMediaId is not null)
        {
            _bin.RequestRemoveSelected();
            return;
        }
        if (_preview.SelectedAnnotationId is { } aid)
        {
            _session.Execute(p => TimelineOps.DeleteAnnotation(p, aid)); // the whole run if it spans cuts
            _preview.Select(null);
        }
        else if (_timeline.SelectedAudioId is { } audioId)
        {
            _session.Execute(p => TimelineOps.DeleteAudio(p, audioId)); // the file stays in the bin
            _timeline.SelectedAudioId = null;
            UpdatePropertyGrid();
        }
        else if (_timeline.SelectedClipId is { } cid)
        {
            _session.Execute(p => TimelineOps.Delete(p, cid));
            _timeline.SelectedClipId = null;
        }
        else
        {
            _statusText.Text = "Click a clip, audio item or annotation first, then press Delete.";
        }
    }

    void ImportDialog()
    {
        using var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = "Import video, images or audio",
            Filter = "Video, images and audio|" + string.Join(";", VideoExtensions.Concat(ImageExtensions).Concat(AudioExtensions).Select(e => "*" + e)) +
                     "|Video|" + string.Join(";", VideoExtensions.Select(e => "*" + e)) +
                     "|Images|" + string.Join(";", ImageExtensions.Select(e => "*" + e)) +
                     "|Audio (music, narration)|" + string.Join(";", AudioExtensions.Select(e => "*" + e)) + "|All files|*.*",
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
            if (media.IsAudioOnly)
            {
                AddAudioAtPlayhead(media); // music / narration goes on the audio track, not the video timeline
            }
            else
            {
                _session.Execute(p => { p.Media.Add(media); TimelineOps.Append(p, media); });
                if (_session.Project.Timeline.Count == 1) _timeline.ZoomToFit();
            }
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
        using var dlg = new NewProjectForm(NewProjectForm.DefaultFolder(_session.FilePath));
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _engine.Pause();
        _session.New(dlg.OutputResolution);
        _engine.Seek(TimeSpan.Zero);
        // Save it straight away, so it has its name from the start (and Ctrl+S just saves).
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dlg.ProjectPath)!);
            _session.Save(dlg.ProjectPath);
            _statusText.Text = $"Created {Path.GetFileName(dlg.ProjectPath)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"The new project couldn't be saved there:\n\n{ex.Message}\n\nIt's open as Untitled; use Save to choose another place.",
                "New project", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
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

    string? _timelineSig;
    string? _mediaSig;

#if DEBUG
    // So the owner can see at a glance which build is running while smoke-testing.
    static readonly string BuildTag = $"  [debug build {File.GetLastWriteTime(typeof(MainForm).Assembly.Location):HH:mm}]";
#else
    const string BuildTag = "";
#endif

    static string TimelineSignature(Project p) =>
        string.Join('|', p.Timeline.Select(c => $"{c.Id}:{c.SourceId}:{c.In.Ticks}:{c.Out.Ticks}"));

    void UpdateUi()
    {
        var name = _session.FilePath is { } p ? Path.GetFileNameWithoutExtension(p) : "Untitled";
        Text = $"{name}{(_session.IsDirty ? " •" : "")} · Flyweight{BuildTag}";
        _undoBtn.Enabled = _session.CanUndo;
        _redoBtn.Enabled = _session.CanRedo;

        // Drop selections that no longer exist (e.g. after undo).
        if (_preview.SelectedAnnotationId is { } aid && _session.Project.FindAnnotation(aid) is null) _preview.Select(null);
        if (_timeline.SelectedClipId is { } cid && _session.Project.FindClip(cid) is null) _timeline.SelectedClipId = null;

        var mediaSig = string.Join('|', _session.Project.Media.Select(m => m.Id));
        if (mediaSig != _mediaSig) { _mediaSig = mediaSig; _bin.Sync(_session.Project, _library); } // status updates come via OnMediaStateChanged
        _timeline.Invalidate();
        _preview.Invalidate();
        UpdatePropertyGrid();
        UpdateToolButtons();
        UpdateTimecode();
        UpdateStatus();
    }

    /// <summary>Picture edits only show if the preview is on that clip: if it isn't, jump to the clip's start.</summary>
    void ShowClipUnderPlayhead(Guid clipId)
    {
        var p = _session.Project;
        if (TimelineOps.Locate(p, _engine.Position) is { } hit && hit.Clip.Id == clipId) return;
        if (p.FindClip(clipId) is null) return;
        _engine.Pause();
        _engine.Seek(TimelineOps.ClipStart(p, clipId));
    }

    void UpdatePropertyGrid()
    {
        var p = _session.Project;
        var a = _preview.SelectedAnnotationId is { } id ? p.FindAnnotation(id) : null;
        var audio = a is null && _timeline.SelectedAudioId is { } aid ? p.FindAudio(aid) : null;
        var clip = a is null && audio is null && _timeline.SelectedClipId is { } cid ? p.FindClip(cid) : null;

        if (a is not null) _props.ShowAnnotation(a); // updates values in place; leaves a focused text box / dragged slider alone
        if (audio is not null) _clipProps.ShowAudio(audio, p.FindMedia(audio.SourceId));
        else if (clip is not null) _clipProps.ShowClip(clip, p.FindMedia(clip.SourceId));
        _props.Visible = a is not null;
        _clipProps.Visible = audio is not null || clip is not null;
        _propsEmpty.Visible = a is null && audio is null && clip is null;
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
        VideoExtensions.Concat(ImageExtensions).Concat(AudioExtensions).Contains(Path.GetExtension(path).ToLowerInvariant());
}
