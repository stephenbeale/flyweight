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
public sealed class ClipPropertiesPanel : PropertySheet
{
    static readonly Color Fg = Theme.Text;

    readonly PropertyGroup _heading, _pictureGroup, _cropGroup;
    readonly Label _name = new() { ForeColor = Fg, AutoSize = true, Margin = new Padding(0, 2, 0, 4) }; // wraps "Changes apply to all…"
    readonly Slider _volume = new(0, (int)(TimelineOps.MaxClipVolume * 100), "%");
    readonly Theme.ToggleSwitch _muted = new() { Text = "Muted", Tip = "Mute (Shift+M)" };
    readonly Label _note = new() { ForeColor = Color.Gray, AutoSize = true, Margin = new Padding(0, 2, 0, 4) };
    readonly Slider _zoom = new(100, (int)(ClipLook.MaxZoom * 100), "%");
    readonly Slider _panX = new(0, 100, "");
    readonly Slider _panY = new(0, 100, "");
    readonly Slider _brightness = new(-100, 100, "");
    readonly Slider _contrast = new(-100, 100, "");
    readonly Slider _cropL = new(0, (int)(ClipLook.MaxCropEach * 100), "%");
    readonly Slider _cropR = new(0, (int)(ClipLook.MaxCropEach * 100), "%");
    readonly Slider _cropT = new(0, (int)(ClipLook.MaxCropEach * 100), "%");
    readonly Slider _cropB = new(0, (int)(ClipLook.MaxCropEach * 100), "%");
    readonly Theme.RoundButton _resetLook = new() { Text = "Reset picture", Width = 130, Height = 32 };
    readonly Label _lookNote = new()
    {
        ForeColor = Color.Gray, AutoSize = true, Margin = new Padding(0, 4, 0, 2),
        Text = "Crop cuts edges off (the rest is fitted to the frame); zoom magnifies. Both work on the original footage, so 4K clips stay sharp. Annotations aren't zoomed.",
    };
    readonly SpeedBox _speed = new();
    readonly List<Control> _speedControls = new();

    /// <summary>Stills only: how long the image shows (seconds). Videos get Speed instead.</summary>
    readonly NumericUpDown _length = new()
    {
        // Gaps can be longer than a still (TimelineOps clamps each to its own limit).
        Minimum = 0.1m, Maximum = (decimal)TimelineOps.MaxGapLength.TotalSeconds, DecimalPlaces = 1, Increment = 1,
        BackColor = Color.FromArgb(46, 46, 54), ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, Width = 80,
    };
    readonly List<Control> _lengthControls = new();

    /// <summary>A still's length on the timeline was changed (seconds). Later clips and the other tracks move along.</summary>
    public event Action<double>? StillLengthChanged;
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

    public ClipPropertiesPanel() : base("clip")
    {
        _heading = Group("Clip");
        Span(_name);
        _speedControls.Add(Row("Speed", _speed));
        _speedControls.Add(_speed);
        _speed.Picked += v => { if (!_loading) SpeedChanged?.Invoke(v); };
        _lengthControls.Add(Row("Length (s)", _length));
        _lengthControls.Add(_length);
        new ToolTip().SetToolTip(_length, "How long the image shows. Everything after it (clips, overlays, audio) moves along. You can also drag its right edge.");
        _length.ValueChanged += (_, _) => { if (!_loading) StillLengthChanged?.Invoke((double)_length.Value); };
        Group("Audio");
        Row("Volume", _volume);
        Row("", _muted);
        Span(_note);
        _pictureGroup = Group("Picture");
        Row("Zoom", _zoom);
        Row("Left / right", _panX);
        Row("Up / down", _panY);
        Row("Brightness", _brightness);
        Row("Contrast", _contrast);
        Span(_resetLook);
        _cropGroup = Group("Crop");
        Row("Left", _cropL);
        Row("Right", _cropR);
        Row("Top", _cropT);
        Row("Bottom", _cropB);
        Span(_lookNote);

        void Look(Slider s, LookSetting which, double scale) =>
            s.Changed += (v, phase) => { if (!_loading) LookChanged?.Invoke(which, v / scale, phase); };
        Look(_zoom, LookSetting.Zoom, 100);
        Look(_panX, LookSetting.PanX, 100);
        Look(_panY, LookSetting.PanY, 100);
        Look(_brightness, LookSetting.Brightness, 100);
        Look(_contrast, LookSetting.Contrast, 100);
        Look(_cropL, LookSetting.CropLeft, 100);
        Look(_cropR, LookSetting.CropRight, 100);
        Look(_cropT, LookSetting.CropTop, 100);
        Look(_cropB, LookSetting.CropBottom, 100);
        _resetLook.Click += (_, _) => LookReset?.Invoke();

        _volume.Changed += (v, phase) => { if (!_loading) VolumeChanged?.Invoke(v / 100.0, phase); };
        _muted.CheckedChanged += (_, _) => { if (!_loading) MutedChanged?.Invoke(_muted.Checked); };
    }

