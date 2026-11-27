namespace Flyweight.Core.Media;

/// <summary>
/// The project's frame shape (D41): landscape 16:9 for normal YouTube videos, or vertical 9:16 for Shorts.
/// Everything placed on the canvas (annotations, overlays, zoom) is in normalised 0-1 coordinates of THIS shape.
/// </summary>
public enum CanvasShape { Landscape, Vertical }

/// <summary>
/// The output canvas: 16:9, or 9:16 for vertical projects (D41). Sources with another shape (vertical phone clips in a
/// landscape project, 4:3) are fitted inside it with bars, identically in preview and export, so a normalised
/// annotation coordinate means the same spot in both. Proxies are ALWAYS 16:9 (shared between projects); a vertical
/// project's preview cuts the picture out of the proxy frame and fits it into its own canvas.
/// </summary>
public static class Canvas
{
    public const int ProxyWidth = 960;
    public const int ProxyHeight = 540;

    /// <summary>Width / height of the canvas: 16/9, or 9/16 when vertical.</summary>
    public static double Aspect(CanvasShape shape) => shape == CanvasShape.Vertical ? 9.0 / 16 : 16.0 / 9;

    /// <summary>A landscape size (long side first) turned to the canvas shape: 1920x1080 becomes 1080x1920 when vertical.</summary>
    public static (int W, int H) Orient(CanvasShape shape, int w, int h) => shape == CanvasShape.Vertical ? (h, w) : (w, h);

    /// <summary>Filter that scales any input to fit W×H and pads to exactly W×H.</summary>
    public static string FitFilter(int w, int h) =>
        $"scale={w}:{h}:force_original_aspect_ratio=decrease:flags=bicubic," +
        $"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1";
}

/// <summary>
/// What fills the bars around a clip that isn't 16:9 (a portrait phone clip, a 4:3 photo): plain black, or a
/// blurred, darkened, zoomed-to-fill copy of the clip itself (D40). A project-wide setting.
/// </summary>
public enum BackgroundFill { Black, Blur }

public enum ExportResolution { P1080, P1440, P2160 }

public static class ExportResolutions
{
    /// <summary>Dropdown labels for a canvas shape, in the same order as <see cref="All"/>.</summary>
    public static IReadOnlyList<string> Labels(CanvasShape shape) => shape == CanvasShape.Vertical
        ? new[] { "1080p vertical (1080×1920)", "1440p vertical (1440×2560)", "4K vertical (2160×3840)" }
        : [.. All.Select(r => r.Label)];

    /// <summary>Dropdown order matches the enum, so an index maps straight to a value.</summary>
    public static readonly IReadOnlyList<(ExportResolution Value, string Label)> All = [
        (ExportResolution.P1080, "1080p (1920×1080)"),
        (ExportResolution.P1440, "1440p (2560×1440): better YouTube quality"),
        (ExportResolution.P2160, "4K (3840×2160)"),
    ];
}

/// <summary>Settings for YouTube-friendly export. Codec choices are fixed on purpose.</summary>
public sealed record ExportSettings
{
    public ExportResolution Resolution { get; init; } = ExportResolution.P1080;

    /// <summary>0 = use the first clip's frame rate.</summary>
    public double FrameRate { get; init; }

    /// <summary>x264 quality. 18 is visually lossless-ish; YouTube re-encodes anyway.</summary>
    public int Crf { get; init; } = 18;

    /// <summary>x264 preset for CPU exports (and the fallback). "veryfast" matched "medium" on real footage at 2.6× the speed.</summary>
    public string Preset { get; init; } = "veryfast";

    /// <summary>
    /// Use the graphics chip's encoder (Intel Quick Sync) when it works, falling back to x264. Still H.264 High
    /// yuv420p for YouTube: this is not a codec choice, just a faster way to make the same file (rule 9).
    /// </summary>
    public bool UseHardwareEncoder { get; init; } = true;

    /// <summary>The output size for a project of <paramref name="shape"/>: <see cref="Size"/>, turned upright when vertical.</summary>
    public (int Width, int Height) SizeFor(CanvasShape shape) => Canvas.Orient(shape, Size.Width, Size.Height);

    /// <summary>Landscape output size (long side first). Use <see cref="SizeFor"/> for a project.</summary>
    public (int Width, int Height) Size => Resolution switch
    {
        ExportResolution.P1440 => (2560, 1440),
        ExportResolution.P2160 => (3840, 2160),
        _ => (1920, 1080),
    };
}
