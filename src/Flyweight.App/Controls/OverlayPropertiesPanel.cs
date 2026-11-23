using Flyweight.Core.Media;
using Flyweight.Core.Editing;
using Flyweight.Core.Model;

namespace Flyweight.App.Controls;

/// <summary>
/// Properties for the selected overlay (V2, V3…): size, position, quick placements, volume and mute.
/// Like the other panels it never touches the project: it raises <see cref="Edit"/> with a TimelineOps call and
/// MainForm applies it to the selected overlay through ProjectSession, so every change is undoable (rule 2).
/// </summary>
public sealed class OverlayPropertiesPanel : UserControl
{
    static readonly Color Bg = Color.FromArgb(28, 28, 32);
    static readonly Color Fg = Color.Gainsboro;

    static readonly (OverlayPosition Pos, string Label)[] Placements =
    {
        (OverlayPosition.BottomRight, "Bottom right"), (OverlayPosition.BottomLeft, "Bottom left"),
        (OverlayPosition.TopRight, "Top right"), (OverlayPosition.TopLeft, "Top left"),
        (OverlayPosition.Centre, "Centre"), (OverlayPosition.FullFrame, "Full frame (cutaway)"),
    };

    readonly TableLayoutPanel _table = new()
    {
        Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(8, 6, 8, 6), BackColor = Bg,
    };
    readonly Label _name = new() { ForeColor = Fg, AutoSize = true, MaximumSize = new Size(250, 0), Margin = new Padding(0, 2, 0, 4) };
    readonly Label _timing = new() { ForeColor = Color.Gray, AutoSize = true, MaximumSize = new Size(250, 0) };
    readonly ComboBox _place = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(40, 40, 47), ForeColor = Fg };
    readonly Slider _size = new((int)(OverlayLayout.MinScale * 100), (int)(OverlayLayout.MaxScale * 100), "%");
    readonly Slider _x = new(0, 100, "");
    readonly Slider _y = new(0, 100, "");
    readonly Slider _zoom = new(100, (int)(OverlayLayout.MaxZoom * 100), "%");
    readonly Slider _panX = new(0, 100, "");
    readonly Slider _panY = new(0, 100, "");
    readonly Slider _cropL = new(0, (int)(ClipLook.MaxCropEach * 100), "%");
    readonly Slider _cropR = new(0, (int)(ClipLook.MaxCropEach * 100), "%");
    readonly Slider _cropT = new(0, (int)(ClipLook.MaxCropEach * 100), "%");
    readonly Slider _cropB = new(0, (int)(ClipLook.MaxCropEach * 100), "%");
    readonly Slider _volume = new(0, (int)(TimelineOps.MaxClipVolume * 100), "%");
    readonly CheckBox _muted = new() { Text = "Muted  (Shift+M)", ForeColor = Fg, AutoSize = true };
    readonly Label _note = new()
    {
        ForeColor = Color.Gray, AutoSize = true, MaximumSize = new Size(240, 0),
        Text = "Drag it in the preview to move it; scroll over it there to zoom in. Crop trims its edges (the box shrinks with it). Its sound plays over the main clips: " +
               "mute it, or right-click it → Separate audio.",
    };
    bool _loading;

    /// <summary>An edit for the selected overlay (a TimelineOps call), and how it should reach the undo stack.</summary>
    public event Action<Action<Project, Guid>, StyleEditPhase>? Edit;

    public OverlayPropertiesPanel()
    {
        BackColor = Bg;
        ForeColor = Fg;
        AutoScroll = true;
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        Section("Overlay");
        Span(_name);
        Span(_timing);
        Section("Picture");
        _place.Items.Add("Place…");
        foreach (var (_, label) in Placements) _place.Items.Add(label);
        Row("Place", _place);
        Row("Size", _size);
        Row("Across", _x);
        Row("Down", _y);
        Section("Crop");
        Row("Left", _cropL);
        Row("Right", _cropR);
        Row("Top", _cropT);
        Row("Bottom", _cropB);
        Section("Zoom");
        Row("Zoom", _zoom);
        Row("Pan ↔", _panX);
        Row("Pan ↕", _panY);
        Section("Audio");
        Row("Volume", _volume);
        Row("", _muted);
        Span(_note);

        _place.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _place.SelectedIndex <= 0) return;
            var pos = Placements[_place.SelectedIndex - 1].Pos;
            Edit?.Invoke((p, id) => TimelineOps.PlaceOverlay(p, id, pos), StyleEditPhase.Single);
            BeginInvoke(() => { _loading = true; _place.SelectedIndex = 0; _loading = false; }); // it's an action, not a setting
        };
        _size.Changed += (v, phase) => { if (!_loading) Edit?.Invoke((p, id) => TimelineOps.SetOverlayLayout(p, id, null, null, v / 100.0), phase); };
        _x.Changed += (v, phase) => { if (!_loading) Edit?.Invoke((p, id) => TimelineOps.SetOverlayLayout(p, id, v / 100.0, null, null), phase); };
        _y.Changed += (v, phase) => { if (!_loading) Edit?.Invoke((p, id) => TimelineOps.SetOverlayLayout(p, id, null, v / 100.0, null), phase); };
        void Crop(Slider s, CropSide side) =>
            s.Changed += (v, phase) => { if (!_loading) Edit?.Invoke((p, id) => TimelineOps.SetOverlayCrop(p, id, side, v / 100.0), phase); };
        Crop(_cropL, CropSide.Left);
        Crop(_cropR, CropSide.Right);
        Crop(_cropT, CropSide.Top);
        Crop(_cropB, CropSide.Bottom);
        _zoom.Changed += (v, phase) => { if (!_loading) Edit?.Invoke((p, id) => TimelineOps.SetOverlayZoom(p, id, v / 100.0), phase); };
        _panX.Changed += (v, phase) => { if (!_loading) Edit?.Invoke((p, id) => TimelineOps.SetOverlayPan(p, id, v / 100.0, null), phase); };
        _panY.Changed += (v, phase) => { if (!_loading) Edit?.Invoke((p, id) => TimelineOps.SetOverlayPan(p, id, null, v / 100.0), phase); };
        _volume.Changed += (v, phase) => { if (!_loading) Edit?.Invoke((p, id) => TimelineOps.SetOverlayVolume(p, id, v / 100.0), phase); };
        _muted.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            bool on = _muted.Checked;
            Edit?.Invoke((p, id) => TimelineOps.SetOverlayMuted(p, id, on), StyleEditPhase.Single);
        };
        Controls.Add(_table);
    }

    public void ShowOverlay(OverlayClip o, SourceMedia? media)
    {
        _loading = true;
        try
        {
            _name.Text = $"{media?.DisplayName ?? "(missing)"}  ·  V{o.Track + 1}";
            _timing.Text = $"Shows {TimelineControl.FormatTime(o.Start, tenths: true)} – {TimelineControl.FormatTime(o.End, tenths: true)} " +
                           $"({o.Duration.TotalSeconds:0.0}s)";
            if (_place.SelectedIndex < 0) _place.SelectedIndex = 0;
            _size.SetValue((int)Math.Round(o.Scale * 100));
            _x.SetValue((int)Math.Round(o.X * 100));
            _y.SetValue((int)Math.Round(o.Y * 100));
            _zoom.SetValue((int)Math.Round(o.Zoom * 100));
            _cropL.SetValue((int)Math.Round(o.CropLeft * 100));
            _cropR.SetValue((int)Math.Round(o.CropRight * 100));
            _cropT.SetValue((int)Math.Round(o.CropTop * 100));
            _cropB.SetValue((int)Math.Round(o.CropBottom * 100));
            _panX.SetValue((int)Math.Round(o.PanX * 100));
            _panY.SetValue((int)Math.Round(o.PanY * 100));
            _panX.Enabled = _panY.Enabled = OverlayLayout.IsZoomed(o); // nothing to pan at 100%
            bool hasAudio = media is { HasAudio: true, IsStill: false };
            _volume.SetValue((int)Math.Round(o.Volume * 100));
            _muted.Checked = o.Muted;
            _volume.Enabled = hasAudio && !o.Muted;
            _muted.Enabled = hasAudio;
        }
        finally { _loading = false; }
    }

    void Section(string title)
    {
        var l = new Label
        {
            Text = title.ToUpperInvariant(), ForeColor = Color.Gray, AutoSize = true,
            Font = new Font(Font.FontFamily, 7.5f, FontStyle.Bold), Margin = new Padding(0, _table.RowCount == 0 ? 2 : 12, 0, 2),
        };
        Span(l);
    }

    void Span(Control c)
    {
        _table.RowCount++;
        _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _table.Controls.Add(c, 0, _table.RowCount - 1);
        _table.SetColumnSpan(c, 2);
    }

    void Row(string label, Control editor)
    {
        _table.RowCount++;
        _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var l = new Label { Text = label, ForeColor = Fg, AutoSize = false, Height = 28, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        editor.Margin = new Padding(0, 3, 0, 3);
        _table.Controls.Add(l, 0, _table.RowCount - 1);
        _table.Controls.Add(editor, 1, _table.RowCount - 1);
    }
}
