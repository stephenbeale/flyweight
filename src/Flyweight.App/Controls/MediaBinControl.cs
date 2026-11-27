using System.Drawing.Drawing2D;
using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.App.Controls;

/// <summary>
/// List of imported files as rounded cards: thumbnail, name, details, a type badge and a thin progress bar while
/// optimising. Double-click adds to the timeline. An owner-drawn ListView, so selection, keyboard, tooltips and the
/// right-click menu keep their standard behaviour.
/// </summary>
public sealed class MediaBinControl : UserControl
{
    readonly BinList _list;
    readonly Theme.PanelHeader _header = new("Media");
    readonly Label _empty;
    readonly Dictionary<Guid, Bitmap> _thumbs = [];
    readonly Dictionary<Guid, CardInfo> _info = [];
    int _hot = -1;

    /// <summary>What a card shows besides the name (kept here: the ListViewItem only carries the media id).</summary>
    sealed record CardInfo(string Kind, string Detail, double? Progress, bool Failed);

    sealed class BinList : ListView
    {
        public BinList() { DoubleBuffered = true; Theme.DarkScrollbars(this); }
    }

    // Every action carries the selected files in bin order (Ctrl/Shift+click picks several), so MainForm can do them
    // all as ONE undo step.

    /// <summary>Double-click / "Add to end of timeline".</summary>
    public event Action<IReadOnlyList<Guid>>? AddToTimelineRequested;

    /// <summary>Right-click "Insert before/after current clip": media ids, and true for after.</summary>
    public event Action<IReadOnlyList<Guid>, bool>? InsertRequested;

    /// <summary>"Insert at playhead": split the clip under the playhead and put these files in between, in order.</summary>
    public event Action<IReadOnlyList<Guid>>? InsertAtPlayheadRequested;

    /// <summary>"Add as overlay at playhead": picture-in-picture on the overlay tracks.</summary>
    public event Action<IReadOnlyList<Guid>>? AddOverlayRequested;

    /// <summary>Set by MainForm: whether a media id is a pure audio file (for the right-click menu).</summary>
    public Func<Guid, bool> IsAudio { get; set; } = _ => false;

    /// <summary>"Remove from project" (right-click, or Delete with the bin focused).</summary>
    public event Action<IReadOnlyList<Guid>>? RemoveRequested;

    /// <summary>The selected files, in bin order (empty if none).</summary>
    public IReadOnlyList<Guid> SelectedMediaIds =>
        [.. _list.SelectedItems.Cast<ListViewItem>().OrderBy(i => i.Index).Select(i => (Guid)i.Tag!)];

