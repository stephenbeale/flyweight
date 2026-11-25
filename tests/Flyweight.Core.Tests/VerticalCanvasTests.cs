using Flyweight.Core.Editing;
using Flyweight.Core.Export;
using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

/// <summary>D41: vertical (9:16) projects for YouTube Shorts.</summary>
public sealed class VerticalCanvasTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("flyweight-vertical-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
    static TimeSpan S(double s) => TimeSpan.FromSeconds(s);
    static readonly double Vertical = Canvas.Aspect(CanvasShape.Vertical);

    static SourceMedia Media(int w, int h) => new() { OriginalPath = "a.mp4", Duration = S(10), Fps = 30, Width = w, Height = h };

    // ---------------- The setting ----------------

    [Fact]
    public void Old_files_load_landscape_and_the_shape_saves_and_undoes()
    {
        var legacy = ProjectSerializer.Serialize(new Project()).Replace("\"Shape\": \"Landscape\",", "");
        Assert.DoesNotContain("\"Shape\"", legacy);
        Assert.Equal(CanvasShape.Landscape, ProjectSerializer.Deserialize(legacy).Shape);
        Assert.Equal(CanvasShape.Vertical, ProjectSerializer.Deserialize(ProjectSerializer.Serialize(new Project { Shape = CanvasShape.Vertical })).Shape);

        var session = new ProjectSession();
        session.New();
        session.Execute(p => TimelineOps.SetCanvasShape(p, CanvasShape.Vertical));
        Assert.Equal(CanvasShape.Vertical, session.Project.Shape);
        session.Undo();
        Assert.Equal(CanvasShape.Landscape, session.Project.Shape);
    }

    [Theory]
    [InlineData(ExportResolution.P1080, 1080, 1920)]
    [InlineData(ExportResolution.P1440, 1440, 2560)]
    [InlineData(ExportResolution.P2160, 2160, 3840)]
    public void Vertical_output_sizes_stand_upright(ExportResolution r, int w, int h)
    {
        var s = new ExportSettings { Resolution = r };
        Assert.Equal((w, h), s.SizeFor(CanvasShape.Vertical));
        Assert.Equal((h, w), s.SizeFor(CanvasShape.Landscape));
        Assert.Equal(3, ExportResolutions.Labels(CanvasShape.Vertical).Count);
    }

    [Fact]
    public void Vertical_1080p_gets_the_1080p_bitrate_not_a_1920p_one()
    {
        string Args(CanvasShape shape)
        {
            var p = new Project { Shape = shape };
            var m = Media(1920, 1080);
            p.Media.Add(m);
            TimelineOps.Append(p, m);
            var plan = ExportPlan.Build(p, new ExportSettings { UseHardwareEncoder = false }, new Dictionary<Guid, string>(), "out.mp4");
            return string.Join(" ", plan.Arguments);
        }
        int kbps = ExportPlan.YouTubeBitrateKbps(1080, 30);
        Assert.Contains($"-maxrate {kbps}k", Args(CanvasShape.Vertical));
        Assert.Contains($"-maxrate {kbps}k", Args(CanvasShape.Landscape));
    }

    // ---------------- Fitting and blur ----------------

    [Fact]
    public void In_a_vertical_project_portrait_clips_fill_and_landscape_clips_get_blurred_bars()
    {
        var p = new Project { Shape = CanvasShape.Vertical, Background = BackgroundFill.Blur };
        var portrait = Media(1080, 1920);
        var wide = Media(1920, 1080);
        p.Media.AddRange(new[] { portrait, wide });
        var c1 = TimelineOps.Append(p, portrait);
        var c2 = TimelineOps.Append(p, wide);
        Assert.False(ClipLook.BlursBars(p, c1, portrait));
        Assert.True(ClipLook.BlursBars(p, c2, wide));

        var g = ExportPlan.Build(p, new ExportSettings(), new Dictionary<Guid, string>(), "out.mp4").FilterGraph;
        Assert.Contains("scale=36:64:force_original_aspect_ratio=increase", g); // the tiny blur frame stands upright too
        Assert.Contains("scale=1080:1920:force_original_aspect_ratio=decrease", g);
    }

    // ---------------- Overlays keep square pixels on a tall canvas ----------------

    [Fact]
    public void Overlay_box_keeps_its_shape_on_a_vertical_canvas()
    {
        var o = new OverlayClip { Scale = 0.5, X = 0.5, Y = 0.5 };
        var (_, _, w, h) = OverlayLayout.Rect(o, Media(1920, 1080), Vertical);
        // In 1080x1920 pixels the box must still be 16:9.
        Assert.Equal(16.0 / 9, w * 1080 / (h * 1920), 6);
        var (_, _, pw, ph) = OverlayLayout.PixelRect(o, Media(1920, 1080), 1080, 1920);
        Assert.Equal(540, pw);
        Assert.InRange(ph, 302, 306);
    }

    [Fact]
    public void Full_frame_and_corner_placements_fit_the_vertical_canvas()
    {
        var crop = new OverlayClip();
        // A portrait overlay fills a vertical canvas exactly; a landscape one runs full width.
        Assert.Equal(1, OverlayLayout.Place(OverlayPosition.FullFrame, 0.35, Media(1080, 1920), crop, Vertical).Scale, 6);
        Assert.Equal(1, OverlayLayout.Place(OverlayPosition.FullFrame, 0.35, Media(1920, 1080), crop, Vertical).Scale, 6);

        // Bottom-right: the same gap in PIXELS from the right and bottom edges.
        var m = Media(1920, 1080);
        var (x, y, s) = OverlayLayout.Place(OverlayPosition.BottomRight, 0.35, m, crop, Vertical);
        var o = new OverlayClip { Scale = s, X = x, Y = y };
        var (bx, by, bw, bh) = OverlayLayout.Rect(o, m, Vertical);
        double right = (1 - bx - bw) * 1080, bottom = (1 - by - bh) * 1920;
        Assert.Equal(right, bottom, 3);
    }

    // ---------------- Real ffmpeg ----------------

    static bool FfmpegAvailable()
    {
        try { Task.Run(() => FfmpegRunner.RunFfmpegAsync(new[] { "-version" })).Wait(); return true; }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("FLYWEIGHT_REQUIRE_FFMPEG") == "1") throw new InvalidOperationException("ffmpeg required", ex);
            return false;
        }
    }

    async Task<string> Clip(string name, int w, int h, string color)
    {
        var path = Path.Combine(_dir, name);
        await FfmpegRunner.RunFfmpegAsync(new[] { "-f", "lavfi", "-i", $"color=c={color}:s={w}x{h}:r=30:d=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", path });
        return path;
    }

    async Task<(int R, int G, int B)> Pixel(string file, double t, int x, int y)
    {
        var raw = Path.Combine(_dir, $"px-{Guid.NewGuid():N}.rgb");
        var args = new List<string>();
        if (t > 0) args.AddRange(new[] { "-ss", FfmpegRunner.Num(t) });
        args.AddRange(new[] { "-i", file, "-frames:v", "1", "-vf", $"format=rgb24,crop=1:1:{x}:{y}", "-f", "rawvideo", "-pix_fmt", "rgb24", raw });
        await FfmpegRunner.RunFfmpegAsync(args);
        var b = await File.ReadAllBytesAsync(raw);
        return (b[0], b[1], b[2]);
    }

    [Fact]
    public async Task Vertical_export_is_1080x1920_portrait_fills_landscape_gets_bars_overlay_lands_in_place()
    {
        if (!FfmpegAvailable()) return;
        var portrait = await Clip("portrait.mp4", 360, 640, "blue");
        var wide = await Clip("wide.mp4", 640, 360, "red");
        var cam = await Clip("cam.mp4", 320, 180, "yellow");

        var p = new Project { Shape = CanvasShape.Vertical, Background = BackgroundFill.Black };
        var mP = await MediaProbe.ProbeAsync(portrait);
        var mW = await MediaProbe.ProbeAsync(wide);
        var mC = await MediaProbe.ProbeAsync(cam);
        p.Media.AddRange(new[] { mP, mW, mC });
        TimelineOps.Append(p, mP);
        TimelineOps.Append(p, mW);
        var o = TimelineOps.AddOverlay(p, mC, S(0), 1);
        TimelineOps.SetOverlayLayout(p, o.Id, 0.5, 0.25, 0.5); // half the width, a quarter of the way down

        var output = Path.Combine(_dir, "vertical.mp4");
        await ExportPlan.Build(p, new ExportSettings { Preset = "ultrafast", UseHardwareEncoder = false }, new Dictionary<Guid, string>(), output).RunAsync();
        var probed = await MediaProbe.ProbeAsync(output);
        Assert.Equal((1080, 1920), (probed.Width, probed.Height));

        static bool Is((int R, int G, int B) c, int r, int g, int b) => Math.Abs(c.R - r) < 60 && Math.Abs(c.G - g) < 60 && Math.Abs(c.B - b) < 60;
        Assert.True(Is(await Pixel(output, 0.5, 540, 1800), 0, 0, 255), "portrait clip fills to the bottom");
        Assert.True(Is(await Pixel(output, 0.5, 540, 480), 255, 255, 0), "overlay centred at a quarter of the way down");
        // Overlay box: 540 wide, 304 tall at y = 480 - 152 .. 480 + 152.
        Assert.True(Is(await Pixel(output, 0.5, 540, 310), 0, 0, 255), "just above the overlay is the clip");
        Assert.True(Is(await Pixel(output, 0.5, 280, 480), 255, 255, 0), "overlay reaches x=280");
        Assert.True(Is(await Pixel(output, 0.5, 260, 480), 0, 0, 255), "but not x=260");
        Assert.True(Is(await Pixel(output, 1.5, 540, 960), 255, 0, 0), "landscape clip in the middle");
        Assert.True(Is(await Pixel(output, 1.5, 540, 1800), 0, 0, 0), "black bar below it");

        // Blurred bars and Save frame at the vertical size.
        p.Background = BackgroundFill.Blur;
        var hit = FrameExport.LocateFrame(p, S(1.5))!.Value;
        var png = Path.Combine(_dir, "frame.png");
        await FfmpegRunner.RunFfmpegAsync(FrameExport.BuildArgs(p, hit, 1080, 1920, png));
        var info = await MediaProbe.ProbeAsync(png);
        Assert.Equal((1080, 1920), (info.Width, info.Height));
        var bar = await Pixel(png, 0, 540, 1800);
        Assert.True(bar.R > 80 && bar.R < 200 && bar.G < 60, $"bar should be dim red blur, was {bar}");
    }
}
