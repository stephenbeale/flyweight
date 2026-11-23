using Flyweight.Core.Model;

namespace Flyweight.Core.Editing;

/// <summary>Quick placements offered in the overlay's properties and right-click menu.</summary>
public enum OverlayPosition { BottomRight, BottomLeft, TopRight, TopLeft, Centre, FullFrame }

/// <summary>
/// Where an overlay sits on the 16:9 canvas. One set of maths for the preview, the export and "Save frame", so they
/// all put it in the same place (the overlay equivalent of rule 5).
/// </summary>
public static class OverlayLayout
{
    public const double MinScale = 0.1, MaxScale = 1.0;

    /// <summary>Gap from the canvas edge for the corner placements, as a fraction of the canvas width.</summary>
    public const double Margin = 0.03;

    /// <summary>The source's shape (width ÷ height); 16:9 if unknown.</summary>
    public static double Aspect(SourceMedia? m) => m is { Width: > 0, Height: > 0 } ? m.Width / (double)m.Height : 16.0 / 9;

    /// <summary>Top-left corner and size, as fractions of the canvas (they can run past 0–1 if it hangs off an edge).</summary>
    public static (double X, double Y, double W, double H) Rect(OverlayClip o, SourceMedia? m)
    {
        double w = o.Scale;
        double h = o.Scale * (16.0 / 9) / Aspect(m);
        return (o.X - w / 2, o.Y - h / 2, w, h);
    }

    /// <summary>
    /// The same rectangle in output pixels for ffmpeg: whole, even sizes (yuv420p needs even), at least 2×2.
    /// </summary>
    public static (int X, int Y, int W, int H) PixelRect(OverlayClip o, SourceMedia? m, int canvasW, int canvasH)
    {
        var (x, y, w, h) = Rect(o, m);
        int pw = Math.Max(2, (int)Math.Round(w * canvasW / 2) * 2);
        int ph = Math.Max(2, (int)Math.Round(h * canvasH / 2) * 2);
        // From the centre, so rounding the size doesn't shift it.
        int px = (int)Math.Round(o.X * canvasW - pw / 2.0);
        int py = (int)Math.Round(o.Y * canvasH - ph / 2.0);
        return (px, py, pw, ph);
    }

    public const double MaxZoom = 4.0;

    public static bool IsZoomed(OverlayClip o) => o.Zoom > 1.0005;

    /// <summary>
    /// The part of the SOURCE picture that fills the overlay's box, as fractions of the source (0–1): the same shape as
    /// the source, 1/zoom of its size, placed by the pan. Preview, export and Save frame all crop exactly this.
    /// </summary>
    public static (double X, double Y, double W, double H) CropRect(OverlayClip o)
    {
        double z = double.IsFinite(o.Zoom) ? Math.Clamp(o.Zoom, 1, MaxZoom) : 1;
        double s = 1 / z;
        double px = double.IsFinite(o.PanX) ? Math.Clamp(o.PanX, 0, 1) : 0.5;
        double py = double.IsFinite(o.PanY) ? Math.Clamp(o.PanY, 0, 1) : 0.5;
        return (px * (1 - s), py * (1 - s), s, s);
    }

    /// <summary>ffmpeg crop of the source for a zoomed overlay (empty when not zoomed). Runs before the scale, so it stays sharp.</summary>
    public static string CropFilter(OverlayClip o)
    {
        if (!IsZoomed(o)) return "";
        var (x, y, w, h) = CropRect(o);
        static string N(double v) => FfmpegNum(v);
        // Whole, even pixel sizes: yuv420p sources can't be cropped to odd sizes or offsets.
        return $"crop=w='2*trunc(iw*{N(w)}/2)':h='2*trunc(ih*{N(h)}/2)':x='2*trunc(iw*{N(x)}/2)':y='2*trunc(ih*{N(y)}/2)',";
    }

    static string FfmpegNum(double v) => Media.FfmpegRunner.Num(Math.Round(v, 6));

    /// <summary>Centre and scale for a quick placement. Corners keep the current size; Full frame fills the width.</summary>
    public static (double X, double Y, double Scale) Place(OverlayPosition pos, double scale, SourceMedia? m)
    {
        if (pos == OverlayPosition.FullFrame) return (0.5, 0.5, 1.0);
        double w = scale, h = scale * (16.0 / 9) / Aspect(m);
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
