using System.Diagnostics;
using Flyweight.App.Controls;

namespace Flyweight.App.Forms;

/// <summary>
/// "Find free sound effects": links to libraries whose licences suit (monetised) YouTube videos, each with a one-line
/// licence note. Links only, opened in the browser: no network code in the app (owner, 2026-11-28, option 1).
/// BBC Sound Effects is left out on purpose: its licence is personal/educational/research use only.
/// </summary>
public sealed class SoundEffectsForm : Form
{
    static readonly (string Name, string Url, string Note)[] Libraries =
    [
        ("Pixabay sound effects", "https://pixabay.com/sound-effects/", "Free, commercial use OK, no credit needed. The easiest place to start."),
        ("Freesound", "https://freesound.org/", "Licence varies per sound: filter by \"Creative Commons 0\" for no-strings sounds. CC-BY needs a credit; avoid NonCommercial."),
        ("YouTube Audio Library", "https://www.youtube.com/audiolibrary", "In YouTube Studio. Free for YouTube videos; some sounds ask for a credit in the description."),
        ("Mixkit", "https://mixkit.co/free-sound-effects/", "Free, commercial use OK, no credit needed."),
        ("Sonniss GDC bundles", "https://sonniss.com/gameaudiogdc", "Big royalty-free packs (several GB each), commercial use OK."),
        ("ZapSplat", "https://www.zapsplat.com/", "Free account; the free tier needs a credit (paid removes it)."),
    ];

    public SoundEffectsForm()
    {
        Text = "Find free sound effects";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Window;
        ForeColor = Theme.Text;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(LogicalToDeviceUnits(20), LogicalToDeviceUnits(16), LogicalToDeviceUnits(20), LogicalToDeviceUnits(14));
        int wrap = LogicalToDeviceUnits(480);

        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill, BackColor = Theme.Window,
        };
        stack.Controls.Add(new Label
        {
            Text = "Free sound effects you can use on YouTube", AutoSize = true,
            Font = new Font(Font.FontFamily, 12f, FontStyle.Bold), Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(10)),
        });

        foreach (var (name, url, note) in Libraries)
        {
            var link = new LinkLabel
            {
                Text = name + "  ↗", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0),
                LinkColor = Color.FromArgb(110, 180, 255), ActiveLinkColor = Color.White, VisitedLinkColor = Color.FromArgb(160, 140, 255),
                LinkBehavior = LinkBehavior.HoverUnderline,
            };
            new ToolTip().SetToolTip(link, url);
            link.LinkClicked += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); link.LinkVisited = true; }
                catch (Exception ex) { MessageBox.Show(this, $"Couldn't open the browser:\n\n{ex.Message}\n\nThe address is {url}", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); }
            };
            stack.Controls.Add(link);
            stack.Controls.Add(new Label
            {
                Text = note, AutoSize = true, MaximumSize = new Size(wrap, 0), ForeColor = Color.Silver,
                Margin = new Padding(0, LogicalToDeviceUnits(2), 0, LogicalToDeviceUnits(10)),
            });
        }

        stack.Controls.Add(new Panel { Height = 1, Width = wrap, BackColor = Theme.Border, Margin = new Padding(0, LogicalToDeviceUnits(4), 0, LogicalToDeviceUnits(10)) });
        stack.Controls.Add(new Label
        {
            Text = "Got one? Import it (Ctrl+I): it goes into Media. Select it there and press I to put it on the audio track at the playhead.\n" +
                   "Licences can change, so check the sound's own page before you publish.",
            AutoSize = true, MaximumSize = new Size(wrap, 0), Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(12)),
        });

        var close = new Theme.RoundButton { Text = "Close", Width = LogicalToDeviceUnits(90), Height = LogicalToDeviceUnits(34), Margin = new Padding(0) };
        close.Click += (_, _) => Close();
        stack.Controls.Add(close);

        Controls.Add(stack);
        CancelButton = close;
        AcceptButton = close;
    }
}
