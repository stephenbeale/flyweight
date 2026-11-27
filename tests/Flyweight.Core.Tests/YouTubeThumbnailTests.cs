using Flyweight.Core.Export;
using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

public sealed class YouTubeThumbnailTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("flyweight-thumb-").FullName;

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

    /// <summary>Random pixels: the worst case for compression.</summary>
    async Task<string> RandomPng()
    {
        var raw = Path.Combine(_dir, "random.rgb");
        var bytes = new byte[1280 * 720 * 3];
        new Random(42).NextBytes(bytes);
        await File.WriteAllBytesAsync(raw, bytes);
        var png = Path.Combine(_dir, "random.png");
        await FfmpegRunner.RunFfmpegAsync(["-f", "rawvideo", "-pix_fmt", "rgb24", "-s", "1280x720", "-i", raw, "-frames:v", "1", png]);
        return png;
    }

    [Fact]
    public async Task Worst_case_frame_is_too_big_as_png_but_fits_as_best_quality_jpeg()
    {
        if (!FfmpegAvailable()) return;
        var png = await RandomPng();
        Assert.True(new FileInfo(png).Length > YouTubeThumbnail.MaxBytes, "a PNG thumbnail can exceed YouTube's 2 MB limit");

        var jpg = Path.Combine(_dir, "thumb.jpg");
        Assert.Equal(2, await YouTubeThumbnail.SaveJpegUnderLimitAsync(png, jpg));
        Assert.True(new FileInfo(jpg).Length <= YouTubeThumbnail.MaxBytes);
        var info = await MediaProbe.ProbeAsync(jpg);
        Assert.Equal((1280, 720), (info.Width, info.Height));
    }

    [Fact]
    public async Task Steps_quality_down_only_when_needed()
    {
        if (!FfmpegAvailable()) return;
        var png = await RandomPng();
        var jpg = Path.Combine(_dir, "tight.jpg");
        const long tightLimit = 1_400_000;
        int q = await YouTubeThumbnail.SaveJpegUnderLimitAsync(png, jpg, tightLimit);
        Assert.True(q > 2);
        Assert.True(new FileInfo(jpg).Length <= tightLimit);
        Assert.Empty(Directory.GetFiles(_dir, "*.partial.jpg"));
    }

    [Fact]
    public async Task Ordinary_frame_keeps_the_best_quality()
    {
        if (!FfmpegAvailable()) return;
        var png = Path.Combine(_dir, "bars.png");
        await FfmpegRunner.RunFfmpegAsync(["-f", "lavfi", "-i", "testsrc2=s=1920x1080", "-frames:v", "1", png]);
        var jpg = Path.Combine(_dir, "bars.jpg");
        Assert.Equal(2, await YouTubeThumbnail.SaveJpegUnderLimitAsync(png, jpg));
        Assert.Equal((1280, 720), ((await MediaProbe.ProbeAsync(jpg)).Width, (await MediaProbe.ProbeAsync(jpg)).Height));
    }

    [Fact]
    public void File_name_follows_the_save_frame_naming()
    {
        var name = YouTubeThumbnail.DefaultFileName(new Project(), Path.Combine(_dir, "Trip.flyweight"), new TimeSpan(0, 1, 23));
        Assert.Equal("Trip thumbnail 1m23s.jpg", name);
    }
}