    /// <summary>The selected file when exactly one is selected.</summary>
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
        if (SelectedMediaIds is { Count: > 0 } ids) RemoveRequested?.Invoke(ids);
    }

    public MediaBinControl()
    {
        BackColor = Theme.Panel;
        _list = new BinList
        {
            Dock = DockStyle.Fill, View = View.Tile, TileSize = new Size(260, 84), ShowItemToolTips = true, OwnerDraw = true,
            BackColor = Theme.Panel, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, MultiSelect = true,
        };
        _list.Columns.Add("Name");
        _list.Columns.Add("Status");
        _list.DrawItem += DrawCard;
        _list.Resize += (_, _) => FitTiles();
        _list.MouseMove += (_, e) => SetHot(_list.GetItemAt(e.X, e.Y)?.Index ?? -1);
        _list.MouseLeave += (_, _) => SetHot(-1);
        _list.ItemActivate += (_, _) =>
        {
            if (SelectedMediaIds is { Count: > 0 } ids) AddToTimelineRequested?.Invoke(ids);
        };
        var menu = new ContextMenuStrip { Renderer = new Theme.ToolRenderer() };
        IReadOnlyList<Guid> Sel() => SelectedMediaIds;
        var atPlayhead = menu.Items.Add("Insert at playhead (I)", null, (_, _) => InsertAtPlayheadRequested?.Invoke(Sel()));
        atPlayhead.ToolTipText = "Splits the clip under the playhead and puts the file(s) in between, in bin order";
        var before = menu.Items.Add("Insert before current clip", null, (_, _) => InsertRequested?.Invoke(Sel(), false));
        var after = menu.Items.Add("Insert after current clip", null, (_, _) => InsertRequested?.Invoke(Sel(), true));
        var overlay = menu.Items.Add("Add as overlay at playhead (O)", null, (_, _) => AddOverlayRequested?.Invoke(Sel()));
        overlay.ToolTipText = "Picture-in-picture over the main video, on the overlay track (V2). Drag it in the preview to place it.";
        menu.Items.Add(new ToolStripSeparator());
        var toEnd = menu.Items.Add("Add to end of timeline", null, (_, _) => AddToTimelineRequested?.Invoke(Sel()));
        menu.Items.Add(new ToolStripSeparator());
        var remove = menu.Items.Add("Remove from project (Del)", null, (_, _) => RemoveRequested?.Invoke(Sel()));
        menu.Opening += (_, e) =>
        {
            var ids = Sel();
            e.Cancel = ids.Count == 0;
            if (e.Cancel) return;
            // Audio files only go on the audio track (at the playhead); the video-only placements don't apply.
            bool anyVideo = ids.Any(id => !IsAudio(id)), allAudio = !anyVideo;
            before.Visible = after.Visible = overlay.Visible = anyVideo;
            string n = ids.Count == 1 ? "" : $" {ids.Count} files";
            atPlayhead.Text = allAudio ? $"Add{n} to audio track at playhead" : $"Insert{n} at playhead (I)";
            before.Text = $"Insert{n} before current clip";
            after.Text = $"Insert{n} after current clip";
            overlay.Text = $"Add{n} as overlay{(ids.Count == 1 ? "" : "s")} at playhead (O)";
            toEnd.Text = $"Add{n} to end of timeline";
            remove.Text = $"Remove{n} from project (Del)";
        };
        _list.ContextMenuStrip = menu;

        _empty = new Label
        {
            Text = "Drop videos, images or audio here\n\nor use Import on the toolbar", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.Muted, BackColor = Theme.Panel,
        };
        var hint = new Label
        {
            Text = "Double-click to add · Ctrl/Shift+click for several · right-click for more", Dock = DockStyle.Bottom, Height = LogicalToDeviceUnits(26),
            ForeColor = Color.FromArgb(120, 120, 130), BackColor = Theme.Panel, Padding = new Padding(10, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Font = new Font(Font.FontFamily, 8f),
        };
        Controls.Add(_list);
        Controls.Add(_empty);
        Controls.Add(hint);
        Controls.Add(_header);
        UpdateEmpty();
    }

    /// <summary>Files dropped anywhere on the bin (list or empty message) go to the same handler as the window.</summary>
    public void AcceptDrops(DragEventHandler enter, DragEventHandler drop)
    {
        foreach (Control c in new Control[] { this, _list, _empty })
        {
            c.AllowDrop = true;
            c.DragEnter += enter;
            c.DragDrop += drop;
        }
    }

    void UpdateEmpty()
    {
        // Don't read Visible back: it reports false until the window is on screen, which hid the message at start-up.
        bool empty = _list.Items.Count == 0;
        _empty.Visible = empty;
        _list.Visible = !empty;
        _header.Detail = _list.Items.Count == 0 ? "" : _list.Items.Count.ToString();
    }

    void SetHot(int index)
    {
        if (index == _hot) return;
        int old = _hot;
        _hot = index;
        if (old >= 0 && old < _list.Items.Count) _list.Invalidate(_list.Items[old].Bounds);
        if (index >= 0 && index < _list.Items.Count) _list.Invalidate(_list.Items[index].Bounds);
    }

    /// <summary>Cards fill the bin's width.</summary>
    void FitTiles()
    {
        int w = Math.Max(160, _list.ClientSize.Width - LogicalToDeviceUnits(6));
        var size = new Size(w, LogicalToDeviceUnits(66));
        if (_list.TileSize != size) _list.TileSize = size;
    }

    int Px(int v) => LogicalToDeviceUnits(v);

    void DrawCard(object? sender, DrawListViewItemEventArgs e)
    {
        var g = e.Graphics;
        var b = e.Bounds;
        using (var bg = new SolidBrush(Theme.Panel)) g.FillRectangle(bg, b);
        var card = new RectangleF(b.X + Px(6), b.Y + Px(3), b.Width - Px(12), b.Height - Px(6));
        bool selected = e.Item.Selected, hot = e.ItemIndex == _hot;
        Theme.FillRounded(g, selected ? Color.FromArgb(30, 52, 70) : hot ? Color.FromArgb(42, 42, 50) : Color.FromArgb(36, 36, 42), card, Px(8));
        Theme.DrawRounded(g, selected ? Theme.Accent : Color.FromArgb(52, 52, 60), selected ? 1.5f : 1, card, Px(8));

        var id = e.Item.Tag is Guid gid ? gid : Guid.Empty;
        _info.TryGetValue(id, out var info);

        // Thumbnail, 16:9, rounded.
        int th = (int)card.Height - Px(10), tw = th * 16 / 9;
        var thumb = new RectangleF(card.X + Px(5), card.Y + Px(5), tw, th);
        using (var path = Theme.Rounded(thumb, Px(5)))
        {
            if (_thumbs.TryGetValue(id, out var bmp))
            {
                var state = g.Save();
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.SetClip(path);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(bmp, thumb);
                g.Restore(state);
            }
            else
            {
                using var ph = new SolidBrush(Color.FromArgb(50, 50, 58));
                var old = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPath(ph, path);
                g.SmoothingMode = old;
                string glyph = info?.Kind == "AUDIO" ? "♪" : info?.Kind == "IMAGE" ? "▣" : "▶";
                TextRenderer.DrawText(g, glyph, Font, Rectangle.Round(thumb), Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        // Text: name, details, then a type badge (or the progress bar while optimising).
        float tx = thumb.Right + Px(10), right = card.Right - Px(10);
        using var bold = new Font(Font, FontStyle.Bold);
        var nameRect = new Rectangle((int)tx, (int)card.Y + Px(5), (int)(right - tx), Px(17));
        TextRenderer.DrawText(g, e.Item.Text, bold, nameRect, Color.FromArgb(232, 232, 238),
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        var detail = info?.Detail ?? (e.Item.SubItems.Count > 1 ? e.Item.SubItems[1].Text : "");
        var detailRect = new Rectangle((int)tx, nameRect.Bottom, (int)(right - tx), Px(16));
        TextRenderer.DrawText(g, detail, Font, detailRect, info?.Failed == true ? Color.FromArgb(255, 120, 110) : Color.FromArgb(150, 150, 160),
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

        if (info?.Progress is { } progress)
        {
            var bar = new RectangleF(tx, card.Bottom - Px(11), right - tx, Px(4));
            Theme.FillRounded(g, Color.FromArgb(58, 58, 68), bar, Px(2));
            if (progress > 0)
                Theme.FillRounded(g, Theme.Accent, new RectangleF(bar.X, bar.Y, (float)(bar.Width * Math.Clamp(progress, 0, 1)), bar.Height), Px(2));
        }
        else if (info is { Kind.Length: > 0 })
        {
            using var small = new Font(Font.FontFamily, 7f, FontStyle.Bold);
            var size = TextRenderer.MeasureText(info.Kind, small);
            var badge = new RectangleF(tx, card.Bottom - Px(21), size.Width + Px(6), Px(16));
            Color tint = info.Kind switch { "AUDIO" => Color.FromArgb(46, 160, 110), "IMAGE" => Color.FromArgb(200, 140, 40), _ => Theme.Accent };
            Theme.FillRounded(g, Color.FromArgb(70, tint), badge, Px(8));
            TextRenderer.DrawText(g, info.Kind, small, Rectangle.Round(badge), Color.FromArgb(225, 225, 232),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>Bring the list in line with the project and current proxy states.</summary>
    public void Sync(Project project, MediaLibrary library)
    {
        _list.BeginUpdate();
        var ids = project.Media.Select(m => m.Id).ToHashSet();
        foreach (var item in _list.Items.Cast<ListViewItem>().Where(i => !ids.Contains((Guid)i.Tag!)).ToList())
        {
            var gone = (Guid)item.Tag!;
            _list.Items.Remove(item);
            _info.Remove(gone);
            if (_thumbs.Remove(gone, out var old)) old.Dispose();
        }

        foreach (var m in project.Media)
        {
            var key = m.Id.ToString();
            var item = _list.Items[key] ?? _list.Items.Add(new ListViewItem([m.DisplayName, ""]) { Name = key, Tag = m.Id });
            var st = library.GetState(m.Id);
            string status = st.Status switch
            {
                ProxyStatus.Ready when m.IsStill => $"{m.Width}×{m.Height}",
                ProxyStatus.Ready when m.IsAudioOnly => $"{TimelineControl.FormatTime(m.Duration)}{(m.AudioChannels == 1 ? "  ·  mono" : "")}",
                ProxyStatus.Optimising when m.IsAudioOnly => "Reading audio…",
                ProxyStatus.Ready => $"{TimelineControl.FormatTime(m.Duration)}  ·  {m.Width}×{m.Height}  ·  {m.Fps:0.##} fps",
                ProxyStatus.Optimising => $"Optimising {st.Progress:P0}",
                ProxyStatus.Failed => $"Failed: {(st.Error ?? "").Split('\n', 2)[0].Trim()}",
                _ => "Queued",
            };
            item.SubItems[1].Text = status;
            string kind = m.IsAudioOnly ? "AUDIO" : m.IsStill ? "IMAGE" : "VIDEO";
            double? progress = st.Status == ProxyStatus.Optimising ? st.Progress : st.Status == ProxyStatus.Queued ? 0 : null;
            var info = new CardInfo(kind, status, progress, st.Status == ProxyStatus.Failed);
            if (!_info.TryGetValue(m.Id, out var was) || was != info) { _info[m.Id] = info; _list.Invalidate(item.Bounds); }
            item.ToolTipText = st.Status == ProxyStatus.Failed ? $"{m.OriginalPath}\n\n{st.Error}" : m.OriginalPath;
            if (st.ThumbnailPath is { } tp && !_thumbs.ContainsKey(m.Id) && File.Exists(tp))
            {
                using var fs = File.OpenRead(tp);
                using var img = Image.FromStream(fs);
                _thumbs[m.Id] = new Bitmap(img, 256, 144);
                _list.Invalidate(item.Bounds);
            }
        }
        _list.EndUpdate();
        UpdateEmpty();
        FitTiles();
    }
}
