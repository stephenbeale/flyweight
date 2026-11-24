using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.Core.Editing;

/// <summary>Which edge a crop setting cuts.</summary>
public enum CropSide { Left, Top, Right, Bottom }

/// <summary>Quick placements offered in the overlay's properties and right-click menu.</summary>
public enum OverlayPosition { BottomRight, BottomLeft, TopRight, TopLeft, Centre, FullFrame }

/// <summary>
/// Where an overlay sits on the 16:9 canvas, and which part of its source fills it. One set of maths for the preview,
/// the export and "Save frame", so they all agree (the overlay equivalent of rule 5).
///
///   crop   cuts edges off the source: the BOX shrinks with it (the picture keeps its size), so trimming a webcam's
///          sides gives a narrower box. <see cref="OverlayClip.Scale"/> is the width the UNCROPPED picture would have.
///   zoom   then magnifies inside what's left: the box stays put, less of the picture fills it, placed by the pan.
/// </summary>
public static class OverlayLayout
{
    /// <summary>Size limits. Above 100% is only useful for filling the frame with a cropped picture.</summary>
    public const double MinScale = 0.1, MaxScale = 4.0;

    /// <summary>Gap from the canvas edge for the corner placements, as a fraction of the canvas width.</summary>
    public const double Margin = 0.03;

    public const double MaxZoom = 4.0;

    /// <summary>The source's shape (width ÷ height); 16:9 if unknown.</summary>
    public static double Aspect(SourceMedia? m) => m is { Width: > 0, Height: > 0 } ? m.Width / (double)m.Height : 16.0 / 9;

    public static bool IsZoomed(OverlayClip o) => o.Zoom > 1.0005;

    public static bool IsCropped(OverlayClip o) => ClipLook.IsCropped(o.CropLeft, o.CropTop, o.CropRight, o.CropBottom);

    /// <summary>Box size (fractions of the canvas) for a picture of shape <paramref name="aspect"/> at <paramref name="scale"/>, after cropping.</summary>
    static (double W, double H) BoxSize(double scale, double aspect, OverlayClip crop)
    {
        var (_, _, cw, ch) = ClipLook.CropBox(crop.CropLeft, crop.CropTop, crop.CropRight, crop.CropBottom);
        return (scale * cw, scale * (16.0 / 9) / aspect * ch);
    }

    /// <summary>Top-left corner and size of the box, as fractions of the canvas (they can run past 0–1 if it hangs off an edge).</summary>
    public static (double X, double Y, double W, double H) Rect(OverlayClip o, SourceMedia? m)
    {
        var (w, h) = BoxSize(o.Scale, Aspect(m), o);
        return (o.X - w / 2, o.Y - h / 2, w, h);
    }

    /// <summary>
    /// The same rectangle in output pixels for ffmpeg: whole, even sizes (yuv420p needs even), at least 2×2.
    /// </summary>
    public static (int X, int Y, int W, int H) PixelRect(OverlayClip o, SourceMedia? m, int canvasW, int canvasH)
    {
        var (_, _, w, h) = Rect(o, m);
        int pw = Math.Max(2, (int)Math.Round(w * canvasW / 2) * 2);
        int ph = Math.Max(2, (int)Math.Round(h * canvasH / 2) * 2);
        // From the centre, so rounding the size doesn't shift it.
        int px = (int)Math.Round(o.X * canvasW - pw / 2.0);
        int py = (int)Math.Round(o.Y * canvasH - ph / 2.0);
        return (px, py, pw, ph);
    }

    /// <summary>
    /// The part of the SOURCE picture that fills the box, as fractions of the source (0–1): the crop, then 1/zoom of
    /// that, placed by the pan. Preview, export and Save frame all show exactly this.
    /// </summary>
    public static (double X, double Y, double W, double H) CropRect(OverlayClip o)
    {
        var (cx, cy, cw, ch) = ClipLook.CropBox(o.CropLeft, o.CropTop, o.CropRight, o.CropBottom);
        double z = double.IsFinite(o.Zoom) ? Math.Clamp(o.Zoom, 1, MaxZoom) : 1;
        double s = 1 / z;
        double px = double.IsFinite(o.PanX) ? Math.Clamp(o.PanX, 0, 1) : 0.5;
        double py = double.IsFinite(o.PanY) ? Math.Clamp(o.PanY, 0, 1) : 0.5;
        return (cx + px * (1 - s) * cw, cy + py * (1 - s) * ch, s * cw, s * ch);
    }

    /// <summary>ffmpeg crop of the source (empty when it shows whole). Runs before the scale, so it stays sharp.</summary>
    public static string CropFilter(OverlayClip o)
    {
        var (x, y, w, h) = CropRect(o);
        return ClipLook.CropFilter(x, y, w, h);
    }

    /// <summary>
    /// Centre and scale for a quick placement. Corners keep the current size; Full frame makes the (cropped) picture
    /// as big as fits the frame.
    /// </summary>
    public static (double X, double Y, double Scale) Place(OverlayPosition pos, double scale, SourceMedia? m, OverlayClip crop)
    {
        double a = Aspect(m);
        if (pos == OverlayPosition.FullFrame)
        {
            var (_, _, cw, ch) = ClipLook.CropBox(crop.CropLeft, crop.CropTop, crop.CropRight, crop.CropBottom);
            double fill = Math.Min(1 / cw, a * 9 / 16 / ch); // widest that still fits both ways
            return (0.5, 0.5, Math.Clamp(Math.Round(fill, 6), MinScale, MaxScale));
        }
        var (w, h) = BoxSize(scale, a, crop);
        double mx = Margin, my = Margin * 16.0 / 9; // the same gap in pixels on both edges
        double left = mx + w / 2, right = 1 - mx - w / 2, top = my + h / 2, bottom = 1 - my - h / 2;
        return pos switch
        {
            OverlayPosition.BottomLeft => (left, bottom, scale),
            OverlayPosition.TopRight => (right, top, scale),
            OverlayPosition.TopLeft => (left, top, scale),
            OverlayPosition.Centre => (0.5, 0.5, scale),
            _ => (right, bottom, scale),
        };
    }
}
