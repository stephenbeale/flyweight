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

    public MediaBinControl()
    {
        var header = new Label
        {
            Text = "Media", Dock = DockStyle.Top, Height = 26, Padding = new Padding(8, 6, 0, 0),
            ForeColor = Color.Silver, BackColor = Color.FromArgb(38, 38, 44),
        };
        _list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Tile, LargeImageList = _images, TileSize = new Size(260, 80),
            BackColor = Color.FromArgb(28, 28, 32), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.None,
            MultiSelect = false,
        };
        _list.Columns.Add("Name");
        _list.Columns.Add("Status");
        _list.ItemActivate += (_, _) =>
        {
            if (_list.SelectedItems.Count == 1 && _list.SelectedItems[0].Tag is Guid id) AddToTimelineRequested?.Invoke(this, id);
        };
        var hint = new Label
        {
            Text = "Double-click to add to the timeline", Dock = DockStyle.Bottom, Height = 22,
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
                ProxyStatus.Ready => $"{TimelineControl.FormatTime(m.Duration)}  ·  {m.Width}×{m.Height}  ·  {m.Fps:0.##} fps",
                ProxyStatus.Optimising => $"Optimising {st.Progress:P0}",
                ProxyStatus.Failed => $"Failed: {st.Error}",
                _ => "Queued",
            };
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