    /// <param name="selectedCount">How many clips are selected: with more than one, edits here apply to all of them.</param>
    public void ShowClip(Clip c, SourceMedia? media, int selectedCount = 1)
    {
        if (c.IsGap) { ShowGap(c, selectedCount); return; }
        _loading = true;
        try
        {
            SetPictureVisible(true);
            bool still = media is { IsStill: true };
            foreach (var sc in _speedControls) sc.Visible = !still; // stills have no speed…
            foreach (var lc in _lengthControls) lc.Visible = still;  // …they have a length instead
            _speed.SetValue(c.Speed);
            if (still)
                _length.Value = Math.Clamp((decimal)Math.Round(c.Duration.TotalSeconds, 1), _length.Minimum, _length.Maximum);
            _zoom.SetValue((int)Math.Round(c.Zoom * 100));
            _panX.SetValue((int)Math.Round(c.PanX * 100));
            _panY.SetValue((int)Math.Round(c.PanY * 100));
            _brightness.SetValue((int)Math.Round(c.Brightness * 100));
            _contrast.SetValue((int)Math.Round(c.Contrast * 100));
            _cropL.SetValue((int)Math.Round(c.CropLeft * 100));
            _cropR.SetValue((int)Math.Round(c.CropRight * 100));
            _cropT.SetValue((int)Math.Round(c.CropTop * 100));
            _cropB.SetValue((int)Math.Round(c.CropBottom * 100));
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

    /// <summary>A gap on the main track: only its length can be changed (no picture, no sound).</summary>
    void ShowGap(Clip c, int selectedCount)
    {
        _loading = true;
        try
        {
            SetPictureVisible(false);
            foreach (var sc in _speedControls) sc.Visible = false;
            foreach (var lc in _lengthControls) lc.Visible = true;
            _length.Value = Math.Clamp((decimal)Math.Round(c.Duration.TotalSeconds, 1), _length.Minimum, _length.Maximum);
        }
        finally { _loading = false; }
        _multi = selectedCount;
        Show(selectedCount > 1 ? $"{selectedCount} clips selected" : "Gap", null, 1, false, hasAudio: false,
            "Empty space: black, with only the audio track and overlays playing. Delete it (Del) to close it up.");
        _name.Text = "Gap (left by Delete)";
    }

    public void ShowAudio(AudioItem a, SourceMedia? media)
    {
        _multi = 1;
        SetPictureVisible(false);
        foreach (var sc in _speedControls) sc.Visible = false;
        foreach (var lc in _lengthControls) lc.Visible = false;
        Show("Audio track item", media, a.Volume, a.Muted, hasAudio: true,
            $"Starts at {TimelineControl.FormatTime(a.Start, tenths: true)}, plays {a.Duration.TotalSeconds:0.0}s. " +
            "It plays over the clips' own sound: turn clips down or mute them (Shift+M) to hear only this.");
    }

    void SetPictureVisible(bool on) => _pictureGroup.Visible = _cropGroup.Visible = on;

    void Show(string heading, SourceMedia? media, double volume, bool muted, bool hasAudio, string note)
    {
        _loading = true;
        try
        {
            _heading.Title = heading;
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
}

public enum LookSetting { Zoom, PanX, PanY, Brightness, Contrast, CropLeft, CropTop, CropRight, CropBottom }
