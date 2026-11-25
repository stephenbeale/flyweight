using Flyweight.Core.Editing;
using Flyweight.Core.Export;
using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

public sealed class AnnotationFadeTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("flyweight-fade-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    static (Project p, Annotation box) BoxOverWholeClip(double clipSeconds = 10, double fadeIn = 1, double fadeOut = 2)
    {
        var p = new Project();
        var m = new SourceMedia { OriginalPath = "a.mp4", Duration = S(clipSeconds), Fps = 30 };
        p.Media.Add(m);
        TimelineOps.Append(p, m);
        var box = TimelineOps.CreateAnnotation(p, S(0), AnnotationKind.Box, 0, 0, .5f, .5f)!;
        box.End = S(clipSeconds);
        box.FadeIn = fadeIn;
        box.FadeOut = fadeOut;
        return (p, box);
    }

    [Fact]
    public void Opacity_ramps_in_and_out()
    {
        var (p, box) = BoxOverWholeClip();
        Assert.Equal(0, (double)TimelineOps.AnnotationOpacity(p, box, S(0)), 3);
        Assert.Equal(0.5, (double)TimelineOps.AnnotationOpacity(p, box, S(0.5)), 3);
        Assert.Equal(1, (double)TimelineOps.AnnotationOpacity(p, box, S(5)), 3);
        Assert.Equal(0.5, (double)TimelineOps.AnnotationOpacity(p, box, S(9)), 3);
    }

    [Fact]
    public void No_fades_means_fully_visible()
    {
        var (p, box) = BoxOverWholeClip(fadeIn: 0, fadeOut: 0);
        Assert.Equal(1f, TimelineOps.AnnotationOpacity(p, box, S(0)));
        Assert.Equal(1f, TimelineOps.AnnotationOpacity(p, box, S(9.99)));
    }

    [Fact]
    public void Fades_longer_than_the_annotation_are_scaled_to_fit()
    {
        var (p, box) = BoxOverWholeClip(clipSeconds: 2, fadeIn: 3, fadeOut: 1); // 4 s of fades in 2 s
        var f = TimelineOps.FadeFor(p, box)!.Value;
        Assert.Equal(1.5, f.In, 3);
        Assert.Equal(0.5, f.Out, 3);
    }

    [Fact]
    public void A_run_across_a_cut_fades_in_on_its_first_piece_and_out_on_its_last()
    {
        var (p, box) = BoxOverWholeClip();
        TimelineOps.SplitAt(p, S(5));
        var pieces = TimelineOps.LinkedPieces(p, box);
        Assert.Equal(2, pieces.Count);
        Assert.Equal((1.0, 0.0), (TimelineOps.FadeFor(p, pieces[0])!.Value.In, TimelineOps.FadeFor(p, pieces[0])!.Value.Out));
        Assert.Equal((0.0, 2.0), (TimelineOps.FadeFor(p, pieces[1])!.Value.In, TimelineOps.FadeFor(p, pieces[1])!.Value.Out));
        // No dip at the cut.
        Assert.Equal(1, (double)TimelineOps.AnnotationOpacity(p, pieces[0], S(4.99)), 2);
        Assert.Equal(1, (double)TimelineOps.AnnotationOpacity(p, pieces[1], S(5.01)), 2);
    }

    [Fact]
    public void Fades_sync_across_a_run_and_old_files_load_without_fades()
    {
        var (p, box) = BoxOverWholeClip();
        TimelineOps.SplitAt(p, S(5));
        box.FadeIn = 0.25;
        TimelineOps.SyncLinked(p, box.Id);
        Assert.All(TimelineOps.LinkedPieces(p, box), a => Assert.Equal(0.25, a.FadeIn));

        var json = """{"Media":[],"Timeline":[],"Annotations":[{"Id":"00000000-0000-0000-0000-000000000001","Kind":"Box"}]}""";
        var a = ProjectSerializer.Deserialize(json).Annotations[0];
        Assert.Equal((0.0, 0.0), (a.FadeIn, a.FadeOut));
    }

    // ---------------- real export ----------------

    static bool FfmpegAvailable()
    {
        // Task.Run: blocking one of xUnit's few test threads here deadlocks parallel test classes.
        try { Task.Run(() => FfmpegRunner.RunFfmpegAsync(new[] { "-version" })).Wait(); return true; }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("FLYWEIGHT_REQUIRE_FFMPEG") == "1")
                throw new InvalidOperationException("ffmpeg is required (FLYWEIGHT_REQUIRE_FFMPEG=1) but could not run.", ex);
            return false;
        }
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

    [Fact]
    public async Task Export_fades_the_annotation_in_and_out()
    {
        if (!FfmpegAvailable()) return;
        var video = Path.Combine(_dir, "red.mp4");
        await FfmpegRunner.RunFfmpegAsync(new[] { "-f", "lavfi", "-i", "color=c=red:s=640x360:r=30:d=4", "-c:v", "libx264", "-pix_fmt", "yuv420p", video });

        var p = new Project();
        var m = await MediaProbe.ProbeAsync(video);
        p.Media.Add(m);
        TimelineOps.Append(p, m);
        var box = TimelineOps.CreateAnnotation(p, S(0), AnnotationKind.Box, 0, 0, .1f, .1f)!;
        box.End = S(4);
        box.FadeIn = 2;
        box.FadeOut = 1;

        // Stand-in for the UI's renderer: opaque white square top-left, transparent elsewhere.
        var png = Path.Combine(_dir, "box.png");
        await FfmpegRunner.RunFfmpegAsync(new[]
        {
            "-f", "lavfi", "-i", "color=c=white@1.0:s=300x300,format=rgba",
            "-vf", "pad=1920:1080:0:0:color=black@0.0", "-frames:v", "1", png,
        });

        var output = Path.Combine(_dir, "faded.mp4");
        await ExportPlan.Build(p, new ExportSettings { Preset = "ultrafast", UseHardwareEncoder = false },
            new Dictionary<Guid, string> { [box.Id] = png }, output).RunAsync();

        // Red under white at opacity a gives G ≈ B ≈ 255·a.
        var start = await Pixel(output, 0.05, 100, 100);
        var mid = await Pixel(output, 1.0, 100, 100);     // halfway through a 2 s fade-in
        var full = await Pixel(output, 2.5, 100, 100);
        var fadingOut = await Pixel(output, 3.5, 100, 100); // halfway through a 1 s fade-out
        Assert.True(start.G < 40, $"starts nearly invisible, got {start}");
        Assert.InRange(mid.G, 90, 165);
        Assert.True(full.G > 230, $"fully visible, got {full}");
        Assert.InRange(fadingOut.G, 90, 165);
        Assert.True((await Pixel(output, 2.5, 500, 300)).G < 40, "outside the box stays red");
    }
}
