using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.App.Controls;

/// <summary>List of imported files with thumbnails and optimising progress. Double-click adds to the timeline.</summary>
public sealed class MediaBinControl : UserControl
{
    readonly ListView _list;
    readonly ImageList _images = new() { ImageSize = new Size(128, 72), ColorDepth = ColorDepth.Depth32Bit };
    readonly HashSet<Guid> _thumbLoaded = new();

    public event EventHandler<Guid>? AddToTimelineRequested;

    /// <summary>Right-click "Insert before/after current clip": media id, and true for after.</summary>
    public event Action<Guid, bool>? InsertRequested;

    /// <summary>"Insert at playhead": split the clip under the playhead and put this file in between.</summary>
    public event Action<Guid>? InsertAtPlayheadRequested;

    /// <summary>"Add as overlay at playhead": picture-in-picture on the overlay track.</summary>
    public event Action<Guid>? AddOverlayRequested;

    /// <summary>Set by MainForm: whether a media id is a pure audio file (for the right-click menu).</summary>
    public Func<Guid, bool> IsAudio { get; set; } = _ => false;

    /// <summary>"Remove from project" (right-click, or Delete with the bin focused): media id.</summary>
    public event Action<Guid>? RemoveRequested;

    /// <summary>The selected file, if any.</summary>
    public Guid? SelectedMediaId => _list.SelectedItems.Count == 1 && _list.SelectedItems[0].Tag is Guid id ? id : null;

    /// <summary>True when the list has keyboard focus, so Delete means "remove this file", not "delete the clip".</summary>
    public bool ListFocused => _list.Focused;

    /// <summary>Select and scroll to an item, e.g. a frame that was just saved.</summary>
    public void Highlight(Guid id)
    {
        if (_list.Items[id.ToString()] is not { } item) return;
        _list.SelectedItems.Clear();
        item.Selected = item.Focused = true;
        item.EnsureVisible();
    }

    public void RequestRemoveSelected()
    {
        if (SelectedMediaId is { } id) RemoveRequested?.Invoke(id);
    }

    public MediaBinControl()
    {
        var header = new Label
        {
            Text = "Media", Dock = DockStyle.Top, Height = 26, Padding = new Padding(8, 6, 0, 0),
            ForeColor = Color.Silver, BackColor = Color.FromArgb(38, 38, 44),
        };
        _list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Tile, LargeImageList = _images, TileSize = new Size(260, 80), ShowItemToolTips = true,
            BackColor = Color.FromArgb(28, 28, 32), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.None,
            MultiSelect = false,
        };
        _list.Columns.Add("Name");
        _list.Columns.Add("Status");
        _list.ItemActivate += (_, _) =>
        {
            if (_list.SelectedItems.Count == 1 && _list.SelectedItems[0].Tag is Guid id) AddToTimelineRequested?.Invoke(this, id);
        };
        var menu = new ContextMenuStrip();
        Guid? MenuMedia() => _list.SelectedItems.Count == 1 && _list.SelectedItems[0].Tag is Guid id ? id : null;
        var atPlayhead = menu.Items.Add("Insert at playhead (I)", null, (_, _) => { if (MenuMedia() is { } id) InsertAtPlayheadRequested?.Invoke(id); });
        atPlayhead.ToolTipText = "Splits the clip under the playhead and puts this in between";
        var before = menu.Items.Add("Insert before current clip", null, (_, _) => { if (MenuMedia() is { } id) InsertRequested?.Invoke(id, false); });
        var after = menu.Items.Add("Insert after current clip", null, (_, _) => { if (MenuMedia() is { } id) InsertRequested?.Invoke(id, true); });
        var overlay = menu.Items.Add("Add as overlay at playhead (O)", null, (_, _) => { if (MenuMedia() is { } id) AddOverlayRequested?.Invoke(id); });
        overlay.ToolTipText = "Picture-in-picture over the main video, on the overlay track (V2). Drag it in the preview to place it.";
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Add to end of timeline", null, (_, _) => { if (MenuMedia() is { } id) AddToTimelineRequested?.Invoke(this, id); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Remove from project (Del)", null, (_, _) => { if (MenuMedia() is { } id) RemoveRequested?.Invoke(id); });
        menu.Opening += (_, e) =>
        {
            e.Cancel = MenuMedia() is null;
            // Audio files only go on the audio track (at the playhead); the video-only placements don't apply.
            bool video = MenuMedia() is { } id && !IsAudio(id);
            before.Visible = after.Visible = overlay.Visible = video;
            atPlayhead.Text = video ? "Insert at playhead (I)" : "Add to audio track at playhead";
        };
        _list.ContextMenuStrip = menu;

        var hint = new Label
        {
            Text = "Double-click to add · right-click for more", Dock = DockStyle.Bottom, Height = 22,
            ForeColor = Color.Gray, BackColor = Color.FromArgb(28, 28, 32), Padding = new Padding(8, 3, 0, 0),
        };
        Controls.Add(_list);
        Controls.Add(hint);
        Controls.Add(header);

        using var placeholder = new Bitmap(128, 72);
        using (var g = Graphics.FromImage(placeholder)) g.Clear(Color.FromArgb(50, 50, 58));
        _images.Images.Add("placeholder", (Image)placeholder.Clone());
    }

    /// <summary>Bring the list in line with the project and current proxy states.</summary>
    public void Sync(Project project, MediaLibrary library)
    {
        _list.BeginUpdate();
        var ids = project.Media.Select(m => m.Id).ToHashSet();
        foreach (var item in _list.Items.Cast<ListViewItem>().Where(i => !ids.Contains((Guid)i.Tag!)).ToList())
            _list.Items.Remove(item);

        foreach (var m in project.Media)
        {
            var key = m.Id.ToString();
            var item = _list.Items[key] ?? _list.Items.Add(new ListViewItem(new[] { m.DisplayName, "" }) { Name = key, Tag = m.Id, ImageKey = "placeholder" });
            var st = library.GetState(m.Id);
            item.SubItems[1].Text = st.Status switch
            {
                ProxyStatus.Ready when m.IsStill => $"Image  ·  {m.Width}×{m.Height}",
                ProxyStatus.Ready when m.IsAudioOnly => $"Audio  ·  {TimelineControl.FormatTime(m.Duration)}{(m.AudioChannels == 1 ? "  ·  mono" : "")}",
                ProxyStatus.Optimising when m.IsAudioOnly => "Reading audio…",
                ProxyStatus.Ready => $"{TimelineControl.FormatTime(m.Duration)}  ·  {m.Width}×{m.Height}  ·  {m.Fps:0.##} fps",
                ProxyStatus.Optimising => $"Optimising {st.Progress:P0}",
                ProxyStatus.Failed => $"Failed: {(st.Error ?? "").Split('\n', 2)[0].Trim()}",
                _ => "Queued",
            };
            item.ToolTipText = st.Status == ProxyStatus.Failed ? $"{m.OriginalPath}\n\n{st.Error}" : m.OriginalPath;
            if (st.ThumbnailPath is { } tp && !_thumbLoaded.Contains(m.Id) && File.Exists(tp))
            {
                using var fs = File.OpenRead(tp);
                using var img = Image.FromStream(fs);
                _images.Images.Add(key, new Bitmap(img, _images.ImageSize));
                item.ImageKey = key;
                _thumbLoaded.Add(m.Id);
            }
        }
        _list.EndUpdate();
    }
}
