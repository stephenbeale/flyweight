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
    public void In_a_vertical_project_landscape_clips_fill_the_frame_and_portrait_clips_fit()
    {
        var p = new Project { Shape = CanvasShape.Vertical, Background = BackgroundFill.Blur };
        var portrait = Media(1080, 1920);
        var wide = Media(1920, 1080);
        p.Media.AddRange([portrait, wide]);
        var c1 = TimelineOps.Append(p, portrait);
        var c2 = TimelineOps.Append(p, wide);
        Assert.False(ClipLook.Fills(p, c1, portrait));
        Assert.False(ClipLook.BlursBars(p, c1, portrait));
        Assert.True(ClipLook.Fills(p, c2, wide));
        Assert.False(ClipLook.BlursBars(p, c2, wide)); // nothing to fill: the picture covers the frame

        var g = ExportPlan.Build(p, new ExportSettings(), new Dictionary<Guid, string>(), "out.mp4").FilterGraph;
        Assert.Contains("crop=w='min(iw,2*trunc(ih*0.5625/2))'", g); // a 9:16 slice of the original, full height
        Assert.DoesNotContain("scale=36:64", g);                   // no blur anywhere
    }

    [Fact]
    public void Fit_whole_picture_brings_back_the_bars_and_the_blur()
    {
        var p = new Project { Shape = CanvasShape.Vertical, Background = BackgroundFill.Blur };
        var wide = Media(1920, 1080);
        p.Media.Add(wide);
        var c = TimelineOps.Append(p, wide);
        TimelineOps.SetClipFitWhole(p, [c.Id], true);
        Assert.False(ClipLook.Fills(p, c, wide));
        Assert.True(ClipLook.BlursBars(p, c, wide));

        var g = ExportPlan.Build(p, new ExportSettings(), new Dictionary<Guid, string>(), "out.mp4").FilterGraph;
        Assert.Contains("scale=36:64:force_original_aspect_ratio=increase", g); // the tiny blur frame stands upright too
        Assert.Contains("scale=1080:1920:force_original_aspect_ratio=decrease", g);
    }

    [Fact]
    public void Only_vertical_projects_fill_and_a_crop_keeps_its_own_framing()
    {
        var wide = Media(1920, 1080);
        var tall = Media(1080, 1920);
        var landscape = new Project { Shape = CanvasShape.Landscape };
        landscape.Media.AddRange([wide, tall]);
        Assert.False(ClipLook.Fills(landscape, TimelineOps.Append(landscape, wide), wide));
        Assert.False(ClipLook.Fills(landscape, TimelineOps.Append(landscape, tall), tall));

        var p = new Project { Shape = CanvasShape.Vertical };
        p.Media.Add(wide);
        var c = TimelineOps.Append(p, wide);
        c.CropLeft = 0.2;
        Assert.False(ClipLook.Fills(p, c, wide));
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.5, 0.5 * (1 - 0.31640625))]
    [InlineData(1.0, 1 - 0.31640625)]
    [InlineData(7.0, 1 - 0.31640625)] // clamped
    public void The_frame_slice_moves_across_the_picture(double frameX, double left)
    {
        var p = new Project { Shape = CanvasShape.Vertical };
        var wide = Media(1920, 1080);
        p.Media.Add(wide);
        var c = TimelineOps.Append(p, wide);
        TimelineOps.SetClipFrameX(p, c.Id, frameX);
        var (x, w) = ClipLook.FrameSlice(c, 16.0 / 9, Vertical);
        Assert.Equal(0.31640625, w, 6); // (9/16) / (16/9)
        Assert.Equal(left, x, 6);
    }

    [Fact]
    public void Frame_position_survives_save_split_and_undo()
    {
        var s = new ProjectSession();
        s.New(shape: CanvasShape.Vertical);
        var wide = Media(1920, 1080);
        s.Execute(p => { p.Media.Add(wide); TimelineOps.Append(p, wide); });
        var id = s.Project.Timeline[0].Id;
        s.Execute(p => TimelineOps.SetClipFrameX(p, id, 0.2));
        s.Execute(p => TimelineOps.SplitAt(p, S(5)));
        Assert.Equal(2, s.Project.Timeline.Count);
        Assert.All(s.Project.Timeline, c => Assert.Equal(0.2, c.FrameX)); // both halves keep the framing

        var loaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(s.Project));
        Assert.Equal(0.2, loaded.Timeline[0].FrameX);
        Assert.Equal(0.5, ProjectSerializer.Deserialize("{ \"Timeline\": [ { } ] }").Timeline[0].FrameX); // old files: centred

        s.Undo();
        s.Undo();
        Assert.Equal(0.5, s.Project.Timeline[0].FrameX);
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
        try { Task.Run(() => FfmpegRunner.RunFfmpegAsync(["-version"])).Wait(); return true; }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("FLYWEIGHT_REQUIRE_FFMPEG") == "1") throw new InvalidOperationException("ffmpeg required", ex);
            return false;
        }
    }

    async Task<string> Clip(string name, int w, int h, string color)
    {
        var path = Path.Combine(_dir, name);
        await FfmpegRunner.RunFfmpegAsync(["-f", "lavfi", "-i", $"color=c={color}:s={w}x{h}:r=30:d=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", path]);
        return path;
    }

    async Task<(int R, int G, int B)> Pixel(string file, double t, int x, int y)
    {
        var raw = Path.Combine(_dir, $"px-{Guid.NewGuid():N}.rgb");
        var args = new List<string>();
        if (t > 0) args.AddRange(["-ss", FfmpegRunner.Num(t)]);
        args.AddRange(["-i", file, "-frames:v", "1", "-vf", $"format=rgb24,crop=1:1:{x}:{y}", "-f", "rawvideo", "-pix_fmt", "rgb24", raw]);
        await FfmpegRunner.RunFfmpegAsync(args);
        var b = await File.ReadAllBytesAsync(raw);
        return (b[0], b[1], b[2]);
    }

    [Fact]
    public async Task Vertical_export_is_1080x1920_portrait_fills_fitted_landscape_gets_bars_overlay_lands_in_place()
    {
        if (!FfmpegAvailable()) return;
        var portrait = await Clip("portrait.mp4", 360, 640, "blue");
        var wide = await Clip("wide.mp4", 640, 360, "red");
        var cam = await Clip("cam.mp4", 320, 180, "yellow");

        var p = new Project { Shape = CanvasShape.Vertical, Background = BackgroundFill.Black };
        var mP = await MediaProbe.ProbeAsync(portrait);
        var mW = await MediaProbe.ProbeAsync(wide);
        var mC = await MediaProbe.ProbeAsync(cam);
        p.Media.AddRange([mP, mW, mC]);
        TimelineOps.Append(p, mP);
        var cW = TimelineOps.Append(p, mW);
        TimelineOps.SetClipFitWhole(p, [cW.Id], true); // the bars case; filling has its own test below
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

    [Fact]
    public async Task A_landscape_clip_fills_the_vertical_export_with_only_the_chosen_slice()
    {
        if (!FfmpegAvailable()) return;
        // 640x360: left half red, right half blue. A 9:16 slice of it is 202 px wide.
        var halves = Path.Combine(_dir, "halves.mp4");
        await FfmpegRunner.RunFfmpegAsync([
            "-f", "lavfi", "-i", "color=c=red:s=320x360:r=30:d=1", "-f", "lavfi", "-i", "color=c=blue:s=320x360:r=30:d=1",
            "-filter_complex", "[0:v][1:v]hstack=inputs=2[v]", "-map", "[v]", "-c:v", "libx264", "-pix_fmt", "yuv420p", halves,
        ]);
        var m = await MediaProbe.ProbeAsync(halves);
        var p = new Project { Shape = CanvasShape.Vertical, Background = BackgroundFill.Black };
        p.Media.Add(m);
        var left = TimelineOps.Append(p, m);
        TimelineOps.Append(p, m); // centred (the default)
        var right = TimelineOps.Append(p, m);
        var zoomed = TimelineOps.Append(p, m);
        TimelineOps.SetClipFrameX(p, left.Id, 0);
        TimelineOps.SetClipFrameX(p, right.Id, 1);
        TimelineOps.SetClipFrameX(p, zoomed.Id, 1);
        TimelineOps.SetClipZoom(p, zoomed.Id, 2);
        TimelineOps.SetClipPan(p, zoomed.Id, 0, 0.5); // left half of the right-hand slice: still blue

        var output = Path.Combine(_dir, "fill.mp4");
        await ExportPlan.Build(p, new ExportSettings { Preset = "ultrafast", UseHardwareEncoder = false }, new Dictionary<Guid, string>(), output).RunAsync();
        var probed = await MediaProbe.ProbeAsync(output);
        Assert.Equal((1080, 1920), (probed.Width, probed.Height)); // exactly the vertical shape, nothing more

        static bool IsRed((int R, int G, int B) c) => c.R > 200 && c.G < 60 && c.B < 60;
        static bool IsBlue((int R, int G, int B) c) => c.B > 200 && c.R < 60 && c.G < 60;
        foreach (var y in new[] { 20, 960, 1900 }) // full height: no bars top or bottom
        {
            Assert.True(IsRed(await Pixel(output, 0.5, 30, y)), $"left slice is all red (left edge, y={y})");
            Assert.True(IsRed(await Pixel(output, 0.5, 1050, y)), $"left slice is all red (right edge, y={y})");
        }
        Assert.True(IsRed(await Pixel(output, 1.5, 100, 960)), "centred slice: red on the left");
        Assert.True(IsBlue(await Pixel(output, 1.5, 980, 960)), "centred slice: blue on the right");
        Assert.True(IsBlue(await Pixel(output, 2.5, 30, 960)), "right slice is all blue");
        Assert.True(IsBlue(await Pixel(output, 2.5, 1050, 960)), "right slice is all blue");
        Assert.True(IsBlue(await Pixel(output, 3.5, 30, 960)), "zoom works inside the slice");

        // Save frame takes the same slice.
        var png = Path.Combine(_dir, "fill-frame.png");
        await FfmpegRunner.RunFfmpegAsync(FrameExport.BuildArgs(p, FrameExport.LocateFrame(p, S(0.5))!.Value, 1080, 1920, png));
        Assert.True(IsRed(await Pixel(png, 0, 1050, 960)), "Save frame shows the left slice too");
    }
}
