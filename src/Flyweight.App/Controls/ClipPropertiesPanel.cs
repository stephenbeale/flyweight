using Flyweight.Core.Editing;
using Flyweight.Core.Model;

namespace Flyweight.App.Controls;

/// <summary>
/// Properties panel for the selected timeline clip or audio-track item: its volume and mute.
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
    readonly Label _name = new() { ForeColor = Fg, AutoSize = false, Height = 22, Dock = DockStyle.Fill, AutoEllipsis = true };
    readonly Slider _volume = new(0, (int)(TimelineOps.MaxClipVolume * 100), "%");
    readonly CheckBox _muted = new() { Text = "Muted  (M)", ForeColor = Fg, AutoSize = true };
    readonly Label _note = new() { ForeColor = Color.Gray, AutoSize = true, MaximumSize = new Size(240, 0) };
    bool _loading;

    /// <summary>Volume as a gain (1 = 100%), and how it should reach the undo stack.</summary>
    public event Action<double, StyleEditPhase>? VolumeChanged;
    public event Action<bool>? MutedChanged;

    public ClipPropertiesPanel()
    {
        BackColor = Bg;
        ForeColor = Fg;
        AutoScroll = true;
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _heading = Section("Clip");
        Span(_name);
        Section("Audio");
        Row("Volume", _volume);
        Row("", _muted);
        Span(_note);

        _volume.Changed += (v, phase) => { if (!_loading) VolumeChanged?.Invoke(v / 100.0, phase); };
        _muted.CheckedChanged += (_, _) => { if (!_loading) MutedChanged?.Invoke(_muted.Checked); };
        Controls.Add(_table);
    }

    public void ShowClip(Clip c, SourceMedia? media)
    {
        bool hasAudio = media is { HasAudio: true };
        Show("Clip", media, c.Volume, c.Muted, hasAudio,
            hasAudio ? "100% is as recorded. Above 100% boosts quiet audio (it can distort)."
            : media is { IsStill: true } ? "Still images have no sound." : "This clip has no audio track.");
    }

    public void ShowAudio(AudioItem a, SourceMedia? media) =>
        Show("Audio track item", media, a.Volume, a.Muted, hasAudio: true,
            $"Starts at {TimelineControl.FormatTime(a.Start, tenths: true)}, plays {a.Duration.TotalSeconds:0.0}s. " +
            "It plays over the clips' own sound: turn clips down or mute them (M) to hear only this.");

    void Show(string heading, SourceMedia? media, double volume, bool muted, bool hasAudio, string note)
    {
        _loading = true;
        try
        {
            _heading.Text = heading.ToUpperInvariant();
            _name.Text = media?.DisplayName ?? "(missing)";
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
