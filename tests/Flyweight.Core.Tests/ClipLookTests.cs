using Flyweight.Core.Editing;
using Flyweight.Core.Export;
using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

public class ClipLookTests
{
    static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    static (Project p, Clip clip) OneClip()
    {
        var p = new Project();
        var m = new SourceMedia { OriginalPath = "a.mp4", Duration = S(10), Fps = 30, Width = 1920, Height = 1080 };
        p.Media.Add(m);
        return (p, TimelineOps.Append(p, m));
    }

    static string Graph(Project p) =>
        ExportPlan.Build(p, new ExportSettings(), new Dictionary<Guid, string>(), "out.mp4").FilterGraph;

    [Fact]
    public void New_clips_show_the_whole_frame_as_shot()
    {
        var (p, c) = OneClip();
        Assert.True(ClipLook.IsDefault(c));
        Assert.Equal((0.0, 0.0, 1.0, 1.0), ClipLook.ViewRect(c));
        var g = Graph(p);
        Assert.DoesNotContain("crop=", g);
        Assert.DoesNotContain("lutrgb", g);
    }

    [Fact]
    public void Old_project_files_load_with_no_zoom_or_tone()
    {
        var (p, c) = OneClip();
        var node = System.Text.Json.Nodes.JsonNode.Parse(ProjectSerializer.Serialize(p))!;
        var clipNode = node["Timeline"]![0]!.AsObject();
        foreach (var key in new[] { "Zoom", "PanX", "PanY", "Brightness", "Contrast" }) clipNode.Remove(key);
        var json = node.ToJsonString();
        Assert.DoesNotContain("Zoom", json);
        var loaded = ProjectSerializer.Deserialize(json).Timeline[0];
        Assert.True(ClipLook.IsDefault(loaded));
        Assert.Equal(0.5, loaded.PanX);
    }

