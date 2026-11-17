using System.Text.Json.Serialization;

namespace Flyweight.Core.Model;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnnotationKind { Arrow, Box, Text }

/// <summary>
/// A drawn overlay.
///
/// Two deliberate design choices:
///  * Times are in SOURCE time of the anchoring clip, so trimming or reordering clips keeps
///    the arrow on the footage it points at. Visibility is clamped to the clip's In/Out.
///  * Coordinates are normalised 0..1 on the 16:9 output canvas, so they are resolution
///    independent (540p preview and 4K export put them in the same place).
/// </summary>
public sealed class Annotation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClipId { get; set; }
    public AnnotationKind Kind { get; set; }

    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }

    /// <summary>Arrow: tail. Box: one corner. Text: top-left anchor.</summary>
    public float X1 { get; set; }
    public float Y1 { get; set; }

    /// <summary>Arrow: head. Box: opposite corner. Text: unused (size comes from the text).</summary>
    public float X2 { get; set; }
    public float Y2 { get; set; }

    public string Text { get; set; } = "";

    /// <summary>#RRGGBB or #AARRGGBB.</summary>
    public string StrokeColor { get; set; } = "#FFE53935";

    /// <summary>Box fill / text background. Null = none.</summary>
    public string? FillColor { get; set; }

    /// <summary>Line thickness in pixels at 1080p; scaled for other sizes.</summary>
    public float StrokeWidth { get; set; } = 8f;

    /// <summary>Font size in pixels at 1080p; scaled for other sizes.</summary>
    public float FontSize { get; set; } = 56f;

    public string FontFamily { get; set; } = "Segoe UI";

    public Annotation Clone() => (Annotation)MemberwiseClone();
}
