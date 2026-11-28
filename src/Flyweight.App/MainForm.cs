using Flyweight.App.Controls;
using Flyweight.App.Forms;
using Flyweight.App.Playback;
using Flyweight.App.Rendering;
using Flyweight.Core.Editing;
using Flyweight.Core.Export;
using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.App;

public sealed class MainForm : Form
{
    static readonly string AppDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flyweight");
    static readonly string AutosavePath = Path.Combine(AppDataDir, "autosave" + ProjectSerializer.FileExtension);
    static readonly string LockPath = Path.Combine(AppDataDir, "session.lock");
    static readonly string RecentPath = Path.Combine(AppDataDir, "recent.json");
    string? _lastRecent;
    static readonly string[] VideoExtensions = [".mp4", ".mov", ".m4v", ".mkv", ".avi", ".webm", ".mts", ".m2ts", ".wmv", ".gif"];
    static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tif", ".tiff"];
    static readonly string[] AudioExtensions = [".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma"];

    readonly ProjectSession _session = new();
    Action _fitTimeline = () => { }; // set in the constructor once the split container exists
    bool _selectingFromTimeline;
    bool _marqueeSelecting; // the preview is being synced to a timeline drag-select box: don't let it reset the box
    readonly MediaLibrary _library = new();
    readonly PlaybackEngine _engine;

    readonly PreviewCanvas _preview = new() { Dock = DockStyle.Fill };
    readonly TimelineControl _timeline = new() { Dock = DockStyle.Fill };
    readonly MediaBinControl _bin = new() { Dock = DockStyle.Fill };
    readonly AnnotationStylePanel _props = new() { Dock = DockStyle.Fill, Visible = false };
    readonly ClipPropertiesPanel _clipProps = new() { Dock = DockStyle.Fill, Visible = false };
    readonly OverlayPropertiesPanel _overlayProps = new() { Dock = DockStyle.Fill, Visible = false };
    readonly Label _propsEmpty = new()
    {
        Dock = DockStyle.Fill, ForeColor = Color.Gray, TextAlign = ContentAlignment.MiddleCenter,
        Text = "Select an arrow, box or text\nto edit its style,\nor a clip for its volume.\n\nA  arrow    R  box    T  text\nShift+M  mute clip\n\n" +
               "With a file selected in Media:\nI  insert at playhead\nO  add as overlay",
    };
    // AutoSize: a fixed pixel width clipped "/ total" once Windows display scaling enlarged the font.
    readonly Label _timecode = new()
    {
        AutoSize = true, Padding = new Padding(12, 0, 12, 0), TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.Gainsboro,
        Font = new Font("Consolas", 11f), Dock = DockStyle.Left,
    };
    readonly ToolStripStatusLabel _statusText = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripButton _undoBtn = new("Undo");
    readonly ToolStripButton _redoBtn = new("Redo");
    readonly Dictionary<Tool, ToolStripButton> _toolButtons = [];
    readonly Theme.RoundButton _playBtn = new() { Text = "▶", Width = 52, Dock = DockStyle.Left, Primary = true };
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

        ToolStripManager.Renderer = new Theme.ToolRenderer(); // right-click menus match the dark toolbar
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
        // Dark toolbar matching the rest of the app (Theme.ToolRenderer: rounded hover, accent for the current tool).
        var tools = new ToolStrip
        {
            GripStyle = ToolStripGripStyle.Hidden, Renderer = new Theme.ToolRenderer(), Padding = new Padding(8, 4, 8, 4),
            BackColor = Theme.Bar, ForeColor = Theme.Text,
        };
        // After a click WinForms keeps the button "Selected" and doesn't repaint it when the mouse moves off, so the
        // hover highlight stuck on Split/Freeze/Delete. The renderer only highlights the button under the pointer;
        // repaint whenever the pointer moves to another button or leaves the bar.
        ToolStripItem? hovered = null;
        tools.MouseMove += (_, e) =>
        {
            var item = tools.GetItemAt(e.Location);
            if (item != hovered) { hovered = item; tools.Invalidate(); }
        };
        tools.MouseLeave += (_, _) => { hovered = null; tools.Invalidate(); };
        ToolStripButton Btn(string text, string tip, Action click)
        {
            var b = new ToolStripButton(text) { ToolTipText = tip, DisplayStyle = ToolStripItemDisplayStyle.Text, ForeColor = Theme.Text, Padding = new Padding(4, 2, 4, 2) };
            b.Click += (_, _) =>
            {
                click();
                b.Owner?.Invalidate(); // after a dialog closes, repaint so the old hover highlight goes
            };
            return b;
        }
        _undoBtn.Click += (_, _) => _session.Undo();
        _redoBtn.Click += (_, _) => _session.Redo();
        _undoBtn.ToolTipText = "Undo (Ctrl+Z)";
        _redoBtn.ToolTipText = "Redo (Ctrl+Y)";
        _undoBtn.ForeColor = _redoBtn.ForeColor = Theme.Text; // the renderer greys them out when disabled

        tools.Items.AddRange(
        [
            Btn("New", "New project", NewProject),
            OpenButton(),
            SaveButton(),
            new ToolStripSeparator(),
            Btn("Import…", "Import video, images or audio (Ctrl+I)", ImportDialog),
            new ToolStripSeparator(),
            _undoBtn, _redoBtn,
            new ToolStripSeparator(),
        ]);
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
        tools.Items.AddRange(
        [
            new ToolStripSeparator(),
            Btn("Split", "Split at playhead (S): the selected overlay or audio item, else the main clip. Shift+S splits every track", Split),
            Btn("Freeze", "Freeze this frame: hold it for a few seconds (F)", () => _ = FreezeFrameAsync()),
            Btn("Delete", "Delete the selection (Del). Clips leave a gap, so nothing else moves", () => DeleteSelection(ripple: false)),
            Btn("Ripple delete", "Delete the selection and close the gap (Shift+Del): later clips, overlays and audio move up to fill it", () => DeleteSelection(ripple: true)),
            Btn("Fit", "Zoom timeline to fit (Ctrl+0)", () => _timeline.ZoomToFit()),
            new ToolStripSeparator(),
            Btn("Chapter", "Add a chapter marker at the playhead (M): they become the 0:00 timestamps in your YouTube description", AddMarker),
            Btn("Chapters…", "See, copy or save your YouTube chapter timestamps (Ctrl+Shift+C copies)", ShowChapters),
            new ToolStripSeparator(),
            Btn("Save frame", "Save the frame under the playhead as a PNG (with zoom, brightness and annotations) next to your footage (Ctrl+Shift+E). Right-click the preview for a YouTube thumbnail", () => _ = ExportFrameAsync(chooseLocation: false)),
            Btn("Export for YouTube…", "Export MP4 (Ctrl+E)", Export),
        ]);

        // Right-click on the preview: freeze this frame, or drop a chapter marker here.
        var previewMenu = new ContextMenuStrip();
        previewMenu.Items.Add("Save this frame as an image (Ctrl+Shift+E)", null, (_, _) => _ = ExportFrameAsync(chooseLocation: false));
        previewMenu.Items.Add("Save this frame as…", null, (_, _) => _ = ExportFrameAsync(chooseLocation: true));
        // Shorts don't take 16:9 custom thumbnails, so vertical projects hide this (D41); Save frame still works.
        var thumb = new ToolStripMenuItem("Save as YouTube thumbnail (1280×720 JPG, under 2 MB)", null, (_, _) => _ = ExportFrameAsync(chooseLocation: false, thumbnail: true));
        previewMenu.Items.Add(thumb);
        previewMenu.Items.Add(new ToolStripSeparator());
        previewMenu.Items.Add("Freeze this frame (F)", null, (_, _) => _ = FreezeFrameAsync());
        previewMenu.Items.Add("Add chapter marker here (M)", null, (_, _) => AddMarker());
        previewMenu.Items.Add(new ToolStripSeparator());
        var landscape = new ToolStripMenuItem("Landscape 16:9", null, (_, _) => ChangeShape(CanvasShape.Landscape));
        var vertical = new ToolStripMenuItem("Vertical 9:16 (Shorts)", null, (_, _) => ChangeShape(CanvasShape.Vertical));
        previewMenu.Items.Add(new ToolStripMenuItem("Frame shape", null, landscape, vertical));
        previewMenu.Opening += (_, e) =>
        {
            e.Cancel = _session.Project.Timeline.Count == 0;
            bool isVertical = _session.Project.Shape == CanvasShape.Vertical;
            thumb.Visible = !isVertical;
            landscape.Checked = !isVertical;
            vertical.Checked = isVertical;
        };
        _preview.ContextMenuStrip = previewMenu;

        // Transport bar under the preview
        var transport = new Panel { Dock = DockStyle.Bottom, Height = 42, BackColor = Theme.Bar, Padding = new Padding(8, 4, 8, 4) };
        Button TBtn(string text, string tip, Action click, int width = 46)
        {
            var b = new Theme.RoundButton { Text = text, Width = width, Dock = DockStyle.Left, ForeColor = Color.White };
            new ToolTip().SetToolTip(b, tip);
            b.Click += (_, _) => { click(); _preview.Focus(); };
            return b;
        }
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
        propsPanel.Controls.Add(_overlayProps);
        propsPanel.Controls.Add(_propsEmpty);
        propsPanel.Controls.Add(new Theme.PanelHeader("Properties"));

