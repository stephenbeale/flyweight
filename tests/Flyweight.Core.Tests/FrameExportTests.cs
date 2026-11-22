using Flyweight.Core.Editing;
using Flyweight.Core.Export;
using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

public sealed class FrameExportTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("flyweight-frame-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
    static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    static bool FfmpegAvailable()
    {
        try { FfmpegRunner.RunFfmpegAsync(new[] { "-version" }).Wait(); return true; }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("FLYWEIGHT_REQUIRE_FFMPEG") == "1") throw new InvalidOperationException("ffmpeg required", ex);
            return false;
        }
    }

    async Task<(int R, int G, int B)> Pixel(string image, int x, int y)
    {
        var raw = Path.Combine(_dir, $"px-{Guid.NewGuid():N}.rgb");
        await FfmpegRunner.RunFfmpegAsync(new[] { "-i", image, "-vf", $"format=rgb24,crop=1:1:{x}:{y}", "-f", "rawvideo", "-pix_fmt", "rgb24", raw });
        var b = await File.ReadAllBytesAsync(raw);
        return (b[0], b[1], b[2]);
    }

    [Fact]
    public async Task Frame_comes_from_the_original_with_the_clips_zoom_and_brightness()
    {
        if (!FfmpegAvailable()) return;
        // 2 s: left half red, right half blue, then 2 s dark grey.
        var halves = Path.Combine(_dir, "halves.mp4");
        await FfmpegRunner.RunFfmpegAsync(new[]
        {
            "-f", "lavfi", "-i", "color=c=red:s=640x720:r=30:d=2", "-f", "lavfi", "-i", "color=c=blue:s=640x720:r=30:d=2",
            "-filter_complex", "[0:v][1:v]hstack=inputs=2[v]", "-map", "[v]", "-c:v", "libx264", "-pix_fmt", "yuv420p", halves,
        });
        var grey = Path.Combine(_dir, "grey.mp4");
        await FfmpegRunner.RunFfmpegAsync(new[] { "-f", "lavfi", "-i", "color=c=0x404040:s=1280x720:r=30:d=2", "-c:v", "libx264", "-pix_fmt", "yuv420p", grey });

        var p = new Project();
        var mH = await MediaProbe.ProbeAsync(halves);
        var mG = await MediaProbe.ProbeAsync(grey);
        p.Media.AddRange(new[] { mH, mG });
        var c1 = TimelineOps.Append(p, mH);
        var c2 = TimelineOps.Append(p, mG);
        TimelineOps.SetClipZoom(p, c1.Id, 2);
        TimelineOps.SetClipPan(p, c1.Id, 1, 0.5);   // zoomed into the blue half
        TimelineOps.SetClipBrightness(p, c2.Id, 0.5);

        var zoomed = Path.Combine(_dir, "zoomed.png");
        await FfmpegRunner.RunFfmpegAsync(FrameExport.BuildArgs(p, FrameExport.LocateFrame(p, S(1))!.Value, 1920, 1080, zoomed));
        var info = await MediaProbe.ProbeAsync(zoomed);
        Assert.Equal((1920, 1080), (info.Width, info.Height)); // output size, not the 540p proxy
        var left = await Pixel(zoomed, 100, 540);
        Assert.True(left.B > 200 && left.R < 60, $"zoomed into the blue half, so the left edge is blue: {left}");

        var bright = Path.Combine(_dir, "bright.png");
        await FfmpegRunner.RunFfmpegAsync(FrameExport.BuildArgs(p, FrameExport.LocateFrame(p, S(3))!.Value, 1920, 1080, bright));
        var g = await Pixel(bright, 960, 540);
        int expected = ClipLook.Tone(0x40, ClipLook.Gamma(0.5), ClipLook.ContrastGain(0));
        Assert.InRange(g.G, expected - 5, expected + 5);
    }

    [Fact]
    public void At_the_very_end_the_last_frame_is_used_and_speed_maps_annotation_time()
    {
        var p = new Project();
        var m = new SourceMedia { OriginalPath = "a.mp4", Duration = S(10), Fps = 30, Width = 1920, Height = 1080 };
        p.Media.Add(m);
        var c = TimelineOps.Append(p, m);
        TimelineOps.SetClipSpeed(p, c.Id, 2);                 // 10 s of footage over 5 s
        var end = FrameExport.LocateFrame(p, S(99))!.Value;
        Assert.True(end.SourceTime > S(9.8));
        var mid = FrameExport.LocateFrame(p, S(2))!.Value;
        Assert.Equal(S(4), mid.SourceTime);
        Assert.Equal(S(2), FrameExport.TimelineTimeOf(mid));
        Assert.Null(FrameExport.LocateFrame(new Project(), S(1)));
    }

    [Fact]
    public void Names_and_folders_never_overwrite()
    {
        var p = new Project();
        // Path.Combine, not a literal "C:\…": CI runs these tests on Linux, where a backslash isn't a folder separator.
        Assert.Equal("Barbour guide frame 1m23s.png", FrameExport.DefaultFileName(p, Path.Combine(_dir, "Barbour guide.flyweight"), S(83)));
        Assert.Equal("My video frame 1h02m03s.png", FrameExport.DefaultFileName(p, null, S(3723)));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), FrameExport.DefaultFolder(p));

        var a = Path.Combine(_dir, "shot.png");
        Assert.Equal(a, FrameExport.Unique(a));
        File.WriteAllText(a, "");
        Assert.Equal(Path.Combine(_dir, "shot (2).png"), FrameExport.Unique(a));
    }
}
