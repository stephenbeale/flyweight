using Flyweight.Core.Model;
using static Flyweight.Core.Media.FfmpegRunner;

namespace Flyweight.Core.Media;

/// <summary>
/// The maths behind a clip's zoom and brightness/contrast, shared by the preview and the export so
/// they match (the same idea as the one annotation renderer, rule 5):
///
///   Zoom works on the fitted 16:9 canvas (the same frame the proxy shows): the visible part is
///   <see cref="ViewRect"/>, in normalised canvas coordinates. The preview draws that part of the
///   proxy; the export pads the ORIGINAL to 16:9 at its own resolution, crops the same rectangle and
///   only then scales to the output size, so a 4K clip zoomed 2× still exports sharp at 1080p.
///
///   Brightness/contrast is a per-channel RGB curve, <see cref="Tone"/>. The preview applies it as a
///   256-entry table; the export uses ffmpeg's lutrgb with the SAME formula and the SAME rounded
///   constants, so both produce identical values.
///
/// Annotations sit on top of the zoomed picture and are not zoomed themselves.
/// </summary>
public static class ClipLook
{
    public const double MaxZoom = 4.0;

    public static bool IsZoomed(Clip c) => c.Zoom > 1.0005;

    public static bool HasTone(Clip c) => Gamma(c.Brightness) != 1 || ContrastGain(c.Contrast) != 1;

    public static bool IsDefault(Clip c) => !IsZoomed(c) && !HasTone(c);

    /// <summary>The visible part of the canvas, normalised 0–1: always 16:9 and always inside the frame.</summary>
    public static (double X, double Y, double W, double H) ViewRect(Clip c)
    {
        double z = double.IsFinite(c.Zoom) ? Math.Clamp(c.Zoom, 1, MaxZoom) : 1;
        double size = 1 / z;
        double px = double.IsFinite(c.PanX) ? Math.Clamp(c.PanX, 0, 1) : 0.5;
        double py = double.IsFinite(c.PanY) ? Math.Clamp(c.PanY, 0, 1) : 0.5;
        return (px * (1 - size), py * (1 - size), size, size);
    }

    /// <summary>
    /// Brightness → gamma exponent: +1 → 0.35 (much brighter shadows), -1 → 2.83. Rounded to 4 places
    /// because the export writes it into the ffmpeg command as text.
    /// </summary>
    public static double Gamma(double brightness) =>
        Math.Round(Math.Pow(2, -1.5 * Clamp1(brightness)), 4);

    /// <summary>Contrast → gain around mid-grey: +1 → 2×, -1 → 0.5×. Rounded like <see cref="Gamma"/>.</summary>
    public static double ContrastGain(double contrast) =>
        Math.Round(Math.Pow(2, Clamp1(contrast)), 4);

    /// <summary>The curve itself: channel value 0–255 in, 0–255 out. Mirrors <see cref="ToneFilter"/>'s expression.</summary>
    public static int Tone(int v, double gamma, double gain)
    {
        double x = Math.Floor((Math.Pow(v / 255.0, gamma) - 0.5) * gain * 255 + 128);
        return (int)Math.Clamp(x, 0, 255);
    }

    /// <summary>256-entry lookup table for the preview.</summary>
    public static byte[] ToneTable(Clip c)
    {
        double g = Gamma(c.Brightness), k = ContrastGain(c.Contrast);
        var t = new byte[256];
        for (int v = 0; v < 256; v++) t[v] = (byte)Tone(v, g, k);
        return t;
    }

    /// <summary>ffmpeg filters for the export (empty when the clip is as shot). Expects frames already at W×H.</summary>
    public static string ToneFilter(Clip c)
    {
        if (!HasTone(c)) return "";
        string e = $"'clip(floor((pow(val/255,{Num(Gamma(c.Brightness))})-0.5)*{Num(ContrastGain(c.Contrast))}*255+128),0,255)'";
        return $"format=rgb24,lutrgb=r={e}:g={e}:b={e},";
    }

    /// <summary>
    /// Replaces <see cref="Canvas.FitFilter"/> for a zoomed clip: pad the source to W:H at its own
    /// resolution (the same geometry as fit-and-pad), crop <see cref="ViewRect"/>, then scale to W×H.
    /// </summary>
    public static string ZoomFilter(Clip c, int w, int h)
    {
        var (x, y, rw, rh) = ViewRect(c);
        string r = Num(w / (double)h), ir = Num(h / (double)w);
        return $"pad=w='max(iw,ih*{r})':h='max(ih,iw*{ir})':x=(ow-iw)/2:y=(oh-ih)/2:color=black," +
               $"crop=w=iw*{Num(rw)}:h=ih*{Num(rh)}:x=iw*{Num(x)}:y=ih*{Num(y)}," +
               $"scale={w}:{h}:flags=bicubic,setsar=1";
    }

    static double Clamp1(double v) => double.IsFinite(v) ? Math.Clamp(v, -1, 1) : 0;
}
