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

    public static bool IsDefault(Clip c) => !IsZoomed(c) && !HasTone(c) && !IsCropped(c);

    // ---------------- Crop: cut edges off the source (changes its shape) ----------------

    /// <summary>Most that can be cut from one side, as a fraction of the source: at least 10% always remains each way.</summary>
    public const double MaxCropEach = 0.45;

    public static double CleanCrop(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, MaxCropEach) : 0;

    public static bool IsCropped(double l, double t, double r, double b) =>
        CleanCrop(l) + CleanCrop(t) + CleanCrop(r) + CleanCrop(b) > 0.0005;

    public static bool IsCropped(Clip c) => IsCropped(c.CropLeft, c.CropTop, c.CropRight, c.CropBottom);

    /// <summary>The kept part of the source, as fractions of it (0–1): left, top, width, height.</summary>
    public static (double X, double Y, double W, double H) CropBox(double l, double t, double r, double b)
    {
        double cl = CleanCrop(l), ct = CleanCrop(t), cr = CleanCrop(r), cb = CleanCrop(b);
        return (cl, ct, 1 - cl - cr, 1 - ct - cb);
    }

    /// <summary>Shape (width ÷ height) of a source of shape <paramref name="aspect"/> after cropping.</summary>
    public static double CroppedAspect(double aspect, double l, double t, double r, double b)
    {
        var (_, _, w, h) = CropBox(l, t, r, b);
        return aspect * w / h;
    }

    /// <summary>
    /// ffmpeg crop of the ORIGINAL to the source-relative rectangle (x, y, w, h), ending with a comma; "" for the whole
    /// frame. Whole, even pixel sizes and offsets: yuv420p sources can't be cropped to odd ones.
    /// </summary>
    public static string CropFilter(double x, double y, double w, double h)
    {
        if (x < 0.0005 && y < 0.0005 && w > 0.9995 && h > 0.9995) return "";
        static string N(double v) => Num(Math.Round(v, 6));
        return $"crop=w='2*trunc(iw*{N(w)}/2)':h='2*trunc(ih*{N(h)}/2)':x='2*trunc(iw*{N(x)}/2)':y='2*trunc(ih*{N(y)}/2)',";
    }

    /// <summary>
    /// A main clip's crop: the cut edges are blacked out IN PLACE on the original (nothing moves or rescales), before
    /// it is fitted to 16:9 and zoomed. Ends with a comma; "" when uncropped. The preview does the same
    /// (PreviewCanvas.Masked).
    /// </summary>
    public static string MaskFilter(Clip c)
    {
        if (!IsCropped(c)) return "";
        double l = CleanCrop(c.CropLeft), t = CleanCrop(c.CropTop), r = CleanCrop(c.CropRight), b = CleanCrop(c.CropBottom);
        static string N(double v) => Num(Math.Round(v, 6));
        var s = "";
        // +1 px on the far edges so rounding never leaves a hairline of picture.
        if (l > 0) s += $"drawbox=x=0:y=0:w=iw*{N(l)}:h=ih:color=black:t=fill,";
        if (r > 0) s += $"drawbox=x=iw*{N(1 - r)}:y=0:w=iw*{N(r)}+1:h=ih:color=black:t=fill,";
        if (t > 0) s += $"drawbox=x=0:y=0:w=iw:h=ih*{N(t)}:color=black:t=fill,";
        if (b > 0) s += $"drawbox=x=0:y=ih*{N(1 - b)}:w=iw:h=ih*{N(b)}+1:color=black:t=fill,";
        return s;
    }

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

    // ---------------- Blurred bars (D40) ----------------
    //
    // Both the preview and the export build the background the same cheap way: the picture scaled to FILL a tiny
    // 64×36 frame (centre-cropped), blurred there (a Gaussian of sigma 2, which two 5-wide box passes match),
    // darkened so the real picture stands out, then scaled up to full size. Blurring 2,304 pixels instead of
    // 8 million keeps a 4K export fast and the preview smooth. The sharp picture is then fitted on top, exactly
    // where the black-bar fit would put it, so annotations land in the same place either way.

    public const int BlurWidth = 64;
    public const int BlurHeight = 36;

    /// <summary>How bright the blurred background is kept (0.6 = 40% darker), so the real picture stands out.</summary>
    public const double BlurDim = 0.6;

    /// <summary>
    /// True when this clip's bars are filled with blur: the project asks for it, the clip isn't zoomed or cropped
    /// (those show their own framing), and its picture isn't already 16:9 (no bars to fill).
    /// </summary>
    public static bool BlursBars(Project p, Clip c, SourceMedia? m) =>
        p.Background == BackgroundFill.Blur && m is { IsAudioOnly: false, Width: > 0, Height: > 0 } &&
        !IsZoomed(c) && !IsCropped(c) && Math.Abs(m.Width / (double)m.Height - 16.0 / 9) > 0.01;

    /// <summary>
    /// Replaces <see cref="Canvas.FitFilter"/> when <see cref="BlursBars"/>: the blurred fill behind the fitted
    /// picture. It splits the stream, so it needs labels unique within the graph: <paramref name="id"/> prefixes them.
    /// Like FitFilter it takes one input and leaves one W×H output, so it drops into a linear chain.
    /// </summary>
    public static string BlurFitFilter(int w, int h, string id) =>
        $"split=2[{id}b][{id}f];" +
        $"[{id}b]scale={BlurWidth}:{BlurHeight}:force_original_aspect_ratio=increase:flags=area,crop={BlurWidth}:{BlurHeight}," +
        $"gblur=sigma=2,colorchannelmixer=rr={Num(BlurDim)}:gg={Num(BlurDim)}:bb={Num(BlurDim)}," +
        $"scale={w}:{h}:flags=bicubic,setsar=1[{id}bg];" +
        $"[{id}f]scale={w}:{h}:force_original_aspect_ratio=decrease:flags=bicubic,setsar=1[{id}fg];" +
        $"[{id}bg][{id}fg]overlay=x=(W-w)/2:y=(H-h)/2,setsar=1";

    /// <summary>
    /// The preview's half of the blur, on the tiny frame (32-bit BGRA/BGRX rows, <paramref name="stride"/> bytes each):
    /// two 5-wide box passes each way (≈ the export's sigma-2 Gaussian; edges repeat) and the <see cref="BlurDim"/>.
    /// </summary>
    public static void BlurSmall(byte[] px, int width, int height, int stride)
    {
        var tmp = new byte[px.Length];
        for (int pass = 0; pass < 2; pass++)
        {
            BoxPass(px, tmp, width, height, stride, horizontal: true);
            BoxPass(tmp, px, width, height, stride, horizontal: false);
        }
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * stride + x * 4;
                for (int ch = 0; ch < 3; ch++) px[i + ch] = (byte)Math.Round(px[i + ch] * BlurDim);
                px[i + 3] = 255;
            }
    }

    static void BoxPass(byte[] src, byte[] dst, int width, int height, int stride, bool horizontal)
    {
        const int r = 2;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                for (int ch = 0; ch < 3; ch++)
                {
                    int sum = 0;
                    for (int k = -r; k <= r; k++)
                    {
                        int sx = horizontal ? Math.Clamp(x + k, 0, width - 1) : x;
                        int sy = horizontal ? y : Math.Clamp(y + k, 0, height - 1);
                        sum += src[sy * stride + sx * 4 + ch];
                    }
                    dst[y * stride + x * 4 + ch] = (byte)((sum + r) / (2 * r + 1));
                }
    }

    static double Clamp1(double v) => double.IsFinite(v) ? Math.Clamp(v, -1, 1) : 0;
}
