using System.ComponentModel;
using Flyweight.App.Rendering;
using Flyweight.Core.Model;

namespace Flyweight.App.Forms;

/// <summary>
/// PropertyGrid view-models for the selected annotation. PropertyGrid gives us colour pickers,
/// number editing and validation for free; we copy values back into the model on change.
/// One class per kind so only relevant properties are shown.
/// </summary>
public abstract class AnnotationProperties
{
    public static AnnotationProperties For(Annotation a)
    {
        AnnotationProperties p = a.Kind switch
        {
            AnnotationKind.Arrow => new ArrowProperties(),
            AnnotationKind.Box => new BoxProperties(),
            _ => new TextProperties(),
        };
        p.Load(a);
        return p;
    }

    [Category("Timing"), DisplayName("Duration (s)"), Description("How long it stays on screen.")]
    public double DurationSeconds { get; set; }

    protected virtual void Load(Annotation a) => DurationSeconds = Math.Round((a.End - a.Start).TotalSeconds, 2);

    public virtual void ApplyTo(Annotation a) =>
        a.End = a.Start + TimeSpan.FromSeconds(Math.Max(0.1, DurationSeconds)); // TimelineOps clamps to the clip on display
}

public class ArrowProperties : AnnotationProperties
{
    [Category("Style")] public Color Colour { get; set; }

    [Category("Style"), Description("Line thickness in pixels at 1080p.")]
    public float Thickness { get; set; }

    protected override void Load(Annotation a)
    {
        base.Load(a);
        Colour = ColorHex.Parse(a.StrokeColor, Color.Red);
        Thickness = a.StrokeWidth;
    }

    public override void ApplyTo(Annotation a)
    {
        base.ApplyTo(a);
        a.StrokeColor = ColorHex.ToHex(Color.FromArgb(255, Colour));
        a.StrokeWidth = Math.Clamp(Thickness, 1, 60);
    }
}

public sealed class BoxProperties : ArrowProperties
{
    [Category("Fill"), DisplayName("Filled")] public bool Filled { get; set; }
    [Category("Fill"), DisplayName("Fill colour")] public Color FillColour { get; set; }
    [Category("Fill"), DisplayName("Fill opacity %")] public int FillOpacity { get; set; }

    protected override void Load(Annotation a)
    {
        base.Load(a);
        Filled = a.FillColor is not null;
        var f = ColorHex.Parse(a.FillColor, Color.FromArgb(70, 255, 214, 0));
        FillColour = Color.FromArgb(255, f);
        FillOpacity = (int)Math.Round(f.A / 2.55);
    }

    public override void ApplyTo(Annotation a)
    {
        base.ApplyTo(a);
        a.FillColor = Filled ? ColorHex.ToHex(Color.FromArgb((int)Math.Round(Math.Clamp(FillOpacity, 0, 100) * 2.55), FillColour)) : null;
    }
}

public sealed class TextProperties : AnnotationProperties
{
    [Category("Text")] public string Text { get; set; } = "";
    [Category("Text"), DisplayName("Size"), Description("Font size in pixels at 1080p.")] public float FontSize { get; set; }
    [Category("Text")] public string Font { get; set; } = "Segoe UI";
    [Category("Text"), DisplayName("Colour")] public Color Colour { get; set; }

    [Category("Background"), DisplayName("Background")] public bool HasBackground { get; set; }
    [Category("Background"), DisplayName("Background colour")] public Color BackgroundColour { get; set; }
    [Category("Background"), DisplayName("Background opacity %")] public int BackgroundOpacity { get; set; }

    protected override void Load(Annotation a)
    {
        base.Load(a);
        Text = a.Text;
        FontSize = a.FontSize;
        Font = a.FontFamily;
        Colour = ColorHex.Parse(a.StrokeColor, Color.White);
        HasBackground = a.FillColor is not null;
        var bg = ColorHex.Parse(a.FillColor, Color.FromArgb(176, 0, 0, 0));
        BackgroundColour = Color.FromArgb(255, bg);
        BackgroundOpacity = (int)Math.Round(bg.A / 2.55);
    }

    public override void ApplyTo(Annotation a)
    {
        base.ApplyTo(a);
        if (!string.IsNullOrWhiteSpace(Text)) a.Text = Text;
        a.FontSize = Math.Clamp(FontSize, 8, 400);
        a.FontFamily = string.IsNullOrWhiteSpace(Font) ? "Segoe UI" : Font;
        a.StrokeColor = ColorHex.ToHex(Color.FromArgb(255, Colour));
        a.FillColor = HasBackground ? ColorHex.ToHex(Color.FromArgb((int)Math.Round(Math.Clamp(BackgroundOpacity, 0, 100) * 2.55), BackgroundColour)) : null;
    }
}
