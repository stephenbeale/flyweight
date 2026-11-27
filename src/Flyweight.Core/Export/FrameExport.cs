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
        var clip = hit.Clip;
        var args = new List<string>();
        string baseChain; // filters from input 0 to the finished main picture (no trailing comma)
        if (clip.IsGap)
        {
            // A gap is black: a generated frame at the output size (overlays and annotations still go on top).
            args.AddRange(["-f", "lavfi", "-i", $"color=c=black:s={width}x{height}:d=1"]);
            baseChain = "setsar=1";
        }
        else
        {
            var media = p.FindMedia(clip.SourceId) ?? throw new InvalidOperationException("The clip's media is missing.");
            var fit = ClipLook.FrameFilter(p, clip, media, width, height, "bf");
            if (!media.IsStill) args.AddRange(["-ss", Sec(hit.SourceTime)]); // input seek: fast, and exact when decoding
            args.AddRange(["-i", media.OriginalPath]);
            baseChain = $"{ClipLook.MaskFilter(clip)}{fit},{ClipLook.ToneFilter(clip)}".TrimEnd(',');
        }

        // Overlay-track video showing at this moment goes on, placed as in the export (OverlayLayout).
        var t = TimelineTimeOf(hit);
        var graph = $"[0:v]{baseChain},format=yuv420p[b0]";
        int n = 0;
        foreach (var o in TimelineOps.OverlaysAt(p, t))
        {
            if (p.FindMedia(o.SourceId) is not { IsAudioOnly: false } om) continue;
            if (!om.IsStill) args.AddRange(["-ss", Sec(o.SourceAt(t - o.Start))]);
            args.AddRange(["-i", om.OriginalPath]);
            var (x, y, w, h) = OverlayLayout.PixelRect(o, om, width, height);
            graph += $";[{n + 1}:v]{OverlayLayout.CropFilter(o)}scale={w}:{h}:flags=bicubic,setsar=1[o{n}];[b{n}][o{n}]overlay={x}:{y}[b{n + 1}]";
            n++;
        }
        if (n == 0)
            args.AddRange(["-vf", $"{baseChain},format=rgb24"]);
        else
            args.AddRange(["-filter_complex", graph + $";[b{n}]format=rgb24[out]", "-map", "[out]"]);
        args.AddRange(["-frames:v", "1", "-f", "image2", "-update", "1", outputPng]);
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

    /// <summary>
    /// Default path for the video export: beside the largest video (falls back to Videos), named after the project
    /// file, else "&lt;main video&gt; - edit.mp4". Not made unique: the export dialog already asks before replacing.
    /// </summary>
    public static string DefaultVideoPath(Project p, string? projectFilePath)
    {
        var main = MainVideo(p);
        var folder = main?.DirectoryName ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var name = !string.IsNullOrEmpty(projectFilePath) ? Path.GetFileNameWithoutExtension(projectFilePath)
                 : main is not null ? Path.GetFileNameWithoutExtension(main.Name) + " - edit"
                 : "My video";
        return Path.Combine(folder, name + ".mp4");
    }

    /// <summary>The project's largest existing video file (the main recording), or null.</summary>
    public static FileInfo? MainVideo(Project p) => p.Media
        .Where(m => !m.IsStill && !m.IsAudioOnly)
        .Select(m => new FileInfo(m.OriginalPath))
        .Where(f => f.Exists)
        .OrderByDescending(f => f.Length)
        .FirstOrDefault();

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
