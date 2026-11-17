using System.Drawing.Imaging;
using Flyweight.Core.Editing;
using Flyweight.Core.Model;

namespace Flyweight.App.Rendering;

/// <summary>Renders each visible annotation to a transparent full-frame PNG for ffmpeg to overlay.</summary>
public static class OverlayRenderer
{
    public static Dictionary<Guid, string> RenderAll(Project project, int width, int height, string directory)
    {
        Directory.CreateDirectory(directory);
        var result = new Dictionary<Guid, string>();
        var canvas = new RectangleF(0, 0, width, height);

        foreach (var a in project.Annotations)
        {
            if (TimelineOps.AnnotationRange(project, a) is null) continue; // trimmed out of view

            using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                AnnotationRenderer.Draw(g, a, canvas);
            }
            var path = Path.Combine(directory, $"{a.Id:N}.png");
            bmp.Save(path, ImageFormat.Png);
            result[a.Id] = path;
        }
        return result;
    }
}
