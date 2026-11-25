using Flyweight.Core.Editing;
using Flyweight.Core.Export;
using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

/// <summary>D40: the bars around clips that aren't 16:9 filled with a blurred copy of the clip.</summary>
public sealed class BlurFillTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("flyweight-blur-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
    static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    static (Project p, Clip clip) OneClip(int w, int h, BackgroundFill fill = BackgroundFill.Blur)
    {
        var p = new Project { Background = fill };
        var m = new SourceMedia { OriginalPath = "a.mp4", Duration = S(10), Fps = 30, Width = w, Height = h };
        p.Media.Add(m);
        return (p, TimelineOps.Append(p, m));
    }

    static string Graph(Project p) =>
        ExportPlan.Build(p, new ExportSettings(), new Dictionary<Guid, string>(), "out.mp4").FilterGraph;

    // ---------------- When it applies ----------------

    [Fact]
    public void Portrait_and_4x3_clips_get_blurred_bars_16x9_clips_have_none_to_fill()
    {
        var (p, portrait) = OneClip(1080, 1920);
        Assert.True(ClipLook.BlursBars(p, portrait, p.Media[0]));
        var (p43, fourThree) = OneClip(1440, 1080);
        Assert.True(ClipLook.BlursBars(p43, fourThree, p43.Media[0]));
        var (p169, wide) = OneClip(1920, 1080);
        Assert.False(ClipLook.BlursBars(p169, wide, p169.Media[0]));
        Assert.DoesNotContain("gblur", Graph(p169));
    }

    [Fact]
    public void Black_projects_zoomed_and_cropped_clips_keep_black_bars()
    {
        var (black, c0) = OneClip(1080, 1920, BackgroundFill.Black);
        Assert.False(ClipLook.BlursBars(black, c0, black.Media[0]));
        Assert.DoesNotContain("gblur", Graph(black));

        var (p, c) = OneClip(1080, 1920);
        TimelineOps.SetClipZoom(p, c.Id, 2);
        Assert.False(ClipLook.BlursBars(p, c, p.Media[0]));
        Assert.DoesNotContain("gblur", Graph(p));

        var (pc, cc) = OneClip(1080, 1920);
        cc.CropLeft = 0.1;
        Assert.False(ClipLook.BlursBars(pc, cc, pc.Media[0]));
    }

    [Fact]
    public void Each_blurred_clip_gets_its_own_labels_in_the_export_graph()
    {
        var (p, _) = OneClip(1080, 1920);
        TimelineOps.Append(p, p.Media[0]);
        var g = Graph(p);
        Assert.Contains("[bf0b][bf0f]", g);
        Assert.Contains("[bf1b][bf1f]", g);
        Assert.Contains($"scale={ClipLook.BlurWidth}:{ClipLook.BlurHeight}:force_original_aspect_ratio=increase", g);
    }

    // ---------------- The setting ----------------

    [Fact]
    public void New_projects_blur_old_files_stay_black_and_the_setting_saves()
    {
        var session = new ProjectSession();
        session.New();
        Assert.Equal(BackgroundFill.Blur, session.Project.Background);

        var legacy = ProjectSerializer.Serialize(new Project()).Replace("\"Background\": \"Black\",", "");
        Assert.DoesNotContain("Background", legacy);
        Assert.Equal(BackgroundFill.Black, ProjectSerializer.Deserialize(legacy).Background);

        var json = ProjectSerializer.Serialize(new Project { Background = BackgroundFill.Blur });
        Assert.Equal(BackgroundFill.Blur, ProjectSerializer.Deserialize(json).Background);
    }

    [Fact]
    public void Toggling_is_one_undo_step()
    {
        var session = new ProjectSession();
        session.New();
        session.Execute(p => TimelineOps.SetBackgroundFill(p, BackgroundFill.Black));
        Assert.Equal(BackgroundFill.Black, session.Project.Background);
        session.Undo();
        Assert.Equal(BackgroundFill.Blur, session.Project.Background);
    }

    // ---------------- The preview's blur maths ----------------

    [Fact]
    public void Preview_blur_keeps_a_flat_colour_flat_and_dims_it()
    {
        int w = ClipLook.BlurWidth, h = ClipLook.BlurHeight, stride = w * 4;
        var px = new byte[stride * h];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 0; px[i + 1] = 200; px[i + 2] = 100; px[i + 3] = 255; }
        ClipLook.BlurSmall(px, w, h, stride);
        for (int i = 0; i < px.Length; i += 4)
        {
            Assert.Equal(0, px[i]);
            Assert.Equal(120, px[i + 1]);
            Assert.Equal(60, px[i + 2]);
        }
    }

    [Fact]
    public void Preview_blur_softens_a_hard_edge()
    {
        int w = ClipLook.BlurWidth, h = ClipLook.BlurHeight, stride = w * 4;
        var px = new byte[stride * h];
        for (int y = 0; y < h; y++)
            for (int x = w / 2; x < w; x++) px[y * stride + x * 4 + 2] = 255; // right half red
        ClipLook.BlurSmall(px, w, h, stride);
        int Red(int x) => px[(h / 2) * stride + x * 4 + 2];
        Assert.Equal(0, Red(0));
        Assert.InRange(Red(w / 2 - 1), 30, 120); // the edge is now a ramp
        Assert.InRange(Red(w / 2), 30, 120);
        Assert.Equal((int)Math.Round(255 * ClipLook.BlurDim), Red(w - 1));
    }

    // ---------------- Real ffmpeg: export and Save frame ----------------

    static bool FfmpegAvailable()
    {
        try { Task.Run(() => FfmpegRunner.RunFfmpegAsync(new[] { "-version" })).Wait(); return true; }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("FLYWEIGHT_REQUIRE_FFMPEG") == "1") throw new InvalidOperationException("ffmpeg required", ex);
            return false;
        }
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
    public async Task Portrait_clip_exports_and_saves_with_dimmed_blurred_bars()
    {
        if (!FfmpegAvailable()) return;
        // Portrait: top half red, bottom half blue, 360×640.
        var src = Path.Combine(_dir, "portrait.mp4");
        await FfmpegRunner.RunFfmpegAsync(new[]
        {
            "-f", "lavfi", "-i", "color=c=red:s=360x320:r=30:d=1", "-f", "lavfi", "-i", "color=c=blue:s=360x320:r=30:d=1",
            "-filter_complex", "[0:v][1:v]vstack=inputs=2[v]", "-map", "[v]", "-c:v", "libx264", "-pix_fmt", "yuv420p", src,
        });
        var p = new Project { Background = BackgroundFill.Blur };
        var m = await MediaProbe.ProbeAsync(src);
        p.Media.Add(m);
        TimelineOps.Append(p, m);

        var output = Path.Combine(_dir, "blur-out.mp4");
        await ExportPlan.Build(p, new ExportSettings { Preset = "ultrafast", UseHardwareEncoder = false }, new Dictionary<Guid, string>(), output).RunAsync();

        // The picture itself is untouched: red above the middle, blue below, at full strength.
        var pic = await Pixel(output, 0.5, 960, 300);
        Assert.True(pic.R > 200 && pic.B < 60, $"picture top should be red, was {pic}");
        // The bars hold the dimmed blur: red-ish up top, blue-ish below, neither black nor full strength.
        // (The fill is the CENTRE 16:9 slice of the portrait, so its top is red and its bottom blue.)
        var barTop = await Pixel(output, 0.5, 100, 60);
        var barBottom = await Pixel(output, 0.5, 100, 1020);
        Assert.True(barTop.R > 80 && barTop.R < 200 && barTop.B < 80, $"top of the bar should be dim red, was {barTop}");
        Assert.True(barBottom.B > 80 && barBottom.B < 200 && barBottom.R < 80, $"bottom of the bar should be dim blue, was {barBottom}");

        // Save frame takes the same path.
        var hit = FrameExport.LocateFrame(p, S(0.5))!.Value;
        var png = Path.Combine(_dir, "frame.png");
        await FfmpegRunner.RunFfmpegAsync(FrameExport.BuildArgs(p, hit, 1920, 1080, png));
        var frameBar = await Pixel(png, 0, 100, 60);
        Assert.InRange(frameBar.R, barTop.R - 25, barTop.R + 25);
        Assert.True(frameBar.B < 80);

        // Turned off: black bars again.
        p.Background = BackgroundFill.Black;
        var black = Path.Combine(_dir, "black-out.mp4");
        await ExportPlan.Build(p, new ExportSettings { Preset = "ultrafast", UseHardwareEncoder = false }, new Dictionary<Guid, string>(), black).RunAsync();
        var bar = await Pixel(black, 0.5, 100, 60);
        Assert.True(bar.R < 30 && bar.G < 30 && bar.B < 30, $"bar should be black, was {bar}");
    }
}
