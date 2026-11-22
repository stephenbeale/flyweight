using Flyweight.Core.Editing;
using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.App.Controls;

/// <summary>
/// Properties panel for the selected timeline clip or audio-track item: its volume and mute, and for
/// clips the picture (zoom, brightness, contrast).
/// Like <see cref="AnnotationStylePanel"/> it never touches the project: it raises
/// <see cref="VolumeChanged"/> / <see cref="MutedChanged"/> and MainForm applies them to whichever is
/// selected through ProjectSession, so every change is undoable (rule 2).
/// </summary>
public sealed class ClipPropertiesPanel : UserControl
{
    static readonly Color Bg = Color.FromArgb(28, 28, 32);
    static readonly Color Fg = Color.Gainsboro;

    readonly TableLayoutPanel _table = new()
    {
        Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(8, 6, 8, 6), BackColor = Bg,
    };
    readonly Label _heading;
    readonly Label _name = new() { ForeColor = Fg, AutoSize = true, MaximumSize = new Size(250, 0), Margin = new Padding(0, 2, 0, 4) }; // wraps "Changes apply to all…"
    readonly Slider _volume = new(0, (int)(TimelineOps.MaxClipVolume * 100), "%");
    readonly CheckBox _muted = new() { Text = "Muted  (M)", ForeColor = Fg, AutoSize = true };
    readonly Label _note = new() { ForeColor = Color.Gray, AutoSize = true, MaximumSize = new Size(240, 0) };
    readonly Slider _zoom = new(100, (int)(ClipLook.MaxZoom * 100), "%");
    readonly Slider _panX = new(0, 100, "");
    readonly Slider _panY = new(0, 100, "");
    readonly Slider _brightness = new(-100, 100, "");
    readonly Slider _contrast = new(-100, 100, "");
    readonly Theme.RoundButton _resetLook = new() { Text = "Reset picture", Width = 130, Height = 32 };
    readonly Label _lookNote = new()
    {
        ForeColor = Color.Gray, AutoSize = true, MaximumSize = new Size(240, 0),
        Text = "Zoom crops the original footage, so 4K clips stay sharp. Annotations aren't zoomed.",
    };
    readonly List<Control> _pictureControls = new();
    readonly ComboBox _speed = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(40, 40, 47), ForeColor = Color.Gainsboro };
    readonly List<Control> _speedControls = new();
    bool _loading;
    int _multi = 1;

    /// <summary>Volume as a gain (1 = 100%), and how it should reach the undo stack.</summary>
    public event Action<double, StyleEditPhase>? VolumeChanged;
    public event Action<bool>? MutedChanged;

    /// <summary>A picture setting changed: the new value in model units (zoom 1–4, pan 0–1, brightness/contrast -1–1).</summary>
    public event Action<LookSetting, double, StyleEditPhase>? LookChanged;
    public event Action? LookReset;

    /// <summary>The clip's playback speed (0.25–4) was picked.</summary>
    public event Action<double>? SpeedChanged;

    public ClipPropertiesPanel()
    {
        BackColor = Bg;
        ForeColor = Fg;
        AutoScroll = true;
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _heading = Section("Clip");
        Span(_name);
        foreach (var sp in TimelineOps.SpeedPresets) _speed.Items.Add($"{sp:0.##}×");
        _speedControls.Add(Row("Speed", _speed));
        _speedControls.Add(_speed);
        _speed.SelectedIndexChanged += (_, _) =>
        {
            if (!_loading && _speed.SelectedIndex >= 0) SpeedChanged?.Invoke(TimelineOps.SpeedPresets[_speed.SelectedIndex]);
        };
        Section("Audio");
        Row("Volume", _volume);
        Row("", _muted);
        Span(_note);
        _pictureControls.Add(Section("Picture"));
        Row("Zoom", _zoom);
        Row("Left / right", _panX);
        Row("Up / down", _panY);
        Row("Brightness", _brightness);
        Row("Contrast", _contrast);
        Span(_resetLook);
        _pictureControls.Add(_resetLook);
        Span(_lookNote);
        _pictureControls.Add(_lookNote);

        void Look(Slider s, LookSetting which, double scale) =>
            s.Changed += (v, phase) => { if (!_loading) LookChanged?.Invoke(which, v / scale, phase); };
        Look(_zoom, LookSetting.Zoom, 100);
        Look(_panX, LookSetting.PanX, 100);
        Look(_panY, LookSetting.PanY, 100);
        Look(_brightness, LookSetting.Brightness, 100);
        Look(_contrast, LookSetting.Contrast, 100);
        _resetLook.Click += (_, _) => LookReset?.Invoke();

        _volume.Changed += (v, phase) => { if (!_loading) VolumeChanged?.Invoke(v / 100.0, phase); };
        _muted.CheckedChanged += (_, _) => { if (!_loading) MutedChanged?.Invoke(_muted.Checked); };
        Controls.Add(_table);
    }

    /// <param name="selectedCount">How many clips are selected: with more than one, edits here apply to all of them.</param>
    public void ShowClip(Clip c, SourceMedia? media, int selectedCount = 1)
    {
        _loading = true;
        try
        {
            SetPictureVisible(true);
            foreach (var sc in _speedControls) sc.Visible = true;
            int si = Array.FindIndex(TimelineOps.SpeedPresets, v => Math.Abs(v - c.Speed) < 0.001);
            if (si < 0) { _speed.Items.Add($"{c.Speed:0.##}×"); si = _speed.Items.Count - 1; } // a hand-edited file
            _speed.SelectedIndex = si;
            _speed.Enabled = media is not { IsStill: true }; // stills: drag the edge to change how long they show
            _zoom.SetValue((int)Math.Round(c.Zoom * 100));
            _panX.SetValue((int)Math.Round(c.PanX * 100));
            _panY.SetValue((int)Math.Round(c.PanY * 100));
            _brightness.SetValue((int)Math.Round(c.Brightness * 100));
            _contrast.SetValue((int)Math.Round(c.Contrast * 100));
            bool zoomed = ClipLook.IsZoomed(c);
            _panX.Enabled = _panY.Enabled = zoomed; // nothing to pan at 100%
            _resetLook.Enabled = !ClipLook.IsDefault(c);
        }
        finally { _loading = false; }

        bool hasAudio = media is { HasAudio: true };
        _multi = selectedCount;
        Show(selectedCount > 1 ? $"{selectedCount} clips selected" : "Clip", media, c.Volume, c.Muted, hasAudio || selectedCount > 1,
            hasAudio ? "100% is as recorded. Above 100% boosts quiet audio (it can distort)."
            : media is { IsStill: true } ? "Still images have no sound." : "This clip has no audio track.");
    }

    public void ShowAudio(AudioItem a, SourceMedia? media)
    {
        _multi = 1;
        SetPictureVisible(false);
        foreach (var sc in _speedControls) sc.Visible = false;
        Show("Audio track item", media, a.Volume, a.Muted, hasAudio: true,
            $"Starts at {TimelineControl.FormatTime(a.Start, tenths: true)}, plays {a.Duration.TotalSeconds:0.0}s. " +
            "It plays over the clips' own sound: turn clips down or mute them (M) to hear only this.");
    }

    void SetPictureVisible(bool on)
    {
        foreach (var c in _pictureControls) c.Visible = on;
    }

    void Show(string heading, SourceMedia? media, double volume, bool muted, bool hasAudio, string note)
    {
        _loading = true;
        try
        {
            _heading.Text = heading.ToUpperInvariant();
            _name.Text = _multi > 1 ? $"Changes apply to all {_multi}. Showing {media?.DisplayName ?? "(missing)"}." : media?.DisplayName ?? "(missing)";
            _name.ForeColor = _multi > 1 ? Theme.Accent : Fg;
            _volume.SetValue((int)Math.Round(volume * 100));
            _muted.Checked = muted;
            _volume.Enabled = hasAudio && !muted;
            _muted.Enabled = hasAudio;
            _note.Text = note;
        }
        finally { _loading = false; }
    }

    Label Section(string title)
    {
        var l = new Label
        {
            Text = title.ToUpperInvariant(), ForeColor = Color.Gray, AutoSize = true,
            Font = new Font(Font.FontFamily, 7.5f, FontStyle.Bold), Margin = new Padding(0, _table.RowCount == 0 ? 2 : 12, 0, 2),
        };
        Span(l);
        return l;
    }

    void Span(Control c)
    {
        _table.RowCount++;
        _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _table.Controls.Add(c, 0, _table.RowCount - 1);
        _table.SetColumnSpan(c, 2);
    }

    Label Row(string label, Control editor)
    {
        _table.RowCount++;
        _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var l = new Label { Text = label, ForeColor = Fg, AutoSize = false, Height = 28, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        editor.Margin = new Padding(0, 3, 0, 3);
        if (_pictureControls.Count > 0) { _pictureControls.Add(l); _pictureControls.Add(editor); } // rows after the Picture heading
        _table.Controls.Add(l, 0, _table.RowCount - 1);
        _table.Controls.Add(editor, 1, _table.RowCount - 1);
        return l;
    }
}

public enum LookSetting { Zoom, PanX, PanY, Brightness, Contrast }
