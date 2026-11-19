using Flyweight.Core.Editing;
using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

public class InsertAndUpgradeTests
{
    static SourceMedia Video(string name, double secs) => new()
    {
        OriginalPath = name, Duration = TimeSpan.FromSeconds(secs), Fps = 30, Width = 1920, Height = 1080, HasAudio = true,
    };

    [Fact]
    public void InsertMedia_puts_the_clip_at_the_index_and_ripples_the_rest()
    {
        var p = new Project();
        var a = Video("a.mp4", 10);
        var b = Video("b.mp4", 20);
        var c = Video("c.mp4", 5);
        TimelineOps.Append(p, a);
        TimelineOps.Append(p, b);

        var inserted = TimelineOps.InsertMedia(p, c, 1);

        Assert.Equal(new[] { a.Id, c.Id, b.Id }, p.Timeline.Select(x => x.SourceId));
        Assert.Equal(TimeSpan.FromSeconds(5), inserted.Duration);
        Assert.Equal(TimeSpan.FromSeconds(15), TimelineOps.ClipStart(p, 2));
        Assert.Contains(c, p.Media);
    }

    [Theory]
    [InlineData(-3, 0)]
    [InlineData(99, 1)]
    public void InsertMedia_clamps_the_index(int index, int expected)
    {
        var p = new Project();
        TimelineOps.Append(p, Video("a.mp4", 10));
        var clip = TimelineOps.InsertMedia(p, Video("b.mp4", 3), index);
        Assert.Equal(expected, p.Timeline.IndexOf(clip));
    }

    [Fact]
    public void Old_image_imported_as_a_tiny_video_is_upgraded_to_a_still()
    {
        var p = new Project();
        var img = Video("C:/pics/photo.JPG", 0.04);
        img.HasAudio = false;
        p.Media.Add(img);
        p.Timeline.Add(new Clip { SourceId = img.Id, In = TimeSpan.Zero, Out = TimeSpan.FromSeconds(0.04) });

        Assert.True(TimelineOps.UpgradeLegacyStills(p));

        Assert.True(img.IsStill);
        Assert.Equal(SourceMedia.StillMaxDuration, img.Duration);
        Assert.Equal(TimelineOps.DefaultStillLength, p.Timeline[0].Duration);
        Assert.False(TimelineOps.UpgradeLegacyStills(p)); // idempotent
    }

    [Fact]
    public void Real_videos_and_gifs_are_left_alone()
    {
        var p = new Project();
        var vid = Video("clip.mp4", 0.04);
        var gif = Video("anim.gif", 0.04);
        p.Media.Add(vid);
        p.Media.Add(gif);

        Assert.False(TimelineOps.UpgradeLegacyStills(p));
        Assert.False(vid.IsStill);
        Assert.False(gif.IsStill);
    }

    [Fact]
    public void Loading_an_old_project_upgrades_it_and_marks_it_dirty()
    {
        var p = new Project();
        p.Media.Add(Video("shot.png", 0.04));
        var session = new ProjectSession();
        session.Load(ProjectSerializer.Deserialize(ProjectSerializer.Serialize(p)), "old.flyweight");
        Assert.True(session.Project.Media[0].IsStill);
        Assert.True(session.IsDirty);
    }

    [Fact]
    public void Dragging_an_annotation_onto_the_next_clip_reanchors_it()
    {
        var p = new Project();
        var a = TimelineOps.Append(p, Video("a.mp4", 10));
        var b = TimelineOps.Append(p, Video("b.mp4", 10));
        b.In = TimeSpan.FromSeconds(4); // b plays source 4–10 s, from timeline 10 s
        var ann = TimelineOps.CreateAnnotation(p, TimeSpan.FromSeconds(2), AnnotationKind.Arrow, 0.1f, 0.1f, 0.2f, 0.2f)!;
        Assert.Equal(a.Id, ann.ClipId);

        TimelineOps.MoveAnnotation(p, ann.Id, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(3));

        Assert.Equal(b.Id, ann.ClipId);
        Assert.Equal(TimeSpan.FromSeconds(6), ann.Start); // 2 s into b = source 4 + 2
        Assert.Equal(TimeSpan.FromSeconds(9), ann.End);
    }

    [Fact]
    public void Moving_an_annotation_keeps_its_length_and_slides_against_the_clip_end()
    {
        var p = new Project();
        TimelineOps.Append(p, Video("a.mp4", 10));
        var ann = TimelineOps.CreateAnnotation(p, TimeSpan.FromSeconds(1), AnnotationKind.Box, 0.1f, 0.1f, 0.2f, 0.2f)!;

        TimelineOps.MoveAnnotation(p, ann.Id, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromSeconds(5), ann.Start);
        Assert.Equal(TimeSpan.FromSeconds(10), ann.End);
    }

