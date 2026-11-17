using Flyweight.Core.Editing;
using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

public class TimelineOpsTests
{
    static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    static (Project p, SourceMedia a, SourceMedia b) TwoClips()
    {
        var p = new Project();
        var a = new SourceMedia { OriginalPath = "a.mp4", Duration = S(10), Fps = 30 };
        var b = new SourceMedia { OriginalPath = "b.mp4", Duration = S(5), Fps = 30 };
        p.Media.AddRange(new[] { a, b });
        TimelineOps.Append(p, a);
        TimelineOps.Append(p, b);
        return (p, a, b);
    }

    [Fact]
    public void Locate_maps_timeline_time_to_source_time()
    {
        var (p, _, b) = TwoClips();
        var hit = TimelineOps.Locate(p, S(12))!.Value;
        Assert.Equal(1, hit.Index);
        Assert.Equal(b.Id, hit.Clip.SourceId);
        Assert.Equal(S(2), hit.SourceTime);
        Assert.Null(TimelineOps.Locate(p, S(15)));
    }

    [Fact]
    public void Split_creates_two_clips_with_same_total_duration()
    {
        var (p, _, _) = TwoClips();
        var right = TimelineOps.SplitAt(p, S(4));
        Assert.NotNull(right);
        Assert.Equal(3, p.Timeline.Count);
        Assert.Equal(S(4), p.Timeline[0].Out);
        Assert.Equal(S(4), p.Timeline[1].In);
        Assert.Equal(S(15), TimelineOps.TotalDuration(p));
    }

    [Fact]
    public void Split_at_edge_is_rejected()
    {
        var (p, _, _) = TwoClips();
        Assert.Null(TimelineOps.SplitAt(p, S(0.01)));
        Assert.Equal(2, p.Timeline.Count);
    }

    [Fact]
    public void Split_moves_and_duplicates_annotations()
    {
        var (p, _, _) = TwoClips();
        var before = TimelineOps.CreateAnnotation(p, S(1), AnnotationKind.Box, 0, 0, .5f, .5f)!;   // 1–4s
        var straddle = TimelineOps.CreateAnnotation(p, S(3), AnnotationKind.Arrow, 0, 0, 1, 1)!;  // 3–6s
        var after = TimelineOps.CreateAnnotation(p, S(6), AnnotationKind.Text, .1f, .1f, 0, 0)!;  // 6–9s

        var right = TimelineOps.SplitAt(p, S(5))!;
        var left = p.Timeline[0];

        Assert.Equal(left.Id, before.ClipId);
        Assert.Equal(left.Id, straddle.ClipId);
        Assert.Equal(right.Id, after.ClipId);
        Assert.Single(p.Annotations, a => a.ClipId == right.Id && a.Kind == AnnotationKind.Arrow);

        // Straddling arrow is visible on both sides of the cut.
        Assert.Contains(TimelineOps.VisibleAt(p, S(4.5)), a => a.Kind == AnnotationKind.Arrow);
        Assert.Contains(TimelineOps.VisibleAt(p, S(5.5)), a => a.Kind == AnnotationKind.Arrow);
    }

    [Fact]
    public void Annotations_follow_their_clip_when_reordered()
    {
        var (p, _, _) = TwoClips();
        var a = TimelineOps.CreateAnnotation(p, S(11), AnnotationKind.Box, 0, 0, 1, 1)!; // in clip b, source 1–4s
        Assert.Equal((S(11), S(14)), TimelineOps.AnnotationRange(p, a));

        TimelineOps.Move(p, p.Timeline[1].Id, 0); // b first
        Assert.Equal((S(1), S(4)), TimelineOps.AnnotationRange(p, a));
    }