    [Fact]
    public void Look_survives_save_and_load()
    {
        var (p, c) = OneClip();
        TimelineOps.SetClipZoom(p, c.Id, 2);
        TimelineOps.SetClipPan(p, c.Id, 0.25, 1);
        TimelineOps.SetClipBrightness(p, c.Id, 0.4);
        TimelineOps.SetClipContrast(p, c.Id, -0.2);
        var l = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(p)).Timeline[0];
        Assert.Equal((2.0, 0.25, 1.0, 0.4, -0.2), (l.Zoom, l.PanX, l.PanY, l.Brightness, l.Contrast));
    }

    [Theory]
    [InlineData(2, 0.5, 0.5, 0.25, 0.25, 0.5)]  // centred: middle half
    [InlineData(2, 0, 0, 0, 0, 0.5)]            // top-left quarter
    [InlineData(2, 1, 1, 0.5, 0.5, 0.5)]        // bottom-right quarter
    [InlineData(4, 1, 0, 0.75, 0, 0.25)]
    [InlineData(1, 1, 1, 0, 0, 1)]              // no zoom: pan does nothing
    public void ViewRect_is_the_zoomed_part_of_the_canvas(double zoom, double panX, double panY, double x, double y, double size)
    {
        var c = new Clip { Zoom = zoom, PanX = panX, PanY = panY };
        var r = ClipLook.ViewRect(c);
        Assert.Equal(x, r.X, 6);
        Assert.Equal(y, r.Y, 6);
        Assert.Equal(size, r.W, 6);
        Assert.Equal(size, r.H, 6);
    }

    [Theory]
    [InlineData(0.5, 1)]
    [InlineData(9, 4)]
    [InlineData(double.NaN, 1)]
    [InlineData(2.5, 2.5)]
    public void SetClipZoom_clamps_to_100_to_400_percent(double input, double expected)
    {
        var (p, c) = OneClip();
        TimelineOps.SetClipZoom(p, c.Id, input);
        Assert.Equal(expected, c.Zoom);
    }

    [Fact]
    public void Setters_clamp_pan_brightness_and_contrast()
    {
        var (p, c) = OneClip();
        TimelineOps.SetClipPan(p, c.Id, -3, null);
        Assert.Equal((0.0, 0.5), (c.PanX, c.PanY)); // null leaves Y alone
        TimelineOps.SetClipPan(p, c.Id, null, 7);
        Assert.Equal((0.0, 1.0), (c.PanX, c.PanY));
        TimelineOps.SetClipBrightness(p, c.Id, 5);
        TimelineOps.SetClipContrast(p, c.Id, double.NaN);
        Assert.Equal((1.0, 0.0), (c.Brightness, c.Contrast));
    }

    [Fact]
    public void Reset_returns_to_the_whole_frame_as_shot()
    {
        var (p, c) = OneClip();
        TimelineOps.SetClipZoom(p, c.Id, 3);
        TimelineOps.SetClipPan(p, c.Id, 0, 0);
        TimelineOps.SetClipBrightness(p, c.Id, 0.5);
        TimelineOps.ResetClipLook(p, c.Id);
        Assert.True(ClipLook.IsDefault(c));
        Assert.Equal((0.5, 0.5), (c.PanX, c.PanY));
    }

    [Fact]
    public void Neutral_tone_curve_is_the_identity()
    {
        var t = ClipLook.ToneTable(new Clip());
        for (int v = 0; v < 256; v++) Assert.Equal(v, t[v]);
    }

    [Fact]
    public void Brightening_lifts_dark_and_mid_tones_but_keeps_black_and_white()
    {
        var t = ClipLook.ToneTable(new Clip { Brightness = 0.5 });
        Assert.Equal(0, t[0]);
        Assert.Equal(255, t[255]);
        Assert.True(t[40] > 70, $"dark grey should lift a lot, got {t[40]}");
        Assert.True(t[128] > 160, $"mid grey should lift, got {t[128]}");
        for (int v = 1; v < 256; v++) Assert.True(t[v] >= t[v - 1], "curve must never reverse");

        var dark = ClipLook.ToneTable(new Clip { Brightness = -0.5 });
        Assert.True(dark[128] < 100);
    }

    [Fact]
    public void Contrast_spreads_or_squeezes_around_mid_grey()
    {
        var more = ClipLook.ToneTable(new Clip { Contrast = 1 });
        Assert.InRange(more[128], 127, 129);
        Assert.True(more[64] < 20 && more[192] > 235);
        var less = ClipLook.ToneTable(new Clip { Contrast = -1 });
        Assert.InRange(less[0], 60, 70);
        Assert.InRange(less[255], 185, 195);
    }

    [Fact]
    public void Export_graph_zoom_crops_the_original_before_scaling_and_applies_the_same_curve()
    {
        var (p, c) = OneClip();
        TimelineOps.SetClipZoom(p, c.Id, 2);
        TimelineOps.SetClipPan(p, c.Id, 1, 0);
        TimelineOps.SetClipBrightness(p, c.Id, 0.5);
        var g = Graph(p);
        Assert.Contains("crop=w=iw*0.5:h=ih*0.5:x=iw*0.5:y=ih*0", g);
        Assert.True(g.IndexOf("crop=", StringComparison.Ordinal) < g.IndexOf("scale=1920:1080", StringComparison.Ordinal), "crop before scale");
        Assert.DoesNotContain("force_original_aspect_ratio", g); // the zoom chain replaces the fit
        // Same rounded constants as the preview's table.
        Assert.Contains($"pow(val/255,{FfmpegRunner.Num(ClipLook.Gamma(0.5))})", g);
        Assert.Contains("lutrgb=r=", g);
    }

    [Fact]
    public void Split_halves_and_freeze_frames_keep_the_look()
    {
        var (p, c) = OneClip();
        TimelineOps.SetClipZoom(p, c.Id, 2);
        TimelineOps.SetClipPan(p, c.Id, 0.1, 0.9);
        TimelineOps.SetClipBrightness(p, c.Id, 0.3);
        var right = TimelineOps.SplitAt(p, S(4))!;
        Assert.Equal((2.0, 0.1, 0.9, 0.3), (right.Zoom, right.PanX, right.PanY, right.Brightness));

        var still = new SourceMedia { OriginalPath = "f.png", IsStill = true, Duration = SourceMedia.StillMaxDuration, Width = 1920, Height = 1080 };
        var frozen = TimelineOps.InsertFreezeFrame(p, S(2), still, S(3))!;
        Assert.Equal((2.0, 0.1, 0.9, 0.3), (frozen.Zoom, frozen.PanX, frozen.PanY, frozen.Brightness));
    }
}