    [Fact]
    public void Moving_across_a_cut_keeps_the_length_as_one_linked_run()
    {
        var p = new Project();
        var a = TimelineOps.Append(p, Video("a.mp4", 10));
        var b = TimelineOps.Append(p, Video("b.mp4", 10));
        var ann = TimelineOps.CreateAnnotation(p, TimeSpan.FromSeconds(1), AnnotationKind.Arrow, 0.1f, 0.1f, 0.2f, 0.2f)!;
        var len = TimeSpan.FromSeconds(4);

        TimelineOps.MoveAnnotation(p, ann.Id, TimeSpan.FromSeconds(7.5), len);

        Assert.Equal(a.Id, ann.ClipId);
        Assert.Equal(TimeSpan.FromSeconds(7.5), ann.Start);
        var run = TimelineOps.RunRange(p, ann)!.Value;
        Assert.Equal((TimeSpan.FromSeconds(7.5), TimeSpan.FromSeconds(11.5)), (run.Start, run.End));
        Assert.Contains(TimelineOps.LinkedPieces(p, ann), x => x.ClipId == b.Id);

        TimelineOps.MoveAnnotation(p, ann.Id, TimeSpan.FromSeconds(2), len); // back inside a: one piece again
        Assert.Single(p.Annotations);
        Assert.Null(ann.LinkId);
    }

    [Fact]
    public void Freeze_frame_can_copy_the_annotations_on_screen_for_its_whole_length()
    {
        var p = new Project();
        TimelineOps.Append(p, Video("a.mp4", 10));
        var shown = TimelineOps.CreateAnnotation(p, TimeSpan.FromSeconds(3), AnnotationKind.Arrow, 0.1f, 0.2f, 0.3f, 0.4f)!;
        TimelineOps.CreateAnnotation(p, TimeSpan.FromSeconds(8), AnnotationKind.Box, 0.5f, 0.5f, 0.6f, 0.6f); // not on screen at 4 s
        var still = new SourceMedia { OriginalPath = "f.png", IsStill = true, Duration = SourceMedia.StillMaxDuration, Fps = 30 };

        var clip = TimelineOps.InsertFreezeFrame(p, TimeSpan.FromSeconds(4), still, TimeSpan.FromSeconds(5), copyAnnotations: true)!;

        var copy = Assert.Single(p.Annotations, x => x.ClipId == clip.Id);
        Assert.NotEqual(shown.Id, copy.Id);
        Assert.Equal(AnnotationKind.Arrow, copy.Kind);
        Assert.Equal((0.1f, 0.2f), (copy.X1, copy.Y1));
        Assert.Equal(TimeSpan.Zero, copy.Start);
        Assert.Equal(TimeSpan.FromSeconds(5), copy.End);
    }

    [Fact]
    public void Freeze_frame_without_copy_adds_no_annotations()
    {
        var p = new Project();
        TimelineOps.Append(p, Video("a.mp4", 10));
        TimelineOps.CreateAnnotation(p, TimeSpan.FromSeconds(3), AnnotationKind.Arrow, 0.1f, 0.2f, 0.3f, 0.4f);
        var still = new SourceMedia { OriginalPath = "f.png", IsStill = true, Duration = SourceMedia.StillMaxDuration, Fps = 30 };

        var clip = TimelineOps.InsertFreezeFrame(p, TimeSpan.FromSeconds(4), still, TimeSpan.FromSeconds(5))!;

        Assert.DoesNotContain(p.Annotations, x => x.ClipId == clip.Id);
    }

    [Fact]
    public void Clip_jumps_step_through_cuts()
    {
        var p = new Project();
        TimelineOps.Append(p, Video("a.mp4", 10));
        TimelineOps.Append(p, Video("b.mp4", 5));
        var s = TimeSpan.FromSeconds;

        Assert.Equal(s(10), TimelineOps.NextClipBoundary(p, s(3)));
        Assert.Equal(s(15), TimelineOps.NextClipBoundary(p, s(10)));   // on a cut: go to the next one
        Assert.Equal(s(15), TimelineOps.NextClipBoundary(p, s(15)));   // at the end: stay
        Assert.Equal(s(10), TimelineOps.PreviousClipBoundary(p, s(12))); // inside b: b's start
        Assert.Equal(s(0), TimelineOps.PreviousClipBoundary(p, s(10)));  // on b's start: a's start
        Assert.Equal(s(0), TimelineOps.PreviousClipBoundary(p, s(0)));
    }

    [Fact]
    public void IsImageFile_matches_common_photo_extensions_only()
    {
        Assert.True(MediaProbe.IsImageFile("a.jpeg"));
        Assert.True(MediaProbe.IsImageFile("B.PNG"));
        Assert.False(MediaProbe.IsImageFile("c.gif"));
        Assert.False(MediaProbe.IsImageFile("d.mov"));
    }
}
