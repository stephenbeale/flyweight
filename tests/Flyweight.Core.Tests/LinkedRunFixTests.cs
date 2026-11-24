using Flyweight.Core.Editing;
using Flyweight.Core.Export;
using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

/// <summary>Regression tests for the 2026-11-24 review: linked annotation runs must only span neighbouring clips.</summary>
public class LinkedRunFixTests
{
    static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    /// <summary>One 30 s clip with a box from 5 s to 25 s, split at 10 s and 20 s → one run over three clips.</summary>
    static (Project p, Annotation box) RunOverThreeClips()
    {
        var p = new Project();
        var m = new SourceMedia { OriginalPath = "talk.mp4", Duration = S(30), Fps = 30 };
        p.Media.Add(m);
        TimelineOps.Append(p, m);
        var box = TimelineOps.CreateAnnotation(p, S(5), AnnotationKind.Box, 0, 0, .5f, .5f)!;
        box.End = S(25);
        TimelineOps.SplitAt(p, S(10));
        TimelineOps.SplitAt(p, S(20));
        return (p, box);
    }

    static SourceMedia Still() => new() { OriginalPath = "frame.png", IsStill = true, Duration = SourceMedia.StillMaxDuration, Fps = 30 };

    [Fact]
    public void Setup_really_is_one_linked_run()
    {
        var (p, box) = RunOverThreeClips();
        Assert.Equal(3, TimelineOps.LinkedPieces(p, box).Count);
    }

    [Fact]
    public void Freeze_frame_copies_are_independent_of_the_original_run()
    {
        var (p, box) = RunOverThreeClips();
        var freeze = TimelineOps.InsertFreezeFrame(p, S(15), Still(), S(5), copyAnnotations: true)!;
        var copy = p.Annotations.Single(a => a.ClipId == freeze.Id);
        Assert.Null(copy.LinkId);

        TimelineOps.DeleteAnnotation(p, copy.Id);
        Assert.Contains(p.Annotations, a => a.Id == box.Id); // the original survived
    }

    [Fact]
    public void Inserting_media_inside_an_annotation_does_not_bridge_the_new_clip()
    {
        var p = new Project();
        var m = new SourceMedia { OriginalPath = "talk.mp4", Duration = S(20), Fps = 30 };
        var other = new SourceMedia { OriginalPath = "broll.mp4", Duration = S(10), Fps = 30 };
        p.Media.AddRange(new[] { m, other });
        TimelineOps.Append(p, m);
        var box = TimelineOps.CreateAnnotation(p, S(2), AnnotationKind.Box, 0, 0, 1, 1)!;
        box.End = S(8);

        var inserted = TimelineOps.InsertMediaAt(p, other, S(5));

        Assert.All(p.Annotations, a => Assert.Null(a.LinkId));
        Assert.DoesNotContain(p.Annotations, a => a.ClipId == inserted.Id);
        Assert.Single(TimelineOps.LinkedPieces(p, box));
    }

    [Fact]
    public void Freeze_inside_an_annotation_does_not_bridge_the_freeze_clip()
    {
        var p = new Project();
        var m = new SourceMedia { OriginalPath = "talk.mp4", Duration = S(20), Fps = 30 };
        p.Media.Add(m);
        TimelineOps.Append(p, m);
        var box = TimelineOps.CreateAnnotation(p, S(2), AnnotationKind.Box, 0, 0, 1, 1)!;
        box.End = S(8);

        TimelineOps.InsertFreezeFrame(p, S(5), Still(), S(3)); // no copies
        Assert.All(p.Annotations, a => Assert.Null(a.LinkId));
    }

    [Fact]
    public void Deleting_the_middle_clip_of_a_run_unlinks_the_ends()
    {
        var (p, _) = RunOverThreeClips();
        TimelineOps.Delete(p, p.Timeline[1].Id);
        Assert.Equal(2, p.Annotations.Count);
        Assert.All(p.Annotations, a => Assert.Null(a.LinkId));
    }

    [Fact]
    public void Moving_a_clip_out_of_a_run_splits_it_but_keeps_neighbours_linked()
    {
        var (p, box) = RunOverThreeClips();
        var last = p.Timeline[2];
        TimelineOps.Move(p, last.Id, 0); // order now: third, first, second
        // first+second are still neighbours (one run); the moved piece is on its own.
        Assert.Null(p.Annotations.Single(a => a.ClipId == last.Id).LinkId);
        Assert.Equal(2, TimelineOps.LinkedPieces(p, box).Count);
    }

    [Fact]
    public void Deleting_a_middle_piece_still_works_after_normalising()
    {
        var (p, box) = RunOverThreeClips();
        TimelineOps.NormaliseLinks(p); // no-op on a healthy run
        Assert.Equal(3, TimelineOps.LinkedPieces(p, box).Count);
    }

    [Fact]
    public void Cpu_retry_only_when_it_could_help()
    {
        Assert.False(ExportPlan.CpuRetryCouldHelp(new FfmpegCommandTooLongException()));
        Assert.False(ExportPlan.CpuRetryCouldHelp(new FfmpegException("failed", "D:\\clip.mp4: No such file or directory")));
        Assert.True(ExportPlan.CpuRetryCouldHelp(new FfmpegException("failed", "Error initializing output stream: h264_qsv")));
    }

    [Fact]
    public void Marker_rename_and_delete_go_through_MarkerOps()
    {
        var (p, _) = RunOverThreeClips();
        var m = MarkerOps.Add(p, S(12), "Old")!;
        MarkerOps.Rename(p, m.Id, "  New  ");
        Assert.Equal("New", m.Title);
        MarkerOps.Rename(p, m.Id, "   ");
        Assert.Equal("New", m.Title); // blank titles ignored
        MarkerOps.Delete(p, m.Id);
        Assert.Empty(p.Markers);
    }
}
