using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.Core.Export;

/// <summary>
/// YouTube custom thumbnails: 1280×720, JPG/PNG, at most 2 MB. A busy 1280×720 PNG can go over 2 MB, so we save a
/// JPEG and step the quality down only as far as needed to fit (most frames fit at the best quality).
/// </summary>
public static class YouTubeThumbnail
{
    public const int Width = 1280;
    public const int Height = 720;

    /// <summary>YouTube's limit for a custom thumbnail.</summary>
    public const long MaxBytes = 2L * 1024 * 1024;

    /// <summary>ffmpeg JPEG quality steps, best first (2 = best, 31 = worst).</summary>
    static readonly int[] Qualities = { 2, 3, 4, 5, 7, 9, 12, 16, 20, 25, 31 };

    /// <summary>"Holiday thumbnail 1m23s.jpg", next to the Save frame naming.</summary>
    public static string DefaultFileName(Project p, string? projectFilePath, TimeSpan t) =>
        Path.ChangeExtension(FrameExport.DefaultFileName(p, projectFilePath, t).Replace(" frame ", " thumbnail "), ".jpg");

    /// <summary>
    /// Convert a rendered 1280×720 PNG to a JPEG under <paramref name="maxBytes"/>. Returns the ffmpeg quality used.
    /// The output appears only once it fits, so a half-written or oversized file is never left behind.
    /// </summary>
    public static async Task<int> SaveJpegUnderLimitAsync(string png, string jpg, long maxBytes = MaxBytes, CancellationToken ct = default)
    {
        var tmp = jpg + ".partial.jpg";
        try
        {
            foreach (var q in Qualities)
            {
                await FfmpegRunner.RunFfmpegAsync(new[]
                {
                    "-i", png, "-frames:v", "1", "-vf", $"scale={Width}:{Height}:flags=bicubic,setsar=1",
                    "-q:v", q.ToString(), "-f", "image2", "-update", "1", tmp,
                }, ct: ct);
                if (new FileInfo(tmp).Length <= maxBytes)
                {
                    File.Move(tmp, jpg, overwrite: true);
                    return q;
                }
            }
            throw new InvalidOperationException("Couldn't get the thumbnail under YouTube's 2 MB limit.");
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}
