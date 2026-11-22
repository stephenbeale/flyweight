using Flyweight.Core.Editing;
using Flyweight.Core.Media;
using Flyweight.Core.Model;
using static Flyweight.Core.Media.FfmpegRunner;

namespace Flyweight.Core.Export;

/// <summary>
/// "Export frame as image" (D33): the frame under the playhead as a full-quality PNG. It is read from the ORIGINAL
/// file (not the 540p proxy) at the output size, with the clip's zoom and brightness/contrast applied by the same
/// ClipLook filters as the video export, so the image matches the exported video at that moment. The UI then draws
/// the annotations on top with the preview's renderer (rule 5).
/// </summary>
public static class FrameExport
{
    /// <summary>ffmpeg arguments that write the bare frame (no annotations) for <paramref name="hit"/>.</summary>
    public static IReadOnlyList<string> BuildArgs(Project p, TimelineHit hit, int width, int height, string outputPng)
    {
        var media = p.FindMedia(hit.Clip.SourceId) ?? throw new InvalidOperationException("The clip's media is missing.");
        var clip = hit.Clip;
        var fit = ClipLook.IsZoomed(clip) ? ClipLook.ZoomFilter(clip, width, height) : Canvas.FitFilter(width, height);

        var args = new List<string>();
        if (!media.IsStill) args.AddRange(new[] { "-ss", Sec(hit.SourceTime) }); // input seek: fast, and exact when decoding
        args.AddRange(new[]
        {
            "-i", media.OriginalPath,
            "-frames:v", "1",
            "-vf", $"{fit},{ClipLook.ToneFilter(clip)}format=rgb24",
            "-f", "image2", "-update", "1",
            outputPng,
        });
        return args;
    }

    /// <summary>The clip and source time to grab; at (or past) the very end, the last frame rather than nothing.</summary>
    public static TimelineHit? LocateFrame(Project p, TimeSpan t)
    {
        var total = TimelineOps.TotalDuration(p);
        if (total <= TimeSpan.Zero) return null;
        if (t >= total) t = total - TimeSpan.FromMilliseconds(40);
        return TimelineOps.Locate(p, t < TimeSpan.Zero ? TimeSpan.Zero : t);
    }

    /// <summary>Timeline time of the grabbed frame (after <see cref="LocateFrame"/>'s clamping), for picking annotations.</summary>
    public static TimeSpan TimelineTimeOf(TimelineHit hit) => hit.ClipStart + hit.Clip.TimelineOffset(hit.SourceTime);

    /// <summary>
    /// Folder of the LARGEST video in the project: almost always the main recording, so the image lands beside the
    /// footage it came from. Falls back to Pictures.
    /// </summary>
    public static string DefaultFolder(Project p)
    {
        var largest = p.Media
            .Where(m => !m.IsStill && !m.IsAudioOnly)
            .Select(m => new FileInfo(m.OriginalPath))
            .Where(f => f.Exists)
            .OrderByDescending(f => f.Length)
            .FirstOrDefault();
        return largest?.DirectoryName ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    }

    /// <summary>"Barbour guide frame 1m23s.png": the project's name (else the main video's) plus the timeline time.</summary>
    public static string DefaultFileName(Project p, string? projectFilePath, TimeSpan t)
    {
        string name;
        if (!string.IsNullOrEmpty(projectFilePath)) name = Path.GetFileNameWithoutExtension(projectFilePath);
        else
        {
            var largest = p.Media.Where(m => !m.IsStill && !m.IsAudioOnly && File.Exists(m.OriginalPath))
                .OrderByDescending(m => new FileInfo(m.OriginalPath).Length).FirstOrDefault();
            name = largest is null ? "My video" : Path.GetFileNameWithoutExtension(largest.OriginalPath);
        }
        return $"{name} frame {Stamp(t)}.png";
    }

    /// <summary>1m23s / 1h02m03s.</summary>
    public static string Stamp(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}m{t.Seconds:00}s" : $"{(int)t.TotalMinutes}m{t.Seconds:00}s";

    /// <summary>Never overwrite: "x.png" → "x (2).png" → "x (3).png" …</summary>
    public static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
