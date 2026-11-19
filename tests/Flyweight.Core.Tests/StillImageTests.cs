using Flyweight.Core.Editing;
using Flyweight.Core.Export;
using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

public class StillImageTests
{
    static SourceMedia Still() => new()
    {
        OriginalPath = "photo.jpg", IsStill = true, Duration = SourceMedia.StillMaxDuration, Fps = 30, Width = 4032, Height = 3024,
    };

    [Fact]
    public void Still_goes_on_the_timeline_for_8_seconds()
    {
        var p = new Project();
        var m = Still();
        p.Media.Add(m);
        Assert.Equal(TimeSpan.FromSeconds(8), TimelineOps.Append(p, m).Duration);
    }

    [Fact]
    public void Still_can_be_trimmed_out_to_the_maximum_hold_but_no_further()
    {
        var p = new Project();
        var m = Still();
        p.Media.Add(m);
        var c = TimelineOps.Append(p, m);
        TimelineOps.TrimOut(p, c.Id, TimeSpan.FromHours(1));
        Assert.Equal(SourceMedia.StillMaxDuration, c.Out);
    }

    [Fact]
    public void Export_loops_the_original_image_instead_of_seeking()
    {
        var p = new Project();
        var m = Still();
        p.Media.Add(m);
        TimelineOps.Append(p, m);
        var plan = ExportPlan.Build(p, new ExportSettings(), new Dictionary<Guid, string>(), "out.mp4");
        var args = string.Join(" ", plan.Arguments);
        Assert.Contains("-loop 1", args);
        Assert.Contains("-t 8 -i photo.jpg", args);
        Assert.DoesNotContain("-ss", args);
    }

    [Fact]
    public void Export_refuses_zero_length_clips_instead_of_running_forever()
    {
        // What images imported before still support look like: a 0 s "video".
        var p = new Project();
        var m = new SourceMedia { OriginalPath = "old.png", Duration = TimeSpan.Zero, Fps = 25 };
        p.Media.Add(m);
        TimelineOps.Append(p, m);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ExportPlan.Build(p, new ExportSettings(), new Dictionary<Guid, string>(), "out.mp4"));
        Assert.Contains("old.png", ex.Message);
    }

    [Theory]
    [InlineData("image2", true)]
    [InlineData("png_pipe", true)]
    [InlineData("jpeg_pipe", true)]
    [InlineData("webp_pipe", true)]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", false)]
    [InlineData("gif", false)]
    [InlineData("matroska,webm", false)]
    public void Detects_image_formats_from_ffprobe_name(string format, bool still) =>
        Assert.Equal(still, MediaProbe.IsStillFormat(format));
}

public class FreezeFrameTests
{
    static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    static (Project p, SourceMedia video, SourceMedia still) Setup()
    {
        var p = new Project();
        var video = new SourceMedia { OriginalPath = "a.mp4", Duration = S(10), Fps = 30 };
        p.Media.Add(video);
        TimelineOps.Append(p, video);
        var still = new SourceMedia { OriginalPath = "frame.png", IsStill = true, Duration = SourceMedia.StillMaxDuration, Fps = 30 };
        return (p, video, still);
    }

    [Fact]
    public void Mid_clip_splits_and_inserts_the_hold_between_the_halves()
    {
        var (p, video, still) = Setup();
        var clip = TimelineOps.InsertFreezeFrame(p, S(4), still, TimelineOps.DefaultFreezeLength)!;

        Assert.Equal(3, p.Timeline.Count);
        Assert.Equal((S(0), S(4)), (p.Timeline[0].In, p.Timeline[0].Out));
        Assert.Same(clip, p.Timeline[1]);
        Assert.Equal(still.Id, clip.SourceId);
        Assert.Equal(S(5), clip.Duration);
        Assert.Equal((S(4), S(10)), (p.Timeline[2].In, p.Timeline[2].Out));
        Assert.Equal(video.Id, p.Timeline[2].SourceId);
        Assert.Equal(S(15), TimelineOps.TotalDuration(p));
        Assert.Contains(still, p.Media);
    }

    [Fact]
    public void At_a_clip_start_inserts_without_splitting()
    {
        var (p, _, still) = Setup();
        TimelineOps.InsertFreezeFrame(p, S(0), still, S(3));
        Assert.Equal(2, p.Timeline.Count);
        Assert.Equal(still.Id, p.Timeline[0].SourceId);
    }

    [Fact]
    public void Annotations_after_the_freeze_stay_on_their_footage()
    {
        var (p, _, still) = Setup();
        var a = TimelineOps.CreateAnnotation(p, S(6), AnnotationKind.Box, 0, 0, .1f, .1f)!;
        TimelineOps.InsertFreezeFrame(p, S(4), still, S(5));
        var range = TimelineOps.AnnotationRange(p, a)!.Value;
        Assert.Equal(S(11), range.Start); // was at 6s; 5s of freeze inserted before it
    }

    [Fact]
    public void Outside_the_timeline_does_nothing()
    {
        var (p, _, still) = Setup();
        Assert.Null(TimelineOps.InsertFreezeFrame(p, S(99), still, S(5)));
        Assert.Single(p.Timeline);
        Assert.DoesNotContain(still, p.Media);
    }
}
