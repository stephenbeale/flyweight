using Flyweight.Core.Editing;
using Flyweight.Core.Export;
using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

/// <summary>Long-project exports (filter graph via a file) and default export/freeze-frame folders.</summary>
public sealed class ExportSafetyTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("flyweight-safety-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    static bool FfmpegAvailable()
    {
        // Task.Run: blocking one of xUnit's few test threads here deadlocks parallel test classes.
        try { Task.Run(() => FfmpegRunner.RunFfmpegAsync(["-version"])).Wait(); return true; }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("FLYWEIGHT_REQUIRE_FFMPEG") == "1")
                throw new InvalidOperationException("ffmpeg is required (FLYWEIGHT_REQUIRE_FFMPEG=1) but could not run.", ex);
            return false;
        }
    }

    async Task<string> Clip(string relativePath, string color, double seconds)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await FfmpegRunner.RunFfmpegAsync([
            "-f", "lavfi", "-i", $"color=c={color}:s=640x360:r=30:d={FfmpegRunner.Num(seconds)}",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", path,
        ]);
        return path;
    }

    async Task<(int R, int G, int B)> Pixel(string file, double t, int x, int y)
    {
        var raw = Path.Combine(_dir, $"px-{Guid.NewGuid():N}.rgb");
        await FfmpegRunner.RunFfmpegAsync([
            "-ss", FfmpegRunner.Num(t), "-i", file, "-frames:v", "1",
            "-vf", $"format=rgb24,crop=1:1:{x}:{y}", "-f", "rawvideo", "-pix_fmt", "rgb24", raw,
        ]);
        var b = await File.ReadAllBytesAsync(raw);
        return (b[0], b[1], b[2]);
    }

    // ---------------- C: long filter graphs ----------------

    [Theory]
    [InlineData("ffmpeg version 7.1-full_build-www.gyan.dev Copyright (c) 2000-2024", 7)]
    [InlineData("ffmpeg version 6.1.1-3ubuntu5 Copyright (c) 2000-2023 the FFmpeg developers", 6)]
    [InlineData("ffmpeg version n6.0 Copyright", 6)]
    [InlineData("ffmpeg version 2025-01-02-git-abc123-full_build-www.gyan.dev", 2025)]
    [InlineData("ffmpeg version N-118000-gabcdef", null)]
    [InlineData("something else", null)]
    public void Parses_the_ffmpeg_major_version(string line, int? expected) =>
        Assert.Equal(expected, FfmpegRunner.ParseMajorVersion(line));

    [Fact]
    public void Long_graph_option_uses_the_right_spelling_for_the_ffmpeg_version()
    {
        var args = new List<string> { "-i", "a.mp4", "-filter_complex", "GRAPH", "-map", "[v]", "out.mp4" };
        Assert.Equal(new[] { "-i", "a.mp4", "-/filter_complex", "f.txt", "-map", "[v]", "out.mp4" },
            FfmpegRunner.UseFilterScript(args, "f.txt", slashOptions: true));
        Assert.Equal(new[] { "-i", "a.mp4", "-filter_complex_script", "f.txt", "-map", "[v]", "out.mp4" },
            FfmpegRunner.UseFilterScript(args, "f.txt", slashOptions: false));
        Assert.Equal("GRAPH", args[3]); // input untouched
    }

    [Fact]
    public async Task A_project_with_a_very_long_filter_graph_still_exports()
    {
        if (!FfmpegAvailable()) return;
        var video = await Clip("long/talk.mp4", "red", 1.5);
        var p = new Project();
        var m = await MediaProbe.ProbeAsync(video);
        p.Media.Add(m);
        TimelineOps.Append(p, m);

        // A full-frame transparent PNG with a white square top-left, reused by enough annotations to need the file route.
        var png = Path.Combine(_dir, "box.png");
        await FfmpegRunner.RunFfmpegAsync([
            "-f", "lavfi", "-i", "color=c=white@1.0:s=200x200,format=rgba",
            "-vf", "pad=1920:1080:0:0:color=black@0.0", "-frames:v", "1", png,
        ]);
        var overlays = new Dictionary<Guid, string>();
        for (int i = 0; i < 60; i++)
        {
            var a = TimelineOps.CreateAnnotation(p, TimeSpan.FromSeconds(0.5), AnnotationKind.Box, 0, 0, .1f, .1f)!;
            overlays[a.Id] = png;
        }

        var output = Path.Combine(_dir, "long/out.mp4");
        var plan = ExportPlan.Build(p, new ExportSettings { Preset = "ultrafast", UseHardwareEncoder = false }, overlays, output);
        Assert.True(plan.FilterGraph.Length > FfmpegRunner.MaxInlineFilterLength, "test needs a graph that goes through the file path");

        await plan.RunAsync();

        var c = await Pixel(output, 1.0, 50, 50);
        Assert.True(c.R > 200 && c.G > 200 && c.B > 200, "annotations rendered");
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "flyweight-filter-*.txt")
            .Where(f => File.GetLastWriteTimeUtc(f) > DateTime.UtcNow.AddMinutes(-1))); // temp graph cleaned up
    }

    // ---------------- B: default folders ----------------

    [Fact]
    public void Video_export_defaults_next_to_the_largest_video()
    {
        var small = Directory.CreateDirectory(Path.Combine(_dir, "small")).FullName;
        var big = Directory.CreateDirectory(Path.Combine(_dir, "big")).FullName;
        File.WriteAllBytes(Path.Combine(small, "intro.mp4"), new byte[1_000]);
        File.WriteAllBytes(Path.Combine(big, "Holiday.mov"), new byte[50_000]);
        File.WriteAllBytes(Path.Combine(_dir, "huge.png"), new byte[900_000]);

        var p = new Project();
        p.Media.Add(new SourceMedia { OriginalPath = Path.Combine(small, "intro.mp4") });
        p.Media.Add(new SourceMedia { OriginalPath = Path.Combine(big, "Holiday.mov") });
        p.Media.Add(new SourceMedia { OriginalPath = Path.Combine(_dir, "huge.png"), IsStill = true }); // images don't count

        Assert.Equal(Path.Combine(big, "Holiday - edit.mp4"), FrameExport.DefaultVideoPath(p, null));
        Assert.Equal(Path.Combine(big, "Trip.mp4"), FrameExport.DefaultVideoPath(p, Path.Combine(_dir, "projects", "Trip.flyweight")));

        var empty = FrameExport.DefaultVideoPath(new Project(), null);
        Assert.Equal("My video.mp4", Path.GetFileName(empty));
    }

    [Fact]
    public async Task Freeze_frames_save_next_to_the_footage_and_never_overwrite()
    {
        if (!FfmpegAvailable()) return;
        var video = await Clip("footage/Match.mp4", "lime", 2);
        var footage = Path.GetDirectoryName(video)!;
        var m = await MediaProbe.ProbeAsync(video);
        var lib = new MediaLibrary(Path.Combine(_dir, "cache"));

        var first = await lib.ExtractFrameAsync(m, TimeSpan.FromSeconds(1.5), footage);
        var second = await lib.ExtractFrameAsync(m, TimeSpan.FromSeconds(1.5), footage);
        Assert.Equal(Path.Combine(footage, "Match freeze 0m01s.png"), first);
        Assert.Equal(Path.Combine(footage, "Match freeze 0m01s (2).png"), second);
        Assert.True((await MediaProbe.ProbeAsync(first)).IsStill);
    }
}