        var centreRight = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2, SplitterWidth = 4 };
        centreRight.Panel1.Controls.Add(previewPanel);
        centreRight.Panel2.Controls.Add(propsPanel);

        var top = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = 4 };
        top.Panel1.Controls.Add(_bin);
        top.Panel2.Controls.Add(centreRight);

        var main = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2, SplitterWidth = 4 };
        main.Panel1.Controls.Add(top);
        main.Panel2.Controls.Add(_timeline);

        var status = new StatusStrip { SizingGrip = true, Renderer = new Theme.ToolRenderer(), BackColor = Theme.Bar, ForeColor = Theme.Text };
        status.Items.Add(_statusText);

        Controls.Add(main);
        Controls.Add(tools);
        Controls.Add(status);

        // The timeline panel grows to fit more annotation lanes / overlay tracks (up to half the window; past that it
        // scrolls) and shrinks back as they go, but never below the height the user last dragged it to.
        // Refits run synchronously right after each edit (so a text box's inline editor opens on the final layout),
        // never mid-drag on the timeline (the preview would jump around under the drag), and on window resize.
        int timelineBaseH = 290;
        bool sizingTimeline = false;
        void FitTimeline()
        {
            if (_timeline.IsDragging) return; // refits on mouse-up (PreferredHeightChanged)
            // Too small to split (minimised / tiny window): setting SplitterDistance would throw.
            if (main.Height < main.Panel1MinSize + main.Panel2MinSize + main.SplitterWidth + 200) return;
            int cap = Math.Max(timelineBaseH, main.Height / 2);
            int want = Math.Clamp(_timeline.PreferredHeight, timelineBaseH, cap);
            int distance = Math.Max(200, main.Height - main.SplitterWidth - want);
            if (distance == main.SplitterDistance) return;
            sizingTimeline = true;
            try { main.SplitterDistance = distance; }
            catch (InvalidOperationException) { } // a layout we can't apply right now; the next edit retries
            finally { sizingTimeline = false; }
        }
        _fitTimeline = FitTimeline;
        _timeline.PreferredHeightChanged += (_, _) => FitTimeline();
        main.Resize += (_, _) => FitTimeline();
        main.SplitterMoved += (_, _) =>
        {
            if (!sizingTimeline) timelineBaseH = Math.Max(120, main.Height - main.SplitterWidth - main.SplitterDistance);
        };

        // SplitContainer distances can only be set once the controls have a size.
        Load += (_, _) =>
        {
            sizingTimeline = true;
            main.SplitterDistance = Math.Max(200, main.Height - 290); // ruler + overlay track + video + audio track + annotation lanes
            sizingTimeline = false;
            FitTimeline();
            // Side panels sized for the display scaling (fixed pixels cramped them at 150%), but never more than a
            // quarter of the window each, so the preview keeps most of the room.
            top.SplitterDistance = Math.Min(LogicalToDeviceUnits(270), top.Width / 4);
            centreRight.SplitterDistance = Math.Max(300, centreRight.Width - Math.Min(LogicalToDeviceUnits(330), top.Width / 4));
        };
    }

    // ---------------- Events ----------------

    void WireEvents()
    {
        _session.Changed += (_, _) =>
        {
            // Whatever gave the project its file (open, save, save as, new), it goes to the top of the recent list.
            if (_session.FilePath is { } fp && fp != _lastRecent && !string.Equals(fp, AutosavePath, StringComparison.OrdinalIgnoreCase))
            {
                _lastRecent = fp;
                RecentProjects.Save(RecentPath, RecentProjects.Add(RecentProjects.Load(RecentPath), fp));
            }
            // Only re-seek the player when the CLIPS changed. Annotation/style edits are drawn on top, and
            // re-seeking on every mouse move of an annotation drag queued dozens of VLC seeks (laggy play after).
            var sig = TimelineSignature(_session.Project);
            if (sig != _timelineSig) { _timelineSig = sig; _engine.Refresh(); }
            _fitTimeline(); // new lane / track: grow the timeline now, before e.g. a text editor is placed
            UpdateUi();
        };
        _library.StateChanged += (_, id) => { if (IsHandleCreated) BeginInvoke(() => OnMediaStateChanged(id)); };
        _engine.PositionChanged += (_, _) =>
        {
            UpdateTimecode();
            // Any playhead move (play, step, seek, clip jump) brings it back into view. Scrolling or
            // zooming the timeline alone doesn't move the playhead, so it doesn't fight the user.
            _timeline.EnsurePlayheadVisible();
            FollowPlayheadClip();
            _preview.Invalidate();
        };
        _engine.PlayingChanged += (_, _) => UpdateTimecode(); // flips the ▶ / ❚❚ button

        _timeline.SeekRequested += (_, t) => { _engine.Pause(); _engine.Seek(t); };
        _timeline.AnnotationSelected += (_, _) =>
        {
            // Clicked on the timeline: it's already in view, so don't scroll it (that yanked a long bar from under the drag).
            _selectingFromTimeline = true;
            try { _preview.Select(_timeline.SelectedAnnotationId); } finally { _selectingFromTimeline = false; }
        };
        _timeline.ClipSelected += (_, _) =>
        {
            if (_timeline.SelectedClipId is not null) _preview.Select(null);
            UpdatePropertyGrid();
        };
        _timeline.MarqueeSelected += (_, _) =>
        {
            // Show a lone annotation in the preview (so its style panel opens); otherwise nothing is selected there.
            // The preview's SelectionChanged would reset the timeline's selection to that one item, so skip it.
            var annos = _timeline.SelectedAnnotationIds;
            bool onlyOneAnnotation = annos.Count == 1 && _timeline.SelectionCount == 1;
            _marqueeSelecting = true;
            try { _preview.Select(onlyOneAnnotation ? annos.First() : null); } finally { _marqueeSelecting = false; }
            UpdatePropertyGrid();
            int n = _timeline.SelectionCount;
            _statusText.Text = n == 0 ? "Nothing selected."
                : $"Selected {n} {(n == 1 ? "item" : "items")}: Del deletes (clips leave a gap), Shift+Del ripple deletes. Shift+drag adds more.";
        };
        _timeline.MarkerSelected += (_, _) => { if (_timeline.SelectedMarkerId is not null) _preview.Select(null); UpdatePropertyGrid(); };
        _timeline.MarkerRenameRequested += (_, id) => RenameMarker(id);
        _preview.SelectionChanged += (_, _) =>
        {
            if (_marqueeSelecting) { UpdatePropertyGrid(); return; } // the timeline's box selection stands as it is
            _timeline.SelectedAnnotationId = _preview.SelectedAnnotationId;
            if (_preview.SelectedAnnotationId is { } sel)
            {
                _timeline.SelectedClipId = _timeline.SelectedAudioId = _timeline.SelectedOverlayId = null;
                if (!_selectingFromTimeline) _timeline.RevealAnnotation(sel); // a new text box may land in a lane out of view
            }
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
            var clips = SelectedClips();
            Action<Project>? apply = null;
            if (_timeline.SelectedAudioId is { } aid) apply = p => set(p, aid, true);
            else if (clips.Count > 0) apply = p => { foreach (var cid in clips) set(p, cid, false); }; // every selected clip, one undo step
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
            var clips = SelectedClips();
            Action<Project, Guid> one = which switch
            {
                LookSetting.Zoom => (p, id) => TimelineOps.SetClipZoom(p, id, v),
                LookSetting.PanX => (p, id) => TimelineOps.SetClipPan(p, id, v, null),
                LookSetting.PanY => (p, id) => TimelineOps.SetClipPan(p, id, null, v),
                LookSetting.Brightness => (p, id) => TimelineOps.SetClipBrightness(p, id, v),
                LookSetting.CropLeft => (p, id) => TimelineOps.SetClipCrop(p, id, CropSide.Left, v),
                LookSetting.CropRight => (p, id) => TimelineOps.SetClipCrop(p, id, CropSide.Right, v),
                LookSetting.CropTop => (p, id) => TimelineOps.SetClipCrop(p, id, CropSide.Top, v),
                LookSetting.CropBottom => (p, id) => TimelineOps.SetClipCrop(p, id, CropSide.Bottom, v),
                _ => (p, id) => TimelineOps.SetClipContrast(p, id, v),
            };
            Action<Project> apply = p => { foreach (var id in clips) one(p, id); };
            switch (phase)
            {
                case StyleEditPhase.Single: _session.Execute(apply); break;
                case StyleEditPhase.Live: _session.BeginEdit(); _session.Preview(apply); break;
                case StyleEditPhase.Commit: _session.Preview(apply); _session.CommitEdit(); break;
            }
            ShowClipUnderPlayhead(cid);
        };
        _clipProps.SpeedChanged += speed =>
        {
            if (_timeline.SelectedClipId is not { } cid) return;
            var clips = SelectedClips();
            _session.Execute(p => { foreach (var id in clips) TimelineOps.SetClipSpeed(p, id, speed); });
            ShowClipUnderPlayhead(cid);
            _statusText.Text = $"Speed {speed:0.##}×" + (clips.Count > 1 ? $" for {clips.Count} clips" : "");
        };
        _clipProps.StillLengthChanged += secs =>
        {
            if (_timeline.SelectedClipId is not { } cid) return;
            var clips = SelectedClips(); // SetStillLength ignores any videos in a mixed selection
            _session.Execute(p => { foreach (var id in clips) TimelineOps.SetStillLength(p, id, TimeSpan.FromSeconds(secs)); });
            _timeline.Invalidate();
            _statusText.Text = $"Image shows for {secs:0.0}s; everything after it moved along" + (clips.Count > 1 ? $" (all selected images)" : "") + ".";
        };
        _clipProps.LookReset += () =>
        {
            if (_timeline.SelectedClipId is not { } cid) return;
            var clips = SelectedClips();
            _session.Execute(p => { foreach (var id in clips) TimelineOps.ResetClipLook(p, id); });
            ShowClipUnderPlayhead(cid);
        };

        _timeline.AudioSelected += (_, _) =>
        {
            if (_timeline.SelectedAudioId is not null) _preview.Select(null);
            UpdatePropertyGrid();
        };
        _timeline.AudioMenuRequested += ShowAudioMenu;

        // Overlay tracks: select on the timeline or by clicking it in the preview; edit in its own panel.
        _timeline.OverlaySelected += (_, _) =>
        {
            if (_timeline.SelectedOverlayId is not null) _preview.Select(null);
            UpdatePropertyGrid();
        };
        _timeline.OverlayMenuRequested += ShowOverlayMenu;
        _preview.OverlayClicked += id =>
        {
            if (id is not null || _timeline.SelectedOverlayId is not null) _timeline.SelectOverlay(id);
        };
        _overlayProps.Edit += (edit, phase) =>
        {
            if (_timeline.SelectedOverlayId is not { } oid) return;
            Action<Project> apply = p => edit(p, oid);
            switch (phase)
            {
                case StyleEditPhase.Single: _session.Execute(apply); break;
                case StyleEditPhase.Live: _session.BeginEdit(); _session.Preview(apply); break;
                case StyleEditPhase.Commit: _session.Preview(apply); _session.CommitEdit(); break;
            }
            ShowOverlayUnderPlayhead(oid);
        };
        _bin.IsAudio = id => _session.Project.FindMedia(id) is { IsAudioOnly: true };
        _bin.InsertAtPlayheadRequested += InsertAtPlayhead;
        _bin.AddOverlayRequested += AddOverlayAtPlayhead;
        _bin.AddToTimelineRequested += AddToTimeline;
        _bin.InsertRequested += (ids, after) => InsertMedia(ids, CurrentClipId(), after);
        _bin.RemoveRequested += RemoveMedia;
        _timeline.ClipMenuRequested += ShowClipMenu;

        DragEventHandler dragEnter = (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragEventHandler dragDrop = async (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files)
            {
                var project = files.FirstOrDefault(f => f.EndsWith(ProjectSerializer.FileExtension, StringComparison.OrdinalIgnoreCase));
                if (project is not null) { if (ConfirmDiscard()) LoadProject(project); }
                else await ImportAsync(files.Where(IsImportable));
            }
        };
        DragEnter += dragEnter;
        DragDrop += dragDrop;
        _bin.AcceptDrops(dragEnter, dragDrop); // the bin says "drop files here", so it must take them too

        _autosave.Tick += (_, _) => Autosave();
        _autosave.Start();

        Shown += (_, _) =>
        {
            // "Flyweight.exe My video.flyweight" (Open with…, or a shortcut) opens that project.
            var arg = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(f => f.EndsWith(ProjectSerializer.FileExtension, StringComparison.OrdinalIgnoreCase));
            if (arg is not null && !File.Exists(arg)) arg = null;
            // Recovery comes first, even when a project was double-clicked: loading it first and letting autosave run
            // would overwrite the crashed session's autosave, losing that work for good.
            bool recovered = ClaimSession() && OfferCrashRecovery(arg);
            if (recovered && arg is not null)
                _statusText.Text = $"Recovered your autosaved work. Save it, then open \"{Path.GetFileName(arg)}\" with File > Open.";
            else if (arg is not null) LoadProject(arg);
            if (!_ownsSession) _statusText.Text = "Another Flyweight window is open, so this one doesn't autosave. Save with Ctrl+S.";
        };
        FormClosing += (_, e) =>
        {
            _preview.CommitTextEdit();
            if (!ConfirmDiscard()) { e.Cancel = true; return; }
            _engine.Dispose();
            if (_ownsSession) // a second window must never delete the first one's lock or autosave
            {
                TryDelete(LockPath);
                TryDelete(AutosavePath);
            }
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
            // Bigger jumps for moving around inside a long clip.
            case Keys.Control | Keys.Left: case Keys.PageUp: _engine.Seek(_engine.Position - TimeSpan.FromSeconds(10)); return true;
            case Keys.Control | Keys.Right: case Keys.PageDown: _engine.Seek(_engine.Position + TimeSpan.FromSeconds(10)); return true;
            case Keys.Home: _engine.Seek(TimeSpan.Zero); return true;
            case Keys.End: _engine.Seek(TimelineOps.TotalDuration(_session.Project)); return true;
            case Keys.S: Split(); return true;
            case Keys.Shift | Keys.S: SplitAll(); return true;
            case Keys.M: case Keys.C: AddMarker(); return true; // M = marker (as in Premiere/Resolve/Final Cut); C kept as an alias
            case Keys.Control | Keys.Shift | Keys.C: CopyChapters(); return true;
            case Keys.Control | Keys.Shift | Keys.E: _ = ExportFrameAsync(chooseLocation: false); return true;
            case Keys.F: _ = FreezeFrameAsync(); return true;
            case Keys.I: WithBinSelection(InsertAtPlayhead, "I"); return true;
            case Keys.O: WithBinSelection(AddOverlayAtPlayhead, "O"); return true;
            // Ctrl+M too: Windows turns it into Enter, which in the media bin re-added the selected file.
            case Keys.Shift | Keys.M: case Keys.Control | Keys.M: ToggleMute(CurrentClipId()); return true;
            // Only Delete deletes. Backspace used to too, but it's easy to hit by accident and a clip vanished (2026-11-26).
            case Keys.Delete: DeleteSelection(ripple: false); return true;                // leaves a gap
            case Keys.Shift | Keys.Delete: DeleteSelection(ripple: true); return true;    // closes it
            case Keys.V: case Keys.Escape: _preview.Tool = Tool.Select; return true;
            case Keys.A: _preview.Tool = Tool.Arrow; return true;
            case Keys.R: _preview.Tool = Tool.Box; return true;
            case Keys.T: _preview.Tool = Tool.Text; return true;
            case Keys.Control | Keys.Z: _session.Undo(); return true;
            case Keys.Control | Keys.Y: case Keys.Control | Keys.Shift | Keys.Z: _session.Redo(); return true;
            case Keys.Control | Keys.S: SaveProject(false); return true;
            case Keys.Control | Keys.C: CopyClips(); return true;
            case Keys.Control | Keys.V: PasteClips(); return true;
            case Keys.Control | Keys.A:
                _preview.Select(null);
                _timeline.SelectAllClips(TimelineOps.Locate(_session.Project, _engine.Position)?.Clip.Id);
                _statusText.Text = $"Selected all {_session.Project.Timeline.Count} clips: Properties, M and Delete apply to all";
                return true;
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
    // ---------------- Copy / paste clips ----------------

    TimelineOps.ClipCopy? _clipCopy;

    /// <summary>Ctrl+C: copy the selected clip(s), or the clip under the playhead, with their annotations.</summary>
    void CopyClips()
    {
        var ids = SelectedClips();
        if (ids.Count == 0 && CurrentClipId() is { } one) ids.Add(one);
        if (TimelineOps.CopyClips(_session.Project, ids) is not { } copy) { _statusText.Text = "Click a clip first, then press Ctrl+C."; return; }
        _clipCopy = copy;
        _statusText.Text = $"Copied {(copy.Clips.Count == 1 ? "1 clip" : $"{copy.Clips.Count} clips")}. Ctrl+V pastes after the current clip.";
    }

    /// <summary>Ctrl+V: insert the copied clip(s) right after the current clip (one undo step), select them and jump there.</summary>
    void PasteClips()
    {
        if (_clipCopy is not { } copy) { _statusText.Text = "Copy a clip first (Ctrl+C), then paste with Ctrl+V."; return; }
        _engine.Pause();
        var p = _session.Project;
        int index = CurrentClipId() is { } anchor ? p.Timeline.FindIndex(c => c.Id == anchor) + 1 : p.Timeline.Count;
        List<Core.Model.Clip> pasted = [];
        _session.Execute(proj => pasted = TimelineOps.PasteClips(proj, copy, index));
        if (pasted.Count == 0) { _statusText.Text = "Nothing pasted: the copied clip's file was removed from the project."; return; }
        _timeline.SelectClips(pasted.Select(c => c.Id));
        _engine.Seek(TimelineOps.ClipStart(_session.Project, pasted[0].Id));
        UpdatePropertyGrid();
        _statusText.Text = $"Pasted {(pasted.Count == 1 ? "1 clip" : $"{pasted.Count} clips")} (Ctrl+Z to undo)";
    }

    Guid? _playheadClipId;

    /// <summary>
    /// With one clip selected, moving the playhead into another clip selects that one, so the Properties panel always
    /// shows the clip you're looking at (owner, 2026-11-28). Only when the playhead CROSSES into another clip, so
    /// clicking a clip elsewhere (e.g. to trim it) isn't undone by the next position update. Gaps don't take it.
    /// </summary>
    void FollowPlayheadClip()
    {
        var under = TimelineOps.Locate(_session.Project, _engine.Position)?.Clip;
        if (under?.Id == _playheadClipId) return;
        _playheadClipId = under?.Id;
        if (under is null || under.IsGap || _timeline.IsDragging) return;
        if (_timeline.SelectedClipIds.Count != 1 || _timeline.SelectedClipId is not { } cur || cur == under.Id) return;
        _timeline.SelectClips([under.Id]); // raises ClipSelected: the Properties panel follows
    }

    Guid? CurrentClipId() =>
        _timeline.SelectedClipId is { } id && _session.Project.FindClip(id) is not null ? id
        : TimelineOps.Locate(_session.Project, _engine.Position)?.Clip.Id;

    /// <summary>Insert a bin item before/after <paramref name="anchorClipId"/> (or at the end if none), then jump to it.</summary>
    void InsertMedia(Guid mediaId, Guid? anchorClipId, bool after) => InsertMedia([mediaId], anchorClipId, after);

    /// <summary>
    /// Insert bin files, in order, before/after <paramref name="anchorClipId"/> (or at the end if none), as one undo
    /// step, then select them and jump to the first. Audio files go on the audio track at the playhead, one after another.
    /// </summary>
    void InsertMedia(IReadOnlyList<Guid> mediaIds, Guid? anchorClipId, bool after)
    {
        _engine.Pause();
        var inserted = new List<Clip>();
        int audio = 0;
        var audioAt = AudioStartAtPlayhead();
        _session.Execute(p =>
        {
            int anchor = anchorClipId is { } a ? p.Timeline.FindIndex(c => c.Id == a) : -1;
            int index = anchor < 0 ? p.Timeline.Count : after ? anchor + 1 : anchor;
            foreach (var id in mediaIds)
            {
                if (p.FindMedia(id) is not { } m) continue;
                if (m.IsAudioOnly) { audioAt = TimelineOps.AddAudio(p, m, audioAt).End; audio++; continue; }
                inserted.Add(TimelineOps.InsertMedia(p, m, index++));
            }
        });
        AfterBinInsert(inserted, audio);
    }

    /// <summary>Where audio from the bin goes: the playhead, or the start if the playhead is at the very end.</summary>
    TimeSpan AudioStartAtPlayhead() =>
        _engine.Position >= TimelineOps.TotalDuration(_session.Project) ? TimeSpan.Zero : _engine.Position;

    /// <summary>After adding bin files: select the new clips, jump to the first, and say what happened.</summary>
    void AfterBinInsert(List<Clip> inserted, int audio, string where = "")
    {
        if (inserted.Count > 0)
        {
            _timeline.SelectClips(inserted.Select(c => c.Id));
            _engine.Seek(TimelineOps.ClipStart(_session.Project, inserted[0].Id));
            _timeline.EnsurePlayheadVisible();
        }
        _preview.Select(null);
        UpdatePropertyGrid();
        _timeline.Invalidate();
        var parts = new List<string>();
        if (inserted.Count > 0) parts.Add(inserted.Count == 1 ? (_session.Project.FindMedia(inserted[0].SourceId)?.DisplayName ?? "1 clip") : $"{inserted.Count} clips");
        if (audio > 0) parts.Add(audio == 1 ? "1 audio file on the audio track" : $"{audio} audio files on the audio track");
        if (parts.Count > 0) _statusText.Text = $"Added {string.Join(" and ", parts)}{where} (Ctrl+Z to undo)";
    }

    /// <summary>Double-click / "Add to end of timeline" in the bin: videos and images go on the end, audio at the playhead.</summary>
    void AddToTimeline(IReadOnlyList<Guid> mediaIds) => InsertMedia(mediaIds, anchorClipId: null, after: true);

    void ShowClipMenu(Guid clipId, Point screen)
    {
        var menu = new ContextMenuStrip();
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        var atPlayhead = new ToolStripMenuItem("Insert at playhead") { ToolTipText = "Splits the clip at the playhead and puts the file in between" };
        var before = new ToolStripMenuItem("Insert before");
        var after = new ToolStripMenuItem("Insert after");
        var overlay = new ToolStripMenuItem("Add as overlay at playhead") { ToolTipText = "Picture-in-picture over this clip (overlay track)" };
        foreach (var m in _session.Project.Media.Where(m => !m.IsAudioOnly))
        {
            var id = m.Id;
            atPlayhead.DropDownItems.Add(m.DisplayName, null, (_, _) => InsertAtPlayhead(id));
            before.DropDownItems.Add(m.DisplayName, null, (_, _) => InsertMedia(id, clipId, after: false));
            after.DropDownItems.Add(m.DisplayName, null, (_, _) => InsertMedia(id, clipId, after: true));
            overlay.DropDownItems.Add(m.DisplayName, null, (_, _) => AddOverlayAtPlayhead(id));
        }
        if (atPlayhead.DropDownItems.Count == 0)
        {
            atPlayhead.Enabled = before.Enabled = after.Enabled = overlay.Enabled = false;
            atPlayhead.ToolTipText = before.ToolTipText = after.ToolTipText = overlay.ToolTipText = "Import some video or images first";
        }
        menu.Items.Add(atPlayhead);
        menu.Items.Add(before);
        menu.Items.Add(after);
        menu.Items.Add(overlay);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Split at playhead (S)", null, (_, _) => Split());
        menu.Items.Add("Split all tracks at playhead (Shift+S)", null, (_, _) => SplitAll());
        menu.Items.Add(new ToolStripMenuItem("Copy (Ctrl+C)", null, (_, _) => CopyClips()));
        menu.Items.Add(new ToolStripMenuItem("Paste after this clip (Ctrl+V)", null, (_, _) => PasteClips()) { Enabled = _clipCopy is not null });
        var clip = _session.Project.FindClip(clipId);
        bool gap = clip is { IsGap: true };
        menu.Items.Add(new ToolStripMenuItem(gap ? "Close gap (Del)" : "Delete, leave a gap (Del)", null, (_, _) => DeleteSelection(ripple: false))
        {
            ToolTipText = gap ? "Removes the gap: everything after it moves up." : "Removes the clip and leaves a gap in its place, so nothing else moves.",
        });
        menu.Items.Add(new ToolStripMenuItem("Ripple delete (Shift+Del)", null, (_, _) => DeleteSelection(ripple: true))
        {
            ToolTipText = "Removes it and closes the gap: later clips, overlays and audio move up together, staying in sync.",
        });
        menu.Items.Add(new ToolStripSeparator());
        var mute = new ToolStripMenuItem("Mute clip (Shift+M)", null, (_, _) => ToggleMute(clipId))
        {
            Checked = clip?.Muted == true,
            Enabled = clip is not null && _session.Project.FindMedia(clip.SourceId)?.HasAudio == true,
        };
        menu.Items.Add(mute);
        var why = TimelineOps.CannotSeparateAudio(_session.Project, clipId);
        var whyNotLink = TimelineOps.CannotLinkAudio(_session.Project, clipId);
        if (whyNotLink is null && clip is { Muted: true })
            menu.Items.Add(new ToolStripMenuItem("Link audio", null, (_, _) => LinkAudio(clipId))
            {
                ToolTipText = "Puts this clip's sound back on the clip (from the audio track), so it moves and deletes with the picture again.",
            });
        else
            menu.Items.Add(new ToolStripMenuItem("Unlink audio", null, (_, _) => SeparateAudio(clipId))
            {
                Enabled = why is null,
                ToolTipText = why ?? "Moves this clip's sound onto the audio track, where you can move, trim or keep it on its own " +
                                     "(e.g. delete the picture and keep the sound). Link audio puts it back.",
            });
        menu.Items.Add(new ToolStripSeparator());
        // Vertical projects (D45): a wide clip fills the 9:16 frame; this shows all of it with bars instead.
        if (_session.Project.FindClip(clipId) is { } fc && ClipLook.CanFill(_session.Project, _session.Project.FindMedia(fc.SourceId)))
        {
            var targets = SelectedClips() is { } sel && sel.Contains(clipId) ? sel : [clipId];
            bool fitWhole = fc.FitWhole, cropped = ClipLook.IsCropped(fc);
            menu.Items.Add(new ToolStripMenuItem("Fit whole picture (bars)", null, (_, _) =>
                _session.Execute(p => TimelineOps.SetClipFitWhole(p, targets, !fitWhole)))
            {
                Checked = fitWhole || cropped, // a cropped clip always shows its whole (cropped) picture
                Enabled = !cropped,
                ToolTipText = cropped
                    ? "This clip is cropped, so it shows the whole cropped picture with bars. Reset the crop to fill the frame again."
                    : "Off: the picture fills the vertical frame (drag the preview to choose which part). " +
                      "On: the whole picture fits inside, with black or blurred bars above and below.",
            });
        }
        bool blur = _session.Project.Background == BackgroundFill.Blur;
        menu.Items.Add(new ToolStripMenuItem("Blurred bars behind portrait clips (whole project)", null, (_, _) =>
            _session.Execute(p => TimelineOps.SetBackgroundFill(p, blur ? BackgroundFill.Black : BackgroundFill.Blur)))
        {
            Checked = blur,
            ToolTipText = "Fills the black bars around clips that aren't 16:9 with a blurred copy of the clip. Zoomed or cropped clips keep their own framing.",
        });
        menu.Show(screen);
    }

    /// <summary>Run a bin command (I / O) on the files selected in the media bin.</summary>
    void WithBinSelection(Action<IReadOnlyList<Guid>> command, string key)
    {
        if (_bin.SelectedMediaIds is { Count: > 0 } ids) command(ids);
        else _statusText.Text = $"Select a file in Media first, then press {key}.";
    }

    void InsertAtPlayhead(Guid mediaId) => InsertAtPlayhead([mediaId]);

    /// <summary>
    /// Insert bin files at the playhead, in order, as one undo step: the clip there is split and the files go in
    /// between, one after another. Audio files go on the audio track from the playhead, one after another.
    /// </summary>
    void InsertAtPlayhead(IReadOnlyList<Guid> mediaIds)
    {
        _engine.Pause();
        var at = _engine.Position;
        var inserted = new List<Clip>();
        int audio = 0;
        var audioAt = AudioStartAtPlayhead();
        _session.Execute(p =>
        {
            var t = at;
            foreach (var id in mediaIds)
            {
                if (p.FindMedia(id) is not { } m) continue;
                if (m.IsAudioOnly) { audioAt = TimelineOps.AddAudio(p, m, audioAt).End; audio++; continue; }
                var c = TimelineOps.InsertMediaAt(p, m, t);
                inserted.Add(c);
                t = TimelineOps.ClipStart(p, c.Id) + c.Duration; // the next file goes right after this one
            }
        });
        AfterBinInsert(inserted, audio, $" at {TimelineControl.FormatTime(at, tenths: true)}");
    }

    void AddOverlayAtPlayhead(Guid mediaId) => AddOverlayAtPlayhead([mediaId]);

    /// <summary>
    /// Put bin files on the overlay tracks at the playhead, as picture-in-picture (one undo step). Several files stack
    /// on the lowest free tracks; audio files go on the audio track instead. The last overlay is selected.
    /// </summary>
    void AddOverlayAtPlayhead(IReadOnlyList<Guid> mediaIds)
    {
        var p0 = _session.Project;
        var media = mediaIds.Select(id => p0.FindMedia(id)).OfType<SourceMedia>().ToList();
        if (media.Count == 0) return;
        var total = TimelineOps.TotalDuration(p0);
        if (media.Any(m => !m.IsAudioOnly) && total <= TimeSpan.Zero)
        {
            _statusText.Text = "Overlays go over the main video: add a clip to the timeline first.";
            return;
        }
        _engine.Pause();
        var at = _engine.Position >= total ? TimeSpan.Zero : _engine.Position;
        var audioAt = AudioStartAtPlayhead();
        var added = new List<OverlayClip>();
        int audio = 0;
        _session.Execute(proj =>
        {
            foreach (var m in media)
            {
                if (m.IsAudioOnly) { audioAt = TimelineOps.AddAudio(proj, m, audioAt).End; audio++; continue; }
                var length = m.IsStill ? TimelineOps.DefaultStillLength : m.Duration;
                added.Add(TimelineOps.AddOverlay(proj, m, at, TimelineOps.FreeOverlayTrack(proj, at, length)));
            }
        });
        if (added.Count > 0) { _timeline.SelectOverlay(added[^1].Id); _engine.Seek(at); }
        else _timeline.Invalidate();
        _statusText.Text = added.Count switch
        {
            0 => $"Added {audio} audio file{(audio == 1 ? "" : "s")} to the audio track (Ctrl+Z to undo)",
            1 => $"Added {p0.FindMedia(added[0].SourceId)?.DisplayName} as an overlay on V{added[0].Track + 1}. Drag it in the preview to place it; " +
                 "the Properties panel sets its size or makes it full frame.",
            _ => $"Added {added.Count} overlays at the playhead, stacked on V{added.Min(o => o.Track) + 1}–V{added.Max(o => o.Track) + 1}. " +
                 "Drag each in the preview to place it (Ctrl+Z to undo).",
        };
    }

    void SeparateAudio(Guid clipId)
    {
        if (TimelineOps.CannotSeparateAudio(_session.Project, clipId) is { } why) { _statusText.Text = why; return; }
        _engine.Pause();
        AudioItem? item = null;
        _session.Execute(p => item = TimelineOps.SeparateAudio(p, clipId));
        if (item is null) return;
        _timeline.SelectedClipId = null;
        _timeline.SelectedAudioId = item.Id;
        _preview.Select(null);
        UpdatePropertyGrid();
        _timeline.Invalidate();
        _statusText.Text = "Unlinked: the clip's sound is now on the audio track. Move, trim or keep it on its own; " +
                           "right-click the clip → Link audio puts it back (Ctrl+Z to undo).";
    }

    /// <summary>Put a clip's unlinked sound (in sync on the audio track) back on the clip.</summary>
    void LinkAudio(Guid clipId)
    {
        if (TimelineOps.CannotLinkAudio(_session.Project, clipId) is { } why) { _statusText.Text = why; return; }
        _engine.Pause();
        int n = 0;
        _session.Execute(p => n = TimelineOps.LinkAudio(p, clipId));
        if (n == 0) return;
        _timeline.SelectedAudioId = null;
        _timeline.SelectedClipId = clipId;
        _preview.Select(null);
        UpdatePropertyGrid();
        _timeline.Invalidate();
        _statusText.Text = "Linked: the clip plays its own sound again, and it moves and deletes with the picture (Ctrl+Z to undo).";
    }

    void ShowOverlayMenu(Guid overlayId, Point screen)
    {
        if (_session.Project.FindOverlay(overlayId) is not { } o) return;
        var media = _session.Project.FindMedia(o.SourceId);
        var menu = new ContextMenuStrip();
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Items.Add("Jump to its start", null, (_, _) => { _engine.Pause(); _engine.Seek(o.Start); });
        menu.Items.Add("Move to playhead", null, (_, _) => _session.Execute(p => TimelineOps.MoveOverlay(p, overlayId, _engine.Position)));
        menu.Items.Add(new ToolStripMenuItem("Split at playhead (S)", null, (_, _) => Split())
        {
            Enabled = _engine.Position - o.Start >= TimelineOps.MinClipLength && o.End - _engine.Position >= TimelineOps.MinClipLength,
        });
        var place = new ToolStripMenuItem("Place");
        foreach (var (pos, label) in new[]
        {
            (OverlayPosition.BottomRight, "Bottom right"), (OverlayPosition.BottomLeft, "Bottom left"),
            (OverlayPosition.TopRight, "Top right"), (OverlayPosition.TopLeft, "Top left"),
            (OverlayPosition.Centre, "Centre"), (OverlayPosition.FullFrame, "Full frame (cutaway)"),
        })
            place.DropDownItems.Add(label, null, (_, _) => { _session.Execute(p => TimelineOps.PlaceOverlay(p, overlayId, pos)); ShowOverlayUnderPlayhead(overlayId); });
        menu.Items.Add(place);
        menu.Items.Add(new ToolStripMenuItem("Move up a track", null, (_, _) => _session.Execute(p => TimelineOps.MoveOverlay(p, overlayId, o.Start, o.Track + 1)))
        {
            Enabled = o.Track < TimelineOps.MaxOverlayTracks,
        });
        menu.Items.Add(new ToolStripMenuItem("Move down a track", null, (_, _) => _session.Execute(p => TimelineOps.MoveOverlay(p, overlayId, o.Start, o.Track - 1)))
        {
            Enabled = o.Track > 1,
        });
        menu.Items.Add(new ToolStripSeparator());
        bool hasAudio = media is { HasAudio: true, IsStill: false };
        menu.Items.Add(new ToolStripMenuItem("Mute (Shift+M)", null, (_, _) => ToggleMute(null)) { Checked = o.Muted, Enabled = hasAudio });
        menu.Items.Add(new ToolStripMenuItem("Separate audio", null, (_, _) =>
        {
            AudioItem? item = null;
            _session.Execute(p => item = TimelineOps.SeparateOverlayAudio(p, overlayId));
            if (item is not null) _statusText.Text = "Separated the overlay's audio onto the audio track; the overlay is muted.";
        })
        {
            Enabled = hasAudio && TimelineOps.CannotSeparateOverlayAudio(_session.Project, overlayId) is null,
            ToolTipText = TimelineOps.CannotSeparateOverlayAudio(_session.Project, overlayId),
        });
        menu.Items.Add("Remove overlay (Del)", null, (_, _) => DeleteSelection());
        menu.Show(screen);
    }

    /// <summary>Overlay edits only show if the playhead is on it: if it isn't, jump to its start.</summary>
    void ShowOverlayUnderPlayhead(Guid overlayId)
    {
        if (_session.Project.FindOverlay(overlayId) is not { } o) return;
        if (o.Start <= _engine.Position && _engine.Position < o.End) return;
        _engine.Pause();
        _engine.Seek(o.Start);
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
        menu.Items.Add(new ToolStripMenuItem("Split at playhead (S)", null, (_, _) => Split())
        {
            Enabled = _engine.Position - a.Start >= TimelineOps.MinClipLength && a.End - _engine.Position >= TimelineOps.MinClipLength,
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Mute (Shift+M)", null, (_, _) => ToggleMute(null)) { Checked = a.Muted });
        if (TimelineOps.LinkedClipOf(_session.Project, audioId) is { } owner)
            menu.Items.Add(new ToolStripMenuItem("Link back to its clip", null, (_, _) => LinkAudio(owner.Id))
            {
                ToolTipText = "This is a clip's own sound, still in sync with it: put it back on the clip.",
            });
        menu.Items.Add("Remove from audio track (Del)", null, (_, _) => DeleteSelection());
        menu.Show(screen);
    }

    /// <summary>Mute/unmute the selected audio item, else <paramref name="clipId"/>.</summary>
    /// <summary>The selected clips that still exist, in timeline order.</summary>
    List<Guid> SelectedClips()
    {
        var ids = _timeline.SelectedClipIds;
        return [.. _session.Project.Timeline.Where(c => ids.Contains(c.Id)).Select(c => c.Id)];
    }

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
        if (_timeline.SelectedOverlayId is { } oid && _session.Project.FindOverlay(oid) is { } ov)
        {
            bool m = !ov.Muted;
            _session.Execute(p => TimelineOps.SetOverlayMuted(p, oid, m));
            var n = _session.Project.FindMedia(ov.SourceId)?.DisplayName ?? "overlay";
            _statusText.Text = m ? $"Muted {n}" : $"Unmuted {n}";
            return;
        }
        if (clipId is not { } id || _session.Project.FindClip(id) is not { } c) return;
        var sel = SelectedClips();
        if (sel.Count > 1 && sel.Contains(id))
        {
            // Several clips: mute them all, unless they're all muted already (then unmute them all).
            bool muteAll = sel.Any(x => _session.Project.FindClip(x) is { Muted: false });
            _session.Execute(p => { foreach (var x in sel) TimelineOps.SetClipMuted(p, x, muteAll); });
            _statusText.Text = $"{(muteAll ? "Muted" : "Unmuted")} {sel.Count} clips";
            return;
        }
        bool muted = !c.Muted;
        _session.Execute(p => TimelineOps.SetClipMuted(p, id, muted));
        var name = _session.Project.FindMedia(c.SourceId)?.DisplayName ?? "clip";
        _statusText.Text = muted ? $"Muted {name}" : $"Unmuted {name}";
    }

    /// <summary>
    /// S / Split: cut the SELECTED overlay or audio item at the playhead if one is selected, else the main clip there.
    /// The right half becomes the selection, so pressing S again keeps cutting the same track.
    /// </summary>
    void Split()
    {
        _engine.Pause();
        var at = _engine.Position;
        var p = _session.Project;
        if (_timeline.SelectedOverlayId is { } oid && p.FindOverlay(oid) is { } o)
        {
            OverlayClip? right = null;
            _session.Execute(proj => right = TimelineOps.SplitOverlayAt(proj, oid, at));
            if (right is null) { _statusText.Text = "Move the playhead onto the selected overlay (not right at its ends), then press S."; return; }
            _timeline.SelectOverlay(right.Id);
            _statusText.Text = $"Split the overlay at {TimelineControl.FormatTime(at, tenths: true)} (Ctrl+Z to undo)";
            return;
        }
        if (_timeline.SelectedAudioId is { } aid && p.FindAudio(aid) is not null)
        {
            AudioItem? right = null;
            _session.Execute(proj => right = TimelineOps.SplitAudioAt(proj, aid, at));
            if (right is null) { _statusText.Text = "Move the playhead onto the selected audio item (not right at its ends), then press S."; return; }
            _timeline.SelectedAudioId = right.Id;
            UpdatePropertyGrid();
            _timeline.Invalidate();
            _statusText.Text = $"Split the audio at {TimelineControl.FormatTime(at, tenths: true)} (Ctrl+Z to undo)";
            return;
        }
        // Main track. It used to fail silently when the playhead was too near a clip end (owner, 2026-11-28: "having
        // issues splitting at full zoom"); say why instead.
        if (TimelineOps.Locate(p, at) is { } hit && !hit.Clip.IsGap
            && (hit.SourceTime - hit.Clip.In < TimelineOps.MinClipLength || hit.Clip.Out - hit.SourceTime < TimelineOps.MinClipLength))
        {
            _statusText.Text = $"Can't split within {TimelineOps.MinClipLength.TotalMilliseconds:0} ms of a clip's start or end (pieces that short "
                             + "can't be exported). To shave a few frames off, drag the clip's edge instead.";
            return;
        }
        _session.Execute(proj => TimelineOps.SplitAt(proj, at));
    }

    /// <summary>Shift+S: cut every track (main clip, overlays, audio) at the playhead, one undo step.</summary>
    void SplitAll()
    {
        _engine.Pause();
        var at = _engine.Position;
        int cuts = 0;
        _session.Execute(proj => cuts = TimelineOps.SplitAllAt(proj, at));
        _statusText.Text = cuts == 0
            ? "Nothing to split here: move the playhead onto a clip, overlay or audio item (not right at its ends)."
            : $"Split {cuts} {(cuts == 1 ? "piece" : "pieces")} on every track at {TimelineControl.FormatTime(at, tenths: true)} (Ctrl+Z to undo)";
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
            _preview.ShowNotice(TimelineOps.Locate(p, at) is { Clip.IsGap: true } ? "That's a gap: there's no picture to freeze here."
                                                                                  : "Move the playhead onto a clip first, then press F.");
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
            // Saved next to the footage (not hidden in AppData), so it travels with the project.
            var png = await _library.ExtractFrameAsync(media, hit.SourceTime, FrameExport.DefaultFolder(p));
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
    /// <summary>Remove files from the project, with every clip, overlay and audio item using them (one confirm, one undo step).</summary>
    void RemoveMedia(IReadOnlyList<Guid> mediaIds)
    {
        var p = _session.Project;
        var media = mediaIds.Select(id => p.FindMedia(id)).OfType<SourceMedia>().ToList();
        if (media.Count == 0) return;
        int uses = media.Sum(m => TimelineOps.UsageCount(p, m.Id));
        string what = media.Count == 1 ? media[0].DisplayName : $"These {media.Count} files";
        if (uses > 0)
        {
            var r = MessageBox.Show(this,
                $"{what} {(media.Count == 1 ? "is" : "are")} used {uses} time{(uses == 1 ? "" : "s")} on the timeline.\n\n" +
                "Remove from the project, along with those clips? (The files on disk aren't deleted, and you can Undo.)",
                "Remove from project", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (r != DialogResult.OK) return;
        }
        _engine.Pause();
        _session.Execute(proj => { foreach (var m in media) TimelineOps.RemoveMedia(proj, m.Id); });
        _timeline.PruneSelection(_session.Project);
        if (_timeline.SelectedAudioId is { } aid && _session.Project.FindAudio(aid) is null) _timeline.SelectedAudioId = null;
        UpdatePropertyGrid();
        _engine.Refresh();
        _statusText.Text = media.Count == 1 ? $"Removed {media[0].DisplayName} from the project" : $"Removed {media.Count} files from the project";
    }

    // ---------------- Chapter markers → YouTube timestamps (D31) ----------------

    void AddMarker()
    {
        _engine.Pause();
        var at = _engine.Position;
        if (TimelineOps.Locate(_session.Project, at) is null) { _statusText.Text = "Move the playhead onto a clip first, then press M."; return; }
        using var dlg = new ChapterTitleForm(MarkerOps.NextDefaultTitle(_session.Project), "Add chapter marker");
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        Marker? added = null;
        _session.Execute(p => added = MarkerOps.Add(p, at, dlg.ChapterTitle));
        if (added is not null) { _timeline.SelectedMarkerId = added.Id; _timeline.Invalidate(); }
        _statusText.Text = $"Chapter \"{added?.Title}\" added. Ctrl+Shift+C copies all chapters for YouTube.";
    }

    void RenameMarker(Guid id)
    {
        if (_session.Project.FindMarker(id) is not { } m) return;
        using var dlg = new ChapterTitleForm(m.Title, "Rename chapter");
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.ChapterTitle.Length == 0) return;
        _session.Execute(p => MarkerOps.Rename(p, id, dlg.ChapterTitle));
    }

    void ShowChapters()
    {
        _engine.Pause();
        using var dlg = new ChaptersForm(_session.Project, _session.FilePath);
        dlg.ShowDialog(this);
    }

    /// <summary>One keystroke: chapters straight onto the clipboard, with a heads-up if YouTube would ignore them.</summary>
    void CopyChapters()
    {
        var chapters = YouTubeChapters.Build(_session.Project);
        if (chapters.Text.Length == 0) { _statusText.Text = chapters.Warnings[0]; return; }
        Clipboard.SetText(chapters.Text);
        _statusText.Text = chapters.YouTubeWillShowChapters
            ? $"Copied {chapters.Chapters.Count} chapters. Paste them into your YouTube description."
            : $"Copied, but YouTube won't show them yet: {chapters.Warnings[0]}";
    }

    /// <summary>
    /// Delete everything selected, as one undo step: annotations, audio items, overlays and main-track clips.
    /// Del (<paramref name="ripple"/> false) takes clips off and leaves a GAP, so nothing else moves; a selected gap is
    /// closed. Shift+Del (<paramref name="ripple"/> true) closes the space too: later clips, overlays and the audio
    /// track move up together and stay in sync (D38). Overlays, audio items and annotations sit at their own times,
    /// so for them both keys just remove them.
    /// </summary>
    void DeleteSelection(bool ripple = false)
    {
        if (_bin.ListFocused && _bin.SelectedMediaIds.Count > 0)
        {
            _bin.RequestRemoveSelected();
            return;
        }
        if (_timeline.SelectedMarkerId is { } mid)
        {
            _session.Execute(p => MarkerOps.Delete(p, mid));
            _timeline.SelectedMarkerId = null;
            return;
        }
        var p0 = _session.Project;
        var annotations = _timeline.SelectedAnnotationIds.ToHashSet();
        if (_preview.SelectedAnnotationId is { } pa) annotations.Add(pa);
        var audio = _timeline.SelectedAudioIds.ToList();
        var overlays = _timeline.SelectedOverlayIds.ToList();
        var clips = SelectedClips();
        var gaps = clips.Where(id => p0.FindClip(id) is { IsGap: true }).ToList();
        var footage = clips.Except(gaps).ToList();
        if (annotations.Count + audio.Count + overlays.Count + clips.Count == 0)
        {
            _statusText.Text = "Click a clip, overlay, audio item, annotation or chapter marker (or drag a box around several) first, then press Delete.";
            return;
        }

        _engine.Pause();
        _session.Execute(p =>
        {
            foreach (var id in annotations) TimelineOps.DeleteAnnotation(p, id); // the whole run if it spans cuts
            foreach (var id in audio) TimelineOps.DeleteAudio(p, id);             // the file stays in the bin
            foreach (var id in overlays) TimelineOps.DeleteOverlay(p, id);
            // Selected gaps close first, so lifting a clip next to one leaves its own gap rather than closing both.
            foreach (var id in gaps) TimelineOps.Delete(p, id);
            foreach (var id in footage)
                if (ripple) TimelineOps.Delete(p, id); else TimelineOps.Lift(p, id);
        });

        _preview.Select(null);
        _timeline.SelectedClipId = null;
        _timeline.SelectedAudioId = null;
        _timeline.SelectedAnnotationId = null;
        _timeline.SelectOverlay(null);
        UpdatePropertyGrid();
        _timeline.Invalidate();

        var parts = new List<string>();
        void Count(int n, string one, string many) { if (n > 0) parts.Add(n == 1 ? one : $"{n} {many}"); }
        Count(footage.Count, "1 clip", "clips");
        Count(gaps.Count, "1 gap", "gaps");
        Count(overlays.Count, "1 overlay", "overlays");
        Count(audio.Count, "1 audio item", "audio items");
        Count(annotations.Count, "1 annotation", "annotations");
        var how = ripple || (footage.Count == 0 && gaps.Count > 0) ? ": the space was closed up and the tracks stayed in sync"
                : footage.Count == 0 ? ""
                : gaps.Count > 0 ? ": the clips left gaps (Shift+Del closes them up) and the selected gaps closed"
                : ": a gap was left in their place, so nothing else moved (Shift+Del closes gaps up)";
        _statusText.Text = $"Deleted {string.Join(", ", parts)}{how}. Ctrl+Z to undo.";
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
        // A project that already has an edit: import into the media bin only, and let the owner place the files
        // (double-click, I, O). Appending to the end of an edited timeline was rarely what was wanted (owner,
        // 2026-11-28). An empty project still fills the timeline in one go, so starting a video stays one step.
        bool binOnly = _session.Project.Timeline.Count > 0;
        int toBin = 0;
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
            if (binOnly)
            {
                _session.Execute(p => p.Media.Add(media));
                toBin++;
            }
            else if (media.IsAudioOnly)
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
        if (toBin > 0)
            _statusText.Text = $"Added {toBin} file{(toBin == 1 ? "" : "s")} to Media. Double-click to add to the end, or select and press "
                             + "I to insert at the playhead (O for an overlay).";
    }

    bool _exportingFrame;

    /// <summary>
    /// Save the frame under the playhead as a PNG at the project's output size: exact frame from the original, the clip's
    /// zoom and brightness, the annotations on screen (D33). Goes next to the main footage, never overwriting, and is
    /// added to the media bin (not the timeline) so it can be dragged in as a still.
    /// </summary>
    /// <param name="thumbnail">YouTube thumbnail: 1280×720 JPG under YouTube's 2 MB limit, instead of a full-size PNG.</param>
    async Task ExportFrameAsync(bool chooseLocation, bool thumbnail = false)
    {
        if (_exportingFrame) return;
        _engine.Pause();
        _preview.CommitTextEdit();
        var project = _session.Project;
        if (project.Timeline.Count == 0) { _statusText.Text = "Add a clip to the timeline first, then save a frame."; return; }

        var t = _engine.Position;
        var name = thumbnail ? YouTubeThumbnail.DefaultFileName(project, _session.FilePath, t) : FrameExport.DefaultFileName(project, _session.FilePath, t);
        var path = FrameExport.Unique(Path.Combine(FrameExport.DefaultFolder(project), name));
        if (chooseLocation)
        {
            using var dlg = new SaveFileDialog
            {
                Filter = thumbnail ? "JPEG image|*.jpg" : "PNG image|*.png", InitialDirectory = Path.GetDirectoryName(path), FileName = Path.GetFileName(path),
                Title = thumbnail ? "Save YouTube thumbnail as" : "Save frame as",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            path = dlg.FileName;
        }

        _exportingFrame = true;
        _statusText.Text = thumbnail ? "Saving thumbnail…" : "Saving frame…";
        try
        {
            var snapshot = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project)); // the project may change meanwhile
            if (thumbnail)
            {
                // Render at thumbnail size with the same renderer as Save frame, then a JPEG that fits YouTube's limit.
                var png = Path.Combine(Path.GetTempPath(), $"flyweight-thumb-{Guid.NewGuid():N}.png");
                try
                {
                    await FrameRenderer.ExportAsync(snapshot, t, YouTubeThumbnail.Width, YouTubeThumbnail.Height, png);
                    await YouTubeThumbnail.SaveJpegUnderLimitAsync(png, path);
                }
                finally { try { File.Delete(png); } catch { } }
            }
            else
            {
                var (w, h) = new ExportSettings { Resolution = project.OutputResolution }.SizeFor(project.Shape);
                await FrameRenderer.ExportAsync(snapshot, t, w, h, path);
            }

            // Into the bin (not the timeline), selected, so it's easy to find and drag in as a still.
            var media = await _library.ProbeAsync(path);
            _session.Execute(p => p.Media.Add(media));
            StartProxy(media);
            _bin.Highlight(media.Id);
            _statusText.Text = $"Saved {Path.GetFileName(path)} in {Path.GetDirectoryName(path)} and added it to Media.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't save the frame:\n\n{ex.Message}", "Save frame", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _statusText.Text = "Saving the frame failed.";
        }
        finally { _exportingFrame = false; }
    }

    /// <summary>
    /// Switch the whole project between landscape and vertical (D41; one undo step). Positions are kept as fractions of
    /// the frame, so arrows, boxes, text and overlays may land somewhere unexpected: warn first if there are any.
    /// </summary>
    void ChangeShape(CanvasShape shape)
    {
        var p = _session.Project;
        if (p.Shape == shape) return;
        if (p.Annotations.Count > 0 || p.Overlays.Count > 0)
        {
            var name = shape == CanvasShape.Vertical ? "vertical (9:16)" : "landscape (16:9)";
            var answer = MessageBox.Show(this,
                $"Change this project to {name}?\n\nArrows, boxes, text and overlays keep their place as a share of the frame, " +
                "so some may end up stretched or in an odd spot and need moving. You can undo this (Ctrl+Z).",
                "Frame shape", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
            if (answer != DialogResult.OK) return;
        }
        _session.Execute(proj => TimelineOps.SetCanvasShape(proj, shape));
        _preview.Invalidate();
        _statusText.Text = shape == CanvasShape.Vertical ? "Frame shape: vertical 9:16 (YouTube Shorts)." : "Frame shape: landscape 16:9.";
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
        // Default: next to the largest video (the main footage), named after the project.
        var suggested = FrameExport.DefaultVideoPath(_session.Project, _session.FilePath);
        using var dlg = new ExportForm(_session.Project, suggested, details =>
        {
            // Remember the YouTube details in the project (one undo step), only if they changed.
            var old = _session.Project.YouTube;
            if (old.Title == details.Title && old.Description == details.Description && old.Tags == details.Tags && old.SaveTextFile == details.SaveTextFile) return;
            _session.Execute(p => TimelineOps.SetYouTubeDetails(p, details));
        });
        dlg.ShowDialog(this);
    }

    void NewProject()
    {
        if (!ConfirmDiscard("starting a new project")) return;
        using var dlg = new NewProjectForm(NewProjectForm.DefaultFolder(_session.FilePath));
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _engine.Pause();
        _session.New(dlg.OutputResolution, dlg.Background, dlg.Shape);
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

    /// <summary>"Save" with a ▾ for "Save as…" (a copy under a new name or folder; Ctrl+Shift+S).</summary>
    ToolStripSplitButton SaveButton()
    {
        var b = new ToolStripSplitButton("Save") { ToolTipText = "Save project (Ctrl+S) · ▾ Save as… (Ctrl+Shift+S)", ForeColor = Theme.Text, DisplayStyle = ToolStripItemDisplayStyle.Text };
        b.ButtonClick += (_, _) => { SaveProject(false); b.Owner?.Invalidate(); };
        b.DropDownItems.Add(new ToolStripMenuItem("Save as…", null, (_, _) => SaveProject(true)) { ShortcutKeyDisplayString = "Ctrl+Shift+S" });
        b.DropDownClosed += (_, _) => b.Owner?.Invalidate();
        return b;
    }

    /// <summary>"Open…" with a ▾ listing the last few projects (newest first).</summary>
    ToolStripSplitButton OpenButton()
    {
        var b = new ToolStripSplitButton("Open…") { ToolTipText = "Open project (Ctrl+O) · ▾ for recent projects", ForeColor = Theme.Text, DisplayStyle = ToolStripItemDisplayStyle.Text };
        b.ButtonClick += (_, _) => { OpenProject(); b.Owner?.Invalidate(); };
        b.DropDownClosed += (_, _) => b.Owner?.Invalidate();
        b.DropDownItems.Add("(none yet)"); // so the ▾ opens; rebuilt each time below
        b.DropDownOpening += (_, _) =>
        {
            b.DropDownItems.Clear();
            var recent = RecentProjects.Load(RecentPath);
            if (recent.Count == 0) { b.DropDownItems.Add(new ToolStripMenuItem("No recent projects") { Enabled = false }); return; }
            for (int i = 0; i < recent.Count; i++)
            {
                var path = recent[i];
                bool here = File.Exists(path);
                var item = new ToolStripMenuItem($"&{i + 1}  {Path.GetFileNameWithoutExtension(path)}{(here ? "" : "  (missing)")}")
                {
                    ToolTipText = path, Enabled = here,
                    ShortcutKeyDisplayString = Path.GetFileName(Path.GetDirectoryName(path)),
                };
                item.Click += (_, _) => OpenRecent(path);
                b.DropDownItems.Add(item);
            }
            b.DropDownItems.Add(new ToolStripSeparator());
            b.DropDownItems.Add("Clear recent list", null, (_, _) => { RecentProjects.Save(RecentPath, []); _lastRecent = null; });
        };
        return b;
    }

    void OpenRecent(string path)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show(this, $"That project isn't there any more:\n\n{path}", "Open", MessageBoxButtons.OK, MessageBoxIcon.Information);
            RecentProjects.Save(RecentPath, RecentProjects.Remove(RecentProjects.Load(RecentPath), path));
            return;
        }
        if (!ConfirmDiscard("opening another project")) return;
        LoadProject(path);
    }

    void OpenProject()
    {
        if (!ConfirmDiscard("opening another project")) return;
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

    bool SaveProject(bool saveAs, string? title = null)
    {
        _preview.CommitTextEdit();
        var path = _session.FilePath;
        if (saveAs || path is null)
        {
            // Start where the project already lives, under its current name (Save as = save a copy elsewhere / renamed).
            var current = _session.FilePath;
            using var dlg = new SaveFileDialog
            {
                Filter = $"Flyweight project|*{ProjectSerializer.FileExtension}",
                FileName = (current is null ? "My video" : Path.GetFileNameWithoutExtension(current)) + ProjectSerializer.FileExtension,
                InitialDirectory = current is not null ? Path.GetDirectoryName(current) : NewProjectForm.DefaultFolder(null),
                Title = title ?? (saveAs ? "Save project as" : "Save project"),
            };
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

    /// <summary>
    /// Ask to save unsaved changes first. <paramref name="before"/> says what happens next (e.g. "starting a new
    /// project"): owner was confused when New showed a Save dialog for the OLD project and then the New project window,
    /// and took the first for the second. So the prompt names the current project and says what comes after.
    /// </summary>
    bool ConfirmDiscard(string? before = null)
    {
        if (!_session.IsDirty) return true;
        var name = _session.FilePath is { } fp ? $"\"{Path.GetFileNameWithoutExtension(fp)}\"" : "the current (untitled) project";
        var msg = before is null ? $"Save changes to {name}?"
            : $"Save changes to {name} before {before}?" +
              (_session.FilePath is null ? $"\n\nYes asks where to save it first; then you'll carry on {before}." : "");
        var r = MessageBox.Show(this, msg, "Flyweight", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        return r == DialogResult.No || (r == DialogResult.Yes && SaveProject(false, before is null ? null : $"Save the current project (before {before})"));
    }

    // ---------------- Autosave & crash recovery ----------------

    void Autosave()
    {
        if (!_ownsSession || !_session.IsDirty) return; // only the first Flyweight window autosaves
        try
        {
            _session.Autosave(AutosavePath);
            _statusText.Text = $"Autosaved {DateTime.Now:HH:mm:ss}";
        }
        catch { /* never let autosave interrupt editing */ }
    }

    /// <summary>
    /// True for the first Flyweight window: it owns the session lock and the autosave. A second window (e.g. opened
    /// while the first is running) leaves both alone, so it neither offers to "recover" the first window's work nor
    /// deletes its autosave when it closes.
    /// </summary>
    bool _ownsSession;

    /// <summary>Take the session lock. Returns true if the last session crashed (its lock was left by a process that's gone).</summary>
    bool ClaimSession()
    {
        Directory.CreateDirectory(AppDataDir);
        if (File.Exists(LockPath) && int.TryParse(ReadLock(), out var pid) && pid != Environment.ProcessId && IsFlyweight(pid))
        {
            _ownsSession = false; // that window is alive: it owns the session
            return false;
        }
        bool crashed = File.Exists(LockPath);
        File.WriteAllText(LockPath, Environment.ProcessId.ToString());
        _ownsSession = true;
        return crashed;

        static string ReadLock() { try { return File.ReadAllText(LockPath).Trim(); } catch (IOException) { return ""; } }
        static bool IsFlyweight(int id)
        {
            try
            {
                using var proc = System.Diagnostics.Process.GetProcessById(id);
                return !proc.HasExited && proc.ProcessName.Equals("Flyweight", StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; } // no such process
            catch (InvalidOperationException) { return false; }
        }
    }

    /// <summary>
    /// After a crash, offer the autosave. <paramref name="openingInstead"/> is a project the user asked to open
    /// (double-clicked); the prompt says that No opens it. Returns true if the autosave was loaded.
    /// </summary>
    bool OfferCrashRecovery(string? openingInstead)
    {
        if (!File.Exists(AutosavePath)) return false;

        var msg = $"Flyweight didn't close properly last time.\n\nRecover the autosaved project from {File.GetLastWriteTime(AutosavePath):g}?";
        if (openingInstead is not null)
            msg += $"\n\nNo opens \"{Path.GetFileName(openingInstead)}\" instead, and the autosaved work is discarded.";
        var r = MessageBox.Show(this, msg, "Recover work", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) return false;
        LoadProject(AutosavePath, asRecovery: true);
        return true;
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
        string.Join('|', p.Timeline.Select(c => $"{c.Id}:{c.SourceId}:{c.In.Ticks}:{c.Out.Ticks}:{c.Speed}"));

    void UpdateUi()
    {
        var name = _session.FilePath is { } p ? Path.GetFileNameWithoutExtension(p) : "Untitled";
        Text = $"{name}{(_session.IsDirty ? " •" : "")} · Flyweight{BuildTag}";
        _undoBtn.Enabled = _session.CanUndo;
        _redoBtn.Enabled = _session.CanRedo;

        // Drop selections that no longer exist (e.g. after undo).
        if (_preview.SelectedAnnotationId is { } aid && _session.Project.FindAnnotation(aid) is null) _preview.Select(null);
        _timeline.PruneSelection(_session.Project);
        if (_timeline.SelectedOverlayId is { } oid && _session.Project.FindOverlay(oid) is null) _timeline.SelectedOverlayId = null;

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
        var overlay = a is null && audio is null && _timeline.SelectedOverlayId is { } oid ? p.FindOverlay(oid) : null;
        var clip = a is null && audio is null && overlay is null && _timeline.SelectedClipId is { } cid ? p.FindClip(cid) : null;

        // The preview outlines the selected overlay.
        if (_preview.SelectedOverlayId != overlay?.Id) { _preview.SelectedOverlayId = overlay?.Id; _preview.Invalidate(); }

        if (a is not null) _props.ShowAnnotation(a); // updates values in place; leaves a focused text box / dragged slider alone
        if (audio is not null) _clipProps.ShowAudio(audio, p.FindMedia(audio.SourceId));
        else if (clip is not null) _clipProps.ShowClip(clip, p.FindMedia(clip.SourceId), SelectedClips().Count);
        if (overlay is not null) _overlayProps.ShowOverlay(overlay, p.FindMedia(overlay.SourceId));
        _props.Visible = a is not null;
        _clipProps.Visible = audio is not null || clip is not null;
        _overlayProps.Visible = overlay is not null;
        _propsEmpty.Visible = a is null && audio is null && clip is null && overlay is null;
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
