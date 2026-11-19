using Flyweight.Core.Editing;
using Flyweight.Core.Export;
using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

/// <summary>Export frame as image, freeze-frame location, and default export locations (DECISIONS D18).</summary>
public sealed class FrameExportTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("flyweight-frame-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    static bool FfmpegAvailable()
    {
        try { FfmpegRunner.RunFfmpegAsync(new[] { "-version" }).Wait(); return true; }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("FLYWEIGHT_REQUIRE_FFMPEG") == "1")
                throw new InvalidOperationException("ffmpeg is required (FLYWEIGHT_REQUIRE_FFMPEG=1) but could not run.", ex);
            return false;
        }
    }

    /// <summary>2 s video: red for the first second, green for the second. One long GOP, so 1.5 s is NOT a keyframe.</summary>
    async Task<string> RedThenGreen(string name, int w = 1280, int h = 720)
    {
        var path = Path.Combine(_dir, name);
        await FfmpegRunner.RunFfmpegAsync(new[]
        {
            "-f", "lavfi", "-i", $"color=c=red:s={w}x{h}:r=30:d=1",
            "-f", "lavfi", "-i", $"color=c=lime:s={w}x{h}:r=30:d=1",
            "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0[v]", "-map", "[v]",
            "-c:v", "libx264", "-g", "250", "-pix_fmt", "yuv420p", path,
        });
        return path;
    }

    async Task<(int R, int G, int B)> Pixel(string file, double t, int x, int y)
    {
        var raw = Path.Combine(_dir, $"px-{Guid.NewGuid():N}.rgb");
        await FfmpegRunner.RunFfmpegAsync(new[]
        {
            "-ss", FfmpegRunner.Num(t), "-i", file, "-frames:v", "1",
            "-vf", $"format=rgb24,crop=1:1:{x}:{y}", "-f", "rawvideo", "-pix_fmt", "rgb24", raw,
        });
        var b = await File.ReadAllBytesAsync(raw);
        return (b[0], b[1], b[2]);
    }

    static bool IsRed((int R, int G, int B) c) => c.R > 200 && c.G < 60 && c.B < 60;
    static bool IsGreen((int R, int G, int B) c) => c.G > 200 && c.R < 60 && c.B < 60;

    [Fact]
    public async Task Frame_is_the_exact_frame_from_the_original_at_output_size()
    {
        if (!FfmpegAvailable()) return;
        var video = await RedThenGreen("rg.mp4");
        var p = new Project();
        var m = await MediaProbe.ProbeAsync(video);
        p.Media.Add(m);
        var clip = TimelineOps.Append(p, m);
        TimelineOps.TrimIn(p, clip.Id, TimeSpan.FromSeconds(0.5)); // timeline 0 = source 0.5s

        var early = Path.Combine(_dir, "early.png");
        var late = Path.Combine(_dir, "late.png");
        await FrameExport.ExtractAsync(p, TimeSpan.FromSeconds(0.2), 1920, 1080, early); // source 0.7s → red
        await FrameExport.ExtractAsync(p, TimeSpan.FromSeconds(1.0), 1920, 1080, late);  // source 1.5s → green (mid-GOP)

        var info = await MediaProbe.ProbeAsync(late);
        Assert.Equal((1920, 1080), (info.Width, info.Height));
        Assert.True(info.IsStill); // a PNG probes as a still, so it can go straight into the media bin
        Assert.True(IsRed(await Pixel(early, 0, 960, 540)));
        Assert.True(IsGreen(await Pixel(late, 0, 960, 540)));
    }

    [Fact]
    public async Task Frame_at_or_past_the_end_uses_the_last_frame()
    {
        if (!FfmpegAvailable()) return;
        var video = await RedThenGreen("rg2.mp4");
        var p = new Project();
        var m = await MediaProbe.ProbeAsync(video);
        p.Media.Add(m);
        TimelineOps.Append(p, m);
        var png = Path.Combine(_dir, "end.png");
        await FrameExport.ExtractAsync(p, TimelineOps.TotalDuration(p) + TimeSpan.FromSeconds(5), 1280, 720, png);
        Assert.True(IsGreen(await Pixel(png, 0, 640, 360)));
    }

    [Fact]
    public async Task Freeze_frames_are_saved_into_the_given_folder_and_never_overwrite()
    {
        if (!FfmpegAvailable()) return;
        var footage = Directory.CreateDirectory(Path.Combine(_dir, "footage")).FullName;
        var video = await RedThenGreen(Path.Combine("footage", "Match.mp4"));
        var m = await MediaProbe.ProbeAsync(video);
        var lib = new MediaLibrary(Path.Combine(_dir, "cache"));

        var first = await lib.ExtractFrameAsync(m, TimeSpan.FromSeconds(1.5), footage);
        var second = await lib.ExtractFrameAsync(m, TimeSpan.FromSeconds(1.5), footage);
        Assert.Equal(Path.Combine(footage, "Match freeze 0m01s.png"), first);
        Assert.Equal(Path.Combine(footage, "Match freeze 0m01s (2).png"), second);
        Assert.True(IsGreen(await Pixel(first, 0, 640, 360)));
        Assert.True((await MediaProbe.ProbeAsync(first)).IsStill);
    }

    [Fact]
    public async Task Exported_frame_round_trips_back_into_the_project()
    {
        if (!FfmpegAvailable()) return;
        var video = await RedThenGreen("rg4.mp4");
        var p = new Project();
        var m = await MediaProbe.ProbeAsync(video);
        p.Media.Add(m);
        TimelineOps.Append(p, m);

        var png = Path.Combine(_dir, FrameExport.DefaultFileName(p, null, TimeSpan.FromSeconds(1.5)));
        await FrameExport.ExtractAsync(p, TimeSpan.FromSeconds(1.5), 1920, 1080, png);
        var still = await MediaProbe.ProbeAsync(png);
        p.Media.Add(still);
        Assert.Equal(2, p.Media.Count);
        Assert.Single(p.Timeline); // added to the media bin, not the timeline
        Assert.Equal("rg4 frame 0m01s.png", Path.GetFileName(png));
        Assert.True(p.Media[1].IsStill);
    }

    [Fact]
    public void Default_folder_is_the_folder_of_the_largest_video()
    {
        var small = Directory.CreateDirectory(Path.Combine(_dir, "small")).FullName;
        var big = Directory.CreateDirectory(Path.Combine(_dir, "big")).FullName;
        var pics = Directory.CreateDirectory(Path.Combine(_dir, "pics")).FullName;
        File.WriteAllBytes(Path.Combine(small, "a.mp4"), new byte[1_000]);
        File.WriteAllBytes(Path.Combine(big, "Holiday.mov"), new byte[50_000]);
        File.WriteAllBytes(Path.Combine(pics, "huge.png"), new byte[900_000]); // images don't count

        var p = new Project();
        p.Media.Add(new SourceMedia { OriginalPath = Path.Combine(small, "a.mp4") });
        p.Media.Add(new SourceMedia { OriginalPath = Path.Combine(big, "Holiday.mov") });
        p.Media.Add(new SourceMedia { OriginalPath = Path.Combine(pics, "huge.png"), IsStill = true });
        p.Media.Add(new SourceMedia { OriginalPath = Path.Combine(_dir, "missing.mp4") });

        Assert.Equal(big, ExportLocations.DefaultFolder(p));
        Assert.Equal(Path.Combine(big, "Holiday - edit.mp4"), ExportLocations.DefaultVideoPath(p, null));
        Assert.Equal(Path.Combine(big, "Trip.mp4"), ExportLocations.DefaultVideoPath(p, "/somewhere/Trip.flyweight"));
        Assert.Equal("Holiday frame 1h02m03s.png", FrameExport.DefaultFileName(p, null, new TimeSpan(1, 2, 3)));
    }

    [Fact]
    public void Unique_never_overwrites()
    {
        var path = Path.Combine(_dir, "shot.png");
        Assert.Equal(path, ExportLocations.Unique(path));
        File.WriteAllText(path, "");
        Assert.Equal(Path.Combine(_dir, "shot (2).png"), ExportLocations.Unique(path));
        File.WriteAllText(Path.Combine(_dir, "shot (2).png"), "");
        Assert.Equal(Path.Combine(_dir, "shot (3).png"), ExportLocations.Unique(path));
    }
}
