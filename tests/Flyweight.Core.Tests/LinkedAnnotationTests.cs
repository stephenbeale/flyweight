using Flyweight.Core.Editing;
using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

public class LinkedAnnotationTests
{
    static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    /// <summary>Three 10 s clips; clip b is trimmed to source 4–10 so source time ≠ timeline time.</summary>
    static (Project p, Clip a, Clip b, Clip c) ThreeClips()
    {
        var p = new Project();
        SourceMedia V(string n) => new() { OriginalPath = n, Duration = S(10), Fps = 30, Width = 1920, Height = 1080 };
        var a = TimelineOps.Append(p, V("a.mp4"));
        var b = TimelineOps.Append(p, V("b.mp4"));
        var c = TimelineOps.Append(p, V("c.mp4"));
        b.In = S(4); // a: 0–10, b: 10–16, c: 16–26 on the timeline
        return (p, a, b, c);
    }

    [Fact]
    public void Dragging_the_end_past_a_cut_continues_onto_the_next_clip()
    {
        var (p, a, b, _) = ThreeClips();
        var ann = TimelineOps.CreateAnnotation(p, S(8), AnnotationKind.Arrow, 0.1f, 0.1f, 0.2f, 0.2f)!;

        TimelineOps.ExtendAnnotationEnd(p, ann.Id, S(12));

        var pieces = TimelineOps.LinkedPieces(p, ann);
        Assert.Equal(2, pieces.Count);
        Assert.Equal((a.Id, S(8), S(10)), (pieces[0].ClipId, pieces[0].Start, pieces[0].End));
        Assert.Equal((b.Id, S(4), S(6)), (pieces[1].ClipId, pieces[1].Start, pieces[1].End)); // b's source time
        Assert.NotNull(ann.LinkId);
    }

    [Fact]
    public void Dragging_far_spans_several_clips_and_dragging_back_removes_the_pieces()
    {
        var (p, _, _, c) = ThreeClips();
        var ann = TimelineOps.CreateAnnotation(p, S(8), AnnotationKind.Box, 0.1f, 0.1f, 0.2f, 0.2f)!;

        TimelineOps.ExtendAnnotationEnd(p, ann.Id, S(20));
        Assert.Equal(3, TimelineOps.LinkedPieces(p, ann).Count);
        Assert.Equal(S(4), TimelineOps.LinkedPieces(p, ann)[2].End - TimelineOps.LinkedPieces(p, ann)[2].Start);
        Assert.Equal(c.Id, TimelineOps.LinkedPieces(p, ann)[2].ClipId);

        TimelineOps.ExtendAnnotationEnd(p, ann.Id, S(9)); // back inside its own clip
        Assert.Single(p.Annotations);
        Assert.Null(ann.LinkId);
        Assert.Equal(S(9), ann.End);
    }

    [Fact]
    public void Dragging_the_start_before_a_cut_continues_onto_the_previous_clip()
    {
        var (p, a, b, _) = ThreeClips();
        var ann = TimelineOps.CreateAnnotation(p, S(11), AnnotationKind.Arrow, 0.1f, 0.1f, 0.2f, 0.2f)!; // on b
        Assert.Equal(b.Id, ann.ClipId);

        TimelineOps.ExtendAnnotationStart(p, ann.Id, S(7));

        var pieces = TimelineOps.LinkedPieces(p, ann);
        Assert.Equal(2, pieces.Count);
        Assert.Equal((a.Id, S(7), S(10)), (pieces[0].ClipId, pieces[0].Start, pieces[0].End));
        Assert.Equal((b.Id, S(4)), (pieces[1].ClipId, pieces[1].Start)); // b now starts at its In point
    }

