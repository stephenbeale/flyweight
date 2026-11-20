using Flyweight.Core.Editing;
using Flyweight.Core.Model;

namespace Flyweight.App.Controls;

/// <summary>
/// Properties panel for the selected timeline clip: its audio volume and mute.
/// Like <see cref="AnnotationStylePanel"/> it never touches the project: it raises <see cref="Edit"/>
/// and MainForm routes that through ProjectSession, so every change is undoable (rule 2).
/// </summary>
public sealed class ClipPropertiesPanel : UserControl
{
    static readonly Color Bg = Color.FromArgb(28, 28, 32);
    static readonly Color Fg = Color.Gainsboro;

    readonly TableLayoutPanel _table = new()
    {
        Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(8, 6, 8, 6), BackColor = Bg,
    };
    readonly Label _name = new() { ForeColor = Fg, AutoSize = false, Height = 22, Dock = DockStyle.Fill, AutoEllipsis = true };
    readonly Slider _volume = new(0, (int)(TimelineOps.MaxClipVolume * 100), "%");
    readonly CheckBox _muted = new() { Text = "Muted  (M)", ForeColor = Fg, AutoSize = true };
    readonly Label _note = new() { ForeColor = Color.Gray, AutoSize = true, MaximumSize = new Size(240, 0) };
    bool _loading;

    /// <summary>Raised with a change to apply to the selected clip.</summary>
    public event Action<Action<Project, Guid>, StyleEditPhase>? Edit;

    public ClipPropertiesPanel()
    {
        BackColor = Bg;
        ForeColor = Fg;
        AutoScroll = true;
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        Section("Clip");
        Span(_name);
        Section("Audio");
        Row("Volume", _volume);
        Row("", _muted);
        Span(_note);

        _volume.Changed += (v, phase) => Raise((p, id) => TimelineOps.SetClipVolume(p, id, v / 100.0), phase);
        _muted.CheckedChanged += (_, _) =>
        {
            var on = _muted.Checked;
            Raise((p, id) => TimelineOps.SetClipMuted(p, id, on));
        };
        Controls.Add(_table);
    }

    public void ShowClip(Clip c, SourceMedia? media)
    {
        _loading = true;
        try
        {
            _name.Text = media?.DisplayName ?? "(missing)";
            _volume.SetValue((int)Math.Round(c.Volume * 100));
            _muted.Checked = c.Muted;
            bool hasAudio = media is { HasAudio: true };
            _volume.Enabled = hasAudio && !c.Muted;
            _muted.Enabled = hasAudio;
            _note.Text = hasAudio ? "100% is as recorded. Above 100% boosts quiet audio (it can distort)."
                       : media is { IsStill: true } ? "Still images have no sound." : "This clip has no audio track.";
        }
        finally { _loading = false; }
    }

    void Raise(Action<Project, Guid> edit, StyleEditPhase phase = StyleEditPhase.Single)
    {
        if (!_loading) Edit?.Invoke(edit, phase);
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
