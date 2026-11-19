using Flyweight.Core.Editing;
using Flyweight.Core.Export;
using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

/// <summary>
/// End-to-end tests that run the real ffmpeg. They generate synthetic clips, so no fixtures
/// are needed. Skipped automatically if ffmpeg isn't installed.
/// </summary>
public sealed class FfmpegIntegrationTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("flyweight-test-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>
    /// True if ffmpeg runs. Locally the tests self-skip without it; CI sets
    /// FLYWEIGHT_REQUIRE_FFMPEG=1 so a broken ffmpeg install fails loudly instead of skipping.
    /// </summary>
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

    async Task<string> MakeClip(string name, int w, int h, double seconds, string color, bool audio, double fps = 30)
    {
        var path = Path.Combine(_dir, name);
        var args = new List<string> { "-f", "lavfi", "-i", $"color=c={color}:s={w}x{h}:r={FfmpegRunner.Num(fps)}:d={FfmpegRunner.Num(seconds)}" };
        if (audio) args.AddRange(new[] { "-f", "lavfi", "-i", $"sine=frequency=440:duration={FfmpegRunner.Num(seconds)}" });
        // Long GOP like a phone, to prove the export doesn't depend on keyframes.
        args.AddRange(new[] { "-c:v", "libx264", "-g", "250", "-pix_fmt", "yuv420p" });
        if (audio) args.AddRange(new[] { "-c:a", "aac" });
        args.Add(path);
        await FfmpegRunner.RunFfmpegAsync(args);
        return path;
    }

    /// <summary>Read one RGB pixel from a video at time t.</summary>
    async Task<(int R, int G, int B)> Pixel(string video, double t, int x, int y)
    {
        var raw = Path.Combine(_dir, $"px-{Guid.NewGuid():N}.rgb");
        await FfmpegRunner.RunFfmpegAsync(new[]
        {
            "-ss", FfmpegRunner.Num(t), "-i", video, "-frames:v", "1",
            "-vf", $"format=rgb24,crop=1:1:{x}:{y}", "-f", "rawvideo", "-pix_fmt", "rgb24", raw,
        });
        var b = await File.ReadAllBytesAsync(raw);
        return (b[0], b[1], b[2]);
    }

    [Fact]
    public async Task Probe_reads_size_fps_duration_audio()
    {
        if (!FfmpegAvailable()) return;
        var path = await MakeClip("probe.mp4", 640, 360, 2, "blue", audio: true, fps: 25);
        var m = await MediaProbe.ProbeAsync(path);
        Assert.Equal(640, m.Width);
        Assert.Equal(360, m.Height);
        Assert.Equal(25, m.Fps, 1);
        Assert.InRange(m.Duration.TotalSeconds, 1.9, 2.2);
        Assert.True(m.HasAudio);
    }

    [Fact]
    public async Task Proxy_is_padded_16x9_and_cached()
    {
        if (!FfmpegAvailable()) return;
        var vertical = await MakeClip("vertical.mp4", 360, 640, 1, "green", audio: false);
        var lib = new MediaLibrary(Path.Combine(_dir, "cache"));
        var m = await lib.ProbeAsync(vertical);
        await lib.EnsureProxyAsync(m);

        var st = lib.GetState(m.Id);
        Assert.Equal(ProxyStatus.Ready, st.Status);
        var proxy = await MediaProbe.ProbeAsync(st.ProxyPath!);
        Assert.Equal((960, 540), (proxy.Width, proxy.Height));
        Assert.True(File.Exists(st.ThumbnailPath));

        // Second library instance (e.g. reopening the project) hits the cache instantly.
        var lib2 = new MediaLibrary(Path.Combine(_dir, "cache"));
        await lib2.EnsureProxyAsync(m);
        Assert.Equal(st.ProxyPath, lib2.GetState(m.Id).ProxyPath);
    }

    [Fact]
    public async Task Export_concats_trims_pads_and_overlays_annotations_at_the_right_time()
    {
        if (!FfmpegAvailable()) return;
        var red = await MakeClip("red.mp4", 1280, 720, 4, "red", audio: true);
        var blue = await MakeClip("blue.mp4", 480, 640, 3, "blue", audio: false, fps: 25); // vertical, silent, other fps

        var p = new Project();
        var mRed = await MediaProbe.ProbeAsync(red);
        var mBlue = await MediaProbe.ProbeAsync(blue);
        p.Media.AddRange(new[] { mRed, mBlue });
        var c1 = TimelineOps.Append(p, mRed);
        TimelineOps.Append(p, mBlue);
        TimelineOps.TrimIn(p, c1.Id, TimeSpan.FromSeconds(1)); // red now 1–4s → 3s long. Total 6s.

        // White box over the top-left quarter, visible timeline 1.0–2.0s.
        var box = TimelineOps.CreateAnnotation(p, TimeSpan.FromSeconds(1), AnnotationKind.Box, 0, 0, .5f, .5f)!;
        TimelineOps.RetimeAnnotation(p, box.Id, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));

        // Stand-in for the UI's GDI+ renderer: a 1920×1080 PNG, opaque white top-left, transparent elsewhere.
        var png = Path.Combine(_dir, "box.png");
        await FfmpegRunner.RunFfmpegAsync(new[]
        {
            "-f", "lavfi", "-i", "color=c=white@1.0:s=960x540,format=rgba",
            "-vf", "pad=1920:1080:0:0:color=black@0.0", "-frames:v", "1", png,
        });

        var output = Path.Combine(_dir, "out.mp4");
        var plan = ExportPlan.Build(p, new ExportSettings { Preset = "ultrafast" },
            new Dictionary<Guid, string> { [box.Id] = png }, output);

        var reports = new List<double>();
        await plan.RunAsync(new SyncProgress(reports.Add));

        var outInfo = await MediaProbe.ProbeAsync(output);
        Assert.Equal((1920, 1080), (outInfo.Width, outInfo.Height));
        Assert.InRange(outInfo.Duration.TotalSeconds, 5.9, 6.15);
        Assert.Equal(30, outInfo.Fps, 1); // first clip's rate
        Assert.True(outInfo.HasAudio);
        Assert.Contains(1.0, reports);

        static bool IsRed((int R, int G, int B) c) => c.R > 200 && c.G < 60 && c.B < 60;
        static bool IsWhite((int R, int G, int B) c) => c.R > 200 && c.G > 200 && c.B > 200;
        static bool IsBlue((int R, int G, int B) c) => c.B > 200 && c.R < 60 && c.G < 60;
        static bool IsBlack((int R, int G, int B) c) => c.R < 40 && c.G < 40 && c.B < 40;

        Assert.True(IsRed(await Pixel(output, 0.5, 100, 100)), "before annotation: red");
        Assert.True(IsWhite(await Pixel(output, 1.5, 100, 100)), "during annotation: white box");
        Assert.True(IsRed(await Pixel(output, 1.5, 1500, 900)), "outside box: red");
        Assert.True(IsRed(await Pixel(output, 2.5, 100, 100)), "after annotation: red");
        Assert.True(IsBlue(await Pixel(output, 4.5, 960, 540)), "second clip: blue centre");
        Assert.True(IsBlack(await Pixel(output, 4.5, 50, 540)), "vertical clip: black side bars");
    }

    sealed class SyncProgress(Action<double> a) : IProgress<double>
    {
        public void Report(double value) => a(value);
    }
}
