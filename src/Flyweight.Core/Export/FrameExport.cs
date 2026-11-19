using Flyweight.Core.Editing;
using Flyweight.Core.Media;
using Flyweight.Core.Model;
using static Flyweight.Core.Media.FfmpegRunner;

namespace Flyweight.Core.Export;

/// <summary>
/// Grab the frame under the playhead as a full-quality PNG. It is read from the ORIGINAL file (not the
/// 540p proxy) and fitted to the output canvas, so the image matches what the exported video would show
/// at that moment. The UI draws the annotations on top afterwards with the preview's renderer.
/// </summary>
public static class FrameExport
{
    /// <summary>ffmpeg arguments that write the bare frame (no annotations) at timeline time <paramref name="t"/>.</summary>
    public static IReadOnlyList<string> BuildArgs(Project p, TimeSpan t, int width, int height, string outputPng)
    {
        var hit = LocateFrame(p, t) ?? throw new InvalidOperationException("There is no clip under the playhead.");
        var media = p.FindMedia(hit.Clip.SourceId) ?? throw new InvalidOperationException("The clip's media is missing.");

        var args = new List<string>();
        if (!media.IsStill) args.AddRange(new[] { "-ss", Sec(hit.SourceTime) }); // input seek: fast, and exact when decoding
        args.AddRange(new[]
        {
            "-i", media.OriginalPath,
            "-frames:v", "1",
            "-vf", Canvas.FitFilter(width, height),
            "-f", "image2", "-update", "1",
            outputPng,
        });
        return args;
    }

    public static Task ExtractAsync(Project p, TimeSpan t, int width, int height, string outputPng, CancellationToken ct = default) =>
        RunFfmpegAsync(BuildArgs(p, t, width, height, outputPng), ct: ct);

    /// <summary>The clip/time to grab; at (or past) the very end, use the last frame rather than nothing.</summary>
    public static TimelineHit? LocateFrame(Project p, TimeSpan t)
    {
        var total = TimelineOps.TotalDuration(p);
        if (total <= TimeSpan.Zero) return null;
        if (t >= total) t = total - TimeSpan.FromMilliseconds(40);
        return TimelineOps.Locate(p, t < TimeSpan.Zero ? TimeSpan.Zero : t);
    }

    /// <summary>"My video frame 1m23s.png" style name for the frame at timeline time <paramref name="t"/>.</summary>
    public static string DefaultFileName(Project p, string? projectFilePath, TimeSpan t) =>
        $"{ExportLocations.BaseName(p, projectFilePath)} frame {Stamp(t)}.png";

    /// <summary>1m23s / 1h02m03s.</summary>
    public static string Stamp(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}m{t.Seconds:00}s" : $"{(int)t.TotalMinutes}m{t.Seconds:00}s";
}

/// <summary>Where exports go by default: next to the project's main footage, not in some random folder.</summary>
public static class ExportLocations
{
    /// <summary>
    /// Folder of the LARGEST video file in the project. That is almost always the main recording,
    /// so the export lands next to the footage it came from. Falls back to Videos.
    /// </summary>
    public static string DefaultFolder(Project p)
    {
        var largest = p.Media
            .Where(m => !m.IsStill)
            .Select(m => new FileInfo(m.OriginalPath))
            .Where(f => f.Exists)
            .OrderByDescending(f => f.Length)
            .FirstOrDefault();
        return largest?.DirectoryName ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    }

    /// <summary>Name to base exports on: the project file's name, else the largest video's name.</summary>
    public static string BaseName(Project p, string? projectFilePath)
    {
        if (!string.IsNullOrEmpty(projectFilePath)) return Path.GetFileNameWithoutExtension(projectFilePath);
        var largest = p.Media
            .Where(m => !m.IsStill && File.Exists(m.OriginalPath))
            .OrderByDescending(m => new FileInfo(m.OriginalPath).Length)
            .FirstOrDefault();
        return largest is null ? "My video" : Path.GetFileNameWithoutExtension(largest.OriginalPath);
    }

    /// <summary>Default path for the video export, e.g. "D:\Footage\Holiday - edit.mp4".</summary>
    public static string DefaultVideoPath(Project p, string? projectFilePath)
    {
        var name = string.IsNullOrEmpty(projectFilePath) ? BaseName(p, null) + " - edit" : BaseName(p, projectFilePath);
        return Path.Combine(DefaultFolder(p), name + ".mp4");
    }

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
