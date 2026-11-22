using System.Drawing.Imaging;
using Flyweight.Core.Editing;
using Flyweight.Core.Export;
using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.App.Rendering;

/// <summary>
/// "Export frame as image" (D33): ffmpeg pulls the exact frame from the ORIGINAL file at output size (with the clip's
/// zoom and brightness, as the export does), then the annotations visible at that moment are drawn on with the same
/// renderer as the preview and the video export, so the PNG matches both.
/// </summary>
public static class FrameRenderer
{
    public static async Task ExportAsync(Project project, TimeSpan timelineTime, int width, int height, string outputPng, CancellationToken ct = default)
    {
        if (FrameExport.LocateFrame(project, timelineTime) is not { } hit)
            throw new InvalidOperationException("There is no clip under the playhead.");
        var t = FrameExport.TimelineTimeOf(hit); // same clamping as the grab, so the annotations match the frame we got

        var temp = Path.Combine(Path.GetTempPath(), $"flyweight-frame-{Guid.NewGuid():N}.png");
        try
        {
            await FfmpegRunner.RunFfmpegAsync(FrameExport.BuildArgs(project, hit, width, height, temp), ct: ct);

            using var bmp = LoadUnlocked(temp, width, height);
            using (var g = Graphics.FromImage(bmp))
            {
                var canvas = new RectangleF(0, 0, width, height);
                foreach (var a in TimelineOps.VisibleAt(project, t))
                    AnnotationRenderer.Draw(g, a, canvas);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPng))!);
            bmp.Save(outputPng, ImageFormat.Png);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    /// <summary>Copy into a fresh 32-bit bitmap: drawable, and GDI+ doesn't keep the temp file locked.</summary>
    static Bitmap LoadUnlocked(string path, int width, int height)
    {
        using var fs = File.OpenRead(path);
        using var src = Image.FromStream(fs);
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.DrawImage(src, 0, 0, width, height);
        return bmp;
    }
}