    [Fact]
    public void Trimming_clamps_annotation_visibility_but_keeps_it_on_the_footage()
    {
        var (p, _, _) = TwoClips();
        var a = TimelineOps.CreateAnnotation(p, S(2), AnnotationKind.Box, 0, 0, 1, 1)!; // source 2–5s
        TimelineOps.TrimIn(p, p.Timeline[0].Id, S(3));
        // Clip now starts at source 3s, so annotation shows from timeline 0 to 2s (source 3–5).
        Assert.Equal((S(0), S(2)), TimelineOps.AnnotationRange(p, a));
    }

    [Fact]
    public void Trim_is_clamped_to_source_and_min_length()
    {
        var (p, _, _) = TwoClips();
        var c = p.Timeline[0];
        TimelineOps.TrimOut(p, c.Id, S(99));
        Assert.Equal(S(10), c.Out);
        TimelineOps.TrimIn(p, c.Id, S(10));
        Assert.Equal(c.Out - TimelineOps.MinClipLength, c.In);
    }

    [Fact]
    public void Delete_ripples_and_removes_annotations()
    {
        var (p, _, _) = TwoClips();
        TimelineOps.CreateAnnotation(p, S(1), AnnotationKind.Box, 0, 0, 1, 1);
        TimelineOps.Delete(p, p.Timeline[0].Id);
        Assert.Single(p.Timeline);
        Assert.Empty(p.Annotations);
        Assert.Equal(S(5), TimelineOps.TotalDuration(p));
    }

    [Fact]
    public void Retime_annotation_clamps_to_clip()
    {
        var (p, _, _) = TwoClips();
        var a = TimelineOps.CreateAnnotation(p, S(1), AnnotationKind.Box, 0, 0, 1, 1)!;
        TimelineOps.RetimeAnnotation(p, a.Id, S(-5), S(50));
        Assert.Equal(S(0), a.Start);
        Assert.Equal(S(10), a.End);
    }
}

public class ProjectSessionTests
{
    [Fact]
    public void Undo_and_redo_restore_snapshots()
    {
        var s = new ProjectSession();
        var m = new SourceMedia { OriginalPath = "a.mp4", Duration = TimeSpan.FromSeconds(10) };
        s.Execute(p => { p.Media.Add(m); TimelineOps.Append(p, m); });
        s.Execute(p => TimelineOps.SplitAt(p, TimeSpan.FromSeconds(5)));
        Assert.Equal(2, s.Project.Timeline.Count);

        s.Undo();
        Assert.Single(s.Project.Timeline);
        s.Redo();
        Assert.Equal(2, s.Project.Timeline.Count);
    }

    [Fact]
    public void A_drag_is_one_undo_step_and_no_op_edits_add_none()
    {
        var s = new ProjectSession();
        var m = new SourceMedia { OriginalPath = "a.mp4", Duration = TimeSpan.FromSeconds(10) };
        s.Execute(p => { p.Media.Add(m); TimelineOps.Append(p, m); });
        var id = s.Project.Timeline[0].Id;

        s.BeginEdit();
        for (int i = 1; i <= 5; i++) s.Preview(p => TimelineOps.TrimIn(p, id, TimeSpan.FromSeconds(i)));
        s.CommitEdit();

        s.Execute(_ => { }); // no change → no undo entry
        s.Undo();
        Assert.Equal(TimeSpan.Zero, s.Project.Timeline[0].In);
        s.Undo();
        Assert.Empty(s.Project.Timeline);
        Assert.False(s.CanUndo);
    }

    [Fact]
    public void Project_round_trips_through_json()
    {
        var p = new Project();
        var m = new SourceMedia { OriginalPath = "a.mp4", Duration = TimeSpan.FromSeconds(10), Fps = 29.97 };
        p.Media.Add(m);
        TimelineOps.Append(p, m);
        TimelineOps.CreateAnnotation(p, TimeSpan.FromSeconds(1), AnnotationKind.Text, .1f, .2f, 0, 0);
        var copy = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(p));
        Assert.Equal(ProjectSerializer.Serialize(p), ProjectSerializer.Serialize(copy));
        Assert.Contains("\"Text\"", ProjectSerializer.Serialize(p)); // enums stored as names
    }
}
