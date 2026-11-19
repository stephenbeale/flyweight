namespace Flyweight.Core.Media;

/// <summary>
/// The output is always a 16:9 canvas. Sources with another shape (vertical phone clips,
/// 4:3) are fitted inside it with black bars, identically in proxies and export, so a
/// normalised annotation coordinate means the same spot in both.
/// </summary>
public static class Canvas
{
    public const int ProxyWidth = 960;
    public const int ProxyHeight = 540;

    /// <summary>Filter that scales any input to fit W×H and pads to exactly W×H.</summary>
    public static string FitFilter(int w, int h) =>
        $"scale={w}:{h}:force_original_aspect_ratio=decrease:flags=bicubic," +
        $"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1";
}

public enum ExportResolution { P1080, P1440, P2160 }

public static class ExportResolutions
{
    /// <summary>Dropdown order matches the enum, so an index maps straight to a value.</summary>
    public static readonly IReadOnlyList<(ExportResolution Value, string Label)> All = new[]
    {
        (ExportResolution.P1080, "1080p (1920×1080)"),
        (ExportResolution.P1440, "1440p (2560×1440): better YouTube quality"),
        (ExportResolution.P2160, "4K (3840×2160)"),
    };
}

/// <summary>Settings for YouTube-friendly export. Codec choices are fixed on purpose.</summary>
public sealed record ExportSettings
{
    public ExportResolution Resolution { get; init; } = ExportResolution.P1080;

    /// <summary>0 = use the first clip's frame rate.</summary>
    public double FrameRate { get; init; }

    /// <summary>x264 quality. 18 is visually lossless-ish; YouTube re-encodes anyway.</summary>
    public int Crf { get; init; } = 18;

    public string Preset { get; init; } = "medium";

    public (int Width, int Height) Size => Resolution switch
    {
        ExportResolution.P1440 => (2560, 1440),
        ExportResolution.P2160 => (3840, 2160),
        _ => (1920, 1080),
    };
}
