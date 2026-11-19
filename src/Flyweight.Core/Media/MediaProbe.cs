using System.Globalization;
using System.Text.Json;
using Flyweight.Core.Model;

namespace Flyweight.Core.Media;

public static class MediaProbe
{
    /// <summary>Read duration, size, frame rate and audio presence with ffprobe.</summary>
    public static async Task<SourceMedia> ProbeAsync(string path, CancellationToken ct = default)
    {
        var json = await FfmpegRunner.RunFfprobeAsync(new[]
        {
            "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path,
        }, ct);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        JsonElement? video = null;
        bool hasAudio = false;
        foreach (var s in root.GetProperty("streams").EnumerateArray())
        {
            var type = s.GetProperty("codec_type").GetString();
            bool isCoverArt = s.TryGetProperty("disposition", out var d) &&
                              d.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
            if (type == "video" && video is null && !isCoverArt) video = s;
            if (type == "audio") hasAudio = true;
        }
        var format = root.TryGetProperty("format", out var fmtEl) ? Str(fmtEl, "format_name") ?? "" : "";
        if (video is not { } v) throw new InvalidDataException($"'{Path.GetFileName(path)}' has no video stream.");

        int w = v.GetProperty("width").GetInt32();
        int h = v.GetProperty("height").GetInt32();

        // Phones store portrait video as landscape + a rotation flag. ffmpeg auto-rotates on
        // decode, so report the size the user actually sees.
        int rotation = Math.Abs(ReadRotation(v)) % 180;
        if (rotation == 90) (w, h) = (h, w);

        double fps = ParseRate(Str(v, "avg_frame_rate")) is > 0 and var avg ? avg : ParseRate(Str(v, "r_frame_rate"));
        if (fps is <= 0 or > 240) fps = 30;

        double seconds = ParseDouble(root.TryGetProperty("format", out var fmt) ? Str(fmt, "duration") : null)
                         ?? ParseDouble(Str(v, "duration")) ?? 0;

        if (IsStillFormat(format))
            return new SourceMedia
            {
                OriginalPath = Path.GetFullPath(path),
                Duration = SourceMedia.StillMaxDuration,
                Fps = 30,
                Width = w,
                Height = h,
                HasAudio = false,
                IsStill = true,
            };

        return new SourceMedia
        {
            OriginalPath = Path.GetFullPath(path),
            Duration = TimeSpan.FromSeconds(seconds),
            Fps = fps,
            Width = w,
            Height = h,
            HasAudio = hasAudio,
        };
    }

    /// <summary>
    /// ffprobe reads images through the image2 demuxer or a "*_pipe" one (png_pipe, jpeg_pipe…),
    /// and reports a meaningless 0–0.04 s duration. GIF has its own demuxer and is treated as video.
    /// </summary>
    internal static bool IsStillFormat(string formatName) =>
        formatName.Split(',').Any(f => f == "image2" || f.EndsWith("_pipe", StringComparison.Ordinal));

    static int ReadRotation(JsonElement v)
    {
        if (v.TryGetProperty("side_data_list", out var sdl))
            foreach (var sd in sdl.EnumerateArray())
                if (sd.TryGetProperty("rotation", out var r) && r.TryGetDouble(out var deg))
                    return (int)Math.Round(deg);
        if (v.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var rot) &&
            int.TryParse(rot.GetString(), out var deg2))
            return deg2;
        return 0;
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    static double? ParseDouble(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    /// <summary>"30000/1001" → 29.97</summary>
    static double ParseRate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var parts = s.Split('/');
        if (parts.Length == 2 && ParseDouble(parts[0]) is { } n && ParseDouble(parts[1]) is { } d && d != 0) return n / d;
        return ParseDouble(s) ?? 0;
    }
}