    [Fact]
    public void Style_and_position_changes_sync_across_the_run_but_times_do_not()
    {
        var (p, _, _, _) = ThreeClips();
        var ann = TimelineOps.CreateAnnotation(p, S(8), AnnotationKind.Arrow, 0.1f, 0.1f, 0.2f, 0.2f)!;
        TimelineOps.ExtendAnnotationEnd(p, ann.Id, S(12));
        var other = TimelineOps.LinkedPieces(p, ann)[1];
        var otherTimes = (other.Start, other.End);

        ann.StrokeColor = "#FF00FF00";
        ann.X2 = 0.9f;
        ann.End = S(9.5);
        TimelineOps.SyncLinked(p, ann.Id);

        Assert.Equal("#FF00FF00", other.StrokeColor);
        Assert.Equal(0.9f, other.X2);
        Assert.Equal(otherTimes, (other.Start, other.End));
    }

    [Fact]
    public void Deleting_one_piece_deletes_the_whole_run()
    {
        var (p, _, _, _) = ThreeClips();
        var ann = TimelineOps.CreateAnnotation(p, S(8), AnnotationKind.Arrow, 0.1f, 0.1f, 0.2f, 0.2f)!;
        var keep = TimelineOps.CreateAnnotation(p, S(1), AnnotationKind.Box, 0.1f, 0.1f, 0.2f, 0.2f)!;
        TimelineOps.ExtendAnnotationEnd(p, ann.Id, S(20));

        TimelineOps.DeleteAnnotation(p, TimelineOps.LinkedPieces(p, ann)[1].Id);

        Assert.Equal(new[] { keep.Id }, p.Annotations.Select(x => x.Id));
    }

    [Fact]
    public void Split_links_the_two_halves_of_a_straddling_annotation()
    {
        var (p, _, _, _) = ThreeClips();
        var ann = TimelineOps.CreateAnnotation(p, S(2), AnnotationKind.Arrow, 0.1f, 0.1f, 0.2f, 0.2f)!; // 2–5 s

        TimelineOps.SplitAt(p, S(3));

        Assert.Equal(2, TimelineOps.LinkedPieces(p, ann).Count);
    }

    [Fact]
    public void Moving_a_run_moves_all_of_it_with_the_same_length_and_look()
    {
        var (p, _, _, c) = ThreeClips();
        var ann = TimelineOps.CreateAnnotation(p, S(8), AnnotationKind.Arrow, 0.1f, 0.1f, 0.2f, 0.2f)!;
        ann.StrokeColor = "#FF00FF00";
        TimelineOps.ExtendAnnotationEnd(p, ann.Id, S(12)); // run 8–12 across a|b

        TimelineOps.MoveAnnotation(p, ann.Id, S(14), S(4)); // run 14–18 across b|c

        var run = TimelineOps.RunRange(p, ann)!.Value;
        Assert.Equal((S(14), S(18)), (run.Start, run.End));
        var pieces = TimelineOps.LinkedPieces(p, ann);
        Assert.Equal(2, pieces.Count);
        Assert.Equal(c.Id, pieces[1].ClipId);
        Assert.All(pieces, x => Assert.Equal("#FF00FF00", x.StrokeColor));
        Assert.Equal(2, p.Annotations.Count); // nothing left behind
    }

    [Fact]
    public void Dragging_the_start_edge_of_a_run_keeps_its_end()
    {
        var (p, a, _, _) = ThreeClips();
        var ann = TimelineOps.CreateAnnotation(p, S(8), AnnotationKind.Arrow, 0.1f, 0.1f, 0.2f, 0.2f)!;
        TimelineOps.ExtendAnnotationEnd(p, ann.Id, S(20)); // 8–20

        TimelineOps.SetRunRange(p, ann.Id, S(3), S(20));

        var run = TimelineOps.RunRange(p, ann)!.Value;
        Assert.Equal((S(3), S(20)), (run.Start, run.End));
        Assert.Equal(a.Id, ann.ClipId);
        Assert.Equal(3, TimelineOps.LinkedPieces(p, ann).Count);
    }
}
