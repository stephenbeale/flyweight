using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Flyweight.Core.Model;

namespace Flyweight.App.Rendering;

/// <summary>
/// THE single drawing routine for annotations. The preview calls it over the video frame;
/// export calls it into a transparent bitmap at output resolution. Same code path = the
/// exported video matches what you saw on screen.
///
/// All sizes are relative to canvas height (stroke/font sizes are "pixels at 1080p"),
/// so a 540-pixel preview and a 4K export look identical, just sharper.
/// </summary>
public static class AnnotationRenderer
{
    static readonly Bitmap MeasureBitmap = new(1, 1);
    static readonly Graphics MeasureGraphics = Graphics.FromImage(MeasureBitmap);

    public static float Scale(RectangleF canvas) => Math.Min(canvas.Width, canvas.Height) / 1080f; // short side: vertical 1080x1920 is "1080p" too (D41)

    public static PointF ToCanvas(RectangleF canvas, float nx, float ny) =>
        new(canvas.X + nx * canvas.Width, canvas.Y + ny * canvas.Height);

    /// <summary>
    /// Draw at <paramref name="opacity"/> (0–1, from <c>TimelineOps.AnnotationOpacity</c>) for fades: the annotation is
    /// drawn fully opaque into a layer, then the layer is blended at that opacity, so overlapping parts (outline under
    /// stroke, text over its background) fade together instead of showing through each other.
    /// </summary>
    public static void Draw(Graphics g, Annotation a, RectangleF canvas, float opacity)
    {
        if (opacity >= 0.999f) { Draw(g, a, canvas); return; }
        if (opacity <= 0.001f) return;
        var bounds = Rectangle.Round(canvas);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        using var layer = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var lg = Graphics.FromImage(layer))
        {
            lg.Clear(Color.Transparent);
            Draw(lg, a, new RectangleF(canvas.X - bounds.X, canvas.Y - bounds.Y, canvas.Width, canvas.Height));
        }
        using var attrs = new System.Drawing.Imaging.ImageAttributes();
        attrs.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = opacity });
        g.DrawImage(layer, bounds, 0, 0, layer.Width, layer.Height, GraphicsUnit.Pixel, attrs);
    }

    public static void Draw(Graphics g, Annotation a, RectangleF canvas)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAlias; // not ClearType: it fringes on transparent bitmaps

        var stroke = ColorHex.Parse(a.StrokeColor, Color.Red);
        float scale = Scale(canvas);
        float w = Math.Max(1f, a.StrokeWidth * scale);
        var p1 = ToCanvas(canvas, a.X1, a.Y1);
        var p2 = ToCanvas(canvas, a.X2, a.Y2);

        switch (a.Kind)
        {
            case AnnotationKind.Arrow:
                DrawArrow(g, p1, p2, stroke, w, scale);
                break;
            case AnnotationKind.Box:
                DrawBox(g, a, RectFrom(p1, p2), stroke, w, scale);
                break;
            case AnnotationKind.Text:
                DrawText(g, a, canvas, stroke);
                break;
        }
    }

    // A thin dark outline under arrows and boxes keeps them readable on bright footage.
    static Color OutlineColor(Color c) => Color.FromArgb(c.A * 110 / 255, 0, 0, 0);

    static void DrawArrow(Graphics g, PointF tail, PointF tip, Color color, float w, float scale)
    {
        float dx = tip.X - tail.X, dy = tip.Y - tail.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1) return;
        float ux = dx / len, uy = dy / len;

        float headLen = Math.Min(len, w * 3.2f + 10 * scale);
        float headHalf = headLen * 0.6f;
        var baseCentre = new PointF(tip.X - ux * headLen, tip.Y - uy * headLen);
        var head = new[]
        {
            tip,
            new PointF(baseCentre.X - uy * headHalf, baseCentre.Y + ux * headHalf),
            new PointF(baseCentre.X + uy * headHalf, baseCentre.Y - ux * headHalf),
        };
        // Stop the shaft slightly inside the head so the round cap doesn't poke out of the tip.
        var shaftEnd = new PointF(baseCentre.X + ux * headLen * 0.3f, baseCentre.Y + uy * headLen * 0.3f);

        float outline = Math.Max(1f, 2.5f * scale);
        using (var pen = new Pen(OutlineColor(color), w + outline * 2) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            g.DrawLine(pen, tail, shaftEnd);
            g.DrawPolygon(pen, head);
        }
        using (var pen = new Pen(color, w) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(pen, tail, shaftEnd);
        using (var brush = new SolidBrush(color))
            g.FillPolygon(brush, head);
    }

    static void DrawBox(Graphics g, Annotation a, RectangleF r, Color color, float w, float scale)
    {
        if (r.Width < 1 || r.Height < 1) return;
        if (a.FillColor is { } fill)
        {
            using var b = new SolidBrush(ColorHex.Parse(fill, Color.Transparent));
            g.FillRectangle(b, r);
        }
        float outline = Math.Max(1f, 2.5f * scale);
        using (var pen = new Pen(OutlineColor(color), w + outline * 2) { LineJoin = LineJoin.Round })
            g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
        using (var pen = new Pen(color, w) { LineJoin = LineJoin.Round })
            g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
    }

    static void DrawText(Graphics g, Annotation a, RectangleF canvas, Color color)
    {
        var text = string.IsNullOrEmpty(a.Text) ? " " : a.Text;
        using var font = CreateFont(a, canvas);
        var bounds = TextBounds(a, canvas);

        if (a.FillColor is { } fill)
        {
            using var b = new SolidBrush(ColorHex.Parse(fill, Color.Transparent));
            using var path = RoundedRect(bounds, font.Size * 0.3f);
            g.FillPath(b, path);
        }
        float pad = Padding(font);
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, bounds.X + pad, bounds.Y + pad * 0.6f, StringFormat.GenericTypographic);
    }

    /// <summary>Rectangle a text annotation occupies (used for drawing, hit-testing and the inline editor).</summary>
    public static RectangleF TextBounds(Annotation a, RectangleF canvas)
    {
        var text = string.IsNullOrEmpty(a.Text) ? " " : a.Text;
        using var font = CreateFont(a, canvas);
        var size = MeasureGraphics.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
        float pad = Padding(font);
        var origin = ToCanvas(canvas, a.X1, a.Y1);
        return new RectangleF(origin.X, origin.Y, size.Width + pad * 2, size.Height + pad * 1.2f);
    }

    public static Font CreateFont(Annotation a, RectangleF canvas) =>
        new(a.FontFamily, Math.Max(4f, a.FontSize * Scale(canvas)), FontStyle.Bold, GraphicsUnit.Pixel);

    static float Padding(Font f) => f.Size * 0.35f;

    public static RectangleF RectFrom(PointF a, PointF b) =>
        RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d < 1) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Does <paramref name="pt"/> (control pixels) hit this annotation?</summary>
    public static bool HitTest(Annotation a, RectangleF canvas, PointF pt)
    {
        float scale = Scale(canvas);
        float tol = Math.Max(6f, a.StrokeWidth * scale / 2 + 5);
        var p1 = ToCanvas(canvas, a.X1, a.Y1);
        var p2 = ToCanvas(canvas, a.X2, a.Y2);
        switch (a.Kind)
        {
            case AnnotationKind.Arrow:
                return DistanceToSegment(pt, p1, p2) <= tol;
            case AnnotationKind.Box:
                var r = RectFrom(p1, p2);
                if (a.FillColor is not null) return RectangleF.Inflate(r, tol, tol).Contains(pt);
                var outer = RectangleF.Inflate(r, tol, tol);
                var inner = RectangleF.Inflate(r, -tol, -tol);
                return outer.Contains(pt) && !(inner.Width > 0 && inner.Height > 0 && inner.Contains(pt));
            default:
                return TextBounds(a, canvas).Contains(pt);
        }
    }

    static float DistanceToSegment(PointF p, PointF a, PointF b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float len2 = dx * dx + dy * dy;
        float t = len2 == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        float cx = a.X + t * dx - p.X, cy = a.Y + t * dy - p.Y;
        return MathF.Sqrt(cx * cx + cy * cy);
    }
}

public static class ColorHex
{
    /// <summary>Parse #RRGGBB or #AARRGGBB.</summary>
    public static Color Parse(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var s = hex.TrimStart('#');
        if (!uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v)) return fallback;
        return s.Length switch
        {
            6 => Color.FromArgb(255, (int)(v >> 16) & 255, (int)(v >> 8) & 255, (int)v & 255),
            8 => Color.FromArgb((int)(v >> 24) & 255, (int)(v >> 16) & 255, (int)(v >> 8) & 255, (int)v & 255),
            _ => fallback,
        };
    }

    public static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
}
