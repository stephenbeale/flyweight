using Flyweight.Core.Editing;
using Flyweight.Core.Export;
using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

public class ClipSpeedTests
{
    static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    static (Project p, Clip a, Clip b) TwoClips()
    {
        var p = new Project();
        var m = new SourceMedia { OriginalPath = "a.mp4", Duration = S(20), Fps = 30, Width = 1920, Height = 1080, HasAudio = true };
        p.Media.Add(m);
        var a = TimelineOps.Append(p, m);
        TimelineOps.TrimOut(p, a.Id, S(10));          // a: source 0–10
        var b = TimelineOps.Append(p, m);
        TimelineOps.TrimOut(p, b.Id, S(4));           // b: source 0–4
        return (p, a, b);
    }

    static string Graph(Project p) =>
        ExportPlan.Build(p, new ExportSettings(), new Dictionary<Guid, string>(), "out.mp4").FilterGraph;

    [Fact]
    public void Old_project_files_and_new_clips_play_at_normal_speed()
    {
        var (p, a, _) = TwoClips();
        Assert.Equal(1.0, a.Speed);
        var node = System.Text.Json.Nodes.JsonNode.Parse(ProjectSerializer.Serialize(p))!;
        foreach (var c in node["Timeline"]!.AsArray()) c!.AsObject().Remove("Speed");
        var json = node.ToJsonString();
        Assert.DoesNotContain("\"Speed\"", json);
        Assert.Equal(1.0, ProjectSerializer.Deserialize(json).Timeline[0].Speed);
    }

    [Fact]
    public void Speed_changes_the_length_on_the_timeline_but_not_the_footage_used()
    {
        var (p, a, b) = TwoClips();
        TimelineOps.SetClipSpeed(p, a.Id, 2);
        Assert.Equal(S(10), a.SourceLength);
        Assert.Equal(S(5), a.Duration);
        Assert.Equal(S(9), TimelineOps.TotalDuration(p));   // 5 + 4: the next clip ripples
        Assert.Equal(S(5), TimelineOps.ClipStart(p, b.Id));

        TimelineOps.SetClipSpeed(p, a.Id, 0.5);
        Assert.Equal(S(20), a.Duration);
    }

    [Theory]
    [InlineData(2, 3, 6)]        // 3 s into a 2× clip = 6 s of footage
    [InlineData(0.5, 3, 1.5)]
    [InlineData(4, 2, 8)]
    public void Locate_maps_timeline_time_to_source_time_through_the_speed(double speed, double t, double source)
    {
        var (p, a, _) = TwoClips();
        TimelineOps.SetClipSpeed(p, a.Id, speed);
        var hit = TimelineOps.Locate(p, S(t))!.Value;
        Assert.Equal(a.Id, hit.Clip.Id);
        Assert.Equal(source, hit.SourceTime.TotalSeconds, 6);
    }

    [Fact]
    public void Clip_after_a_fast_clip_starts_where_the_fast_clip_ends()
    {
        var (p, a, b) = TwoClips();
        TimelineOps.SetClipSpeed(p, a.Id, 2);
        var hit = TimelineOps.Locate(p, S(6))!.Value;  // 1 s into b
        Assert.Equal(b.Id, hit.Clip.Id);
        Assert.Equal(1, hit.SourceTime.TotalSeconds, 6);
    }

    [Theory]
    [InlineData(0.1, 0.25)]
    [InlineData(10, 4)]
    [InlineData(double.NaN, 1)]
    [InlineData(1.5, 1.5)]
    public void SetClipSpeed_clamps_to_quarter_to_4x(double input, double expected)
    {
        var (p, a, _) = TwoClips();
        TimelineOps.SetClipSpeed(p, a.Id, input);
        Assert.Equal(expected, a.Speed);
    }

    [Fact]
    public void Stills_have_no_speed()
    {
        var p = new Project();
        var still = new SourceMedia { OriginalPath = "a.png", IsStill = true, Duration = SourceMedia.StillMaxDuration, Width = 800, Height = 600 };
        p.Media.Add(still);
        var c = TimelineOps.Append(p, still);
        TimelineOps.SetClipSpeed(p, c.Id, 2);
        Assert.Equal(1.0, c.Speed);
    }

    [Fact]
    public void Annotations_stay_on_their_footage_when_the_speed_changes()
    {
        var (p, a, _) = TwoClips();
        // Box at source 4–6 s.
        var box = TimelineOps.CreateAnnotation(p, S(4), AnnotationKind.Box, 0, 0, .5f, .5f)!;
        TimelineOps.RetimeAnnotation(p, box.Id, S(4), S(6));
        TimelineOps.SetClipSpeed(p, a.Id, 2);
        Assert.Equal((S(4), S(6)), (box.Start, box.End));                  // same footage
        Assert.Equal((S(2), S(3)), TimelineOps.AnnotationRange(p, box)!.Value); // shown at half the times
        Assert.Contains(box, TimelineOps.VisibleAt(p, S(2.5)));
        Assert.DoesNotContain(box, TimelineOps.VisibleAt(p, S(3.5)));
    }

    [Fact]
    public void Dragging_an_annotation_on_a_fast_clip_uses_timeline_times()
    {
        var (p, a, _) = TwoClips();
        TimelineOps.SetClipSpeed(p, a.Id, 2);
        var box = TimelineOps.CreateAnnotation(p, S(1), AnnotationKind.Box, 0, 0, .5f, .5f)!;
        // New annotations last 3 s ON THE TIMELINE whatever the speed.
        Assert.Equal((S(1), S(4)), TimelineOps.AnnotationRange(p, box)!.Value);
        TimelineOps.RetimeAnnotation(p, box.Id, S(2), S(4));
        Assert.Equal((S(4), S(8)), (box.Start, box.End));
        Assert.Equal((S(2), S(4)), TimelineOps.AnnotationRange(p, box)!.Value);
    }

    [Fact]
    public void A_new_annotation_near_a_cut_still_gets_the_full_default_length()
    {
        var (p, a, b) = TwoClips();                       // a: timeline 0–10, b: 10–14
        var text = TimelineOps.CreateAnnotation(p, S(9), AnnotationKind.Text, .1f, .1f, 0, 0)!;
        // It used to stop at the cut (1 s); now it runs on into b as one linked run.
        Assert.Equal((S(9), S(12)), TimelineOps.RunRange(p, text)!.Value);
        var piece = p.Annotations.Single(x => x.ClipId == b.Id);
        Assert.Equal("Text", piece.Text);
        Assert.Equal(text.LinkId, piece.LinkId);

        // At the very end of the timeline it can only run to the end.
        var last = TimelineOps.CreateAnnotation(p, S(13), AnnotationKind.Box, 0, 0, .5f, .5f)!;
        Assert.Equal((S(13), S(14)), TimelineOps.RunRange(p, last)!.Value);
    }

    [Fact]
    public void An_annotation_run_across_a_cut_into_a_slow_clip_covers_the_right_footage()
    {
        var (p, a, b) = TwoClips();                       // a: 0–10 s on the timeline
        TimelineOps.SetClipSpeed(p, b.Id, 0.5);           // b: 4 s of footage over 8 s, timeline 10–18
        var box = TimelineOps.CreateAnnotation(p, S(8), AnnotationKind.Box, 0, 0, .5f, .5f)!;
        TimelineOps.ExtendAnnotationEnd(p, box.Id, S(14)); // 4 s into b on the timeline = 2 s of b's footage
        var piece = p.Annotations.Single(x => x.ClipId == b.Id);
        Assert.Equal((S(0), S(2)), (piece.Start, piece.End));
        Assert.Equal((S(8), S(14)), TimelineOps.RunRange(p, box)!.Value);
    }

    [Fact]
    public void Split_halves_keep_the_speed_and_cut_at_the_playhead()
    {
        var (p, a, _) = TwoClips();
        TimelineOps.SetClipSpeed(p, a.Id, 2);
        var right = TimelineOps.SplitAt(p, S(2))!;   // 2 s on the timeline = 4 s of footage
        Assert.Equal(S(4), a.Out);
        Assert.Equal(S(4), right.In);
        Assert.Equal(2.0, right.Speed);
        Assert.Equal(S(2), a.Duration);
        Assert.Equal(S(3), right.Duration);
    }

    [Fact]
    public void Speed_survives_save_and_load()
    {
        var (p, a, _) = TwoClips();
        TimelineOps.SetClipSpeed(p, a.Id, 1.5);
        Assert.Equal(1.5, ProjectSerializer.Deserialize(ProjectSerializer.Serialize(p)).Timeline[0].Speed);
    }

    [Fact]
    public void Export_reads_the_footage_then_retimes_video_and_audio_keeping_pitch()
    {
        var (p, a, _) = TwoClips();
        TimelineOps.SetClipSpeed(p, a.Id, 2);
        var plan = ExportPlan.Build(p, new ExportSettings(), new Dictionary<Guid, string>(), "out.mp4");
        int t = plan.Arguments.ToList().IndexOf("-t");
        Assert.Equal("10", plan.Arguments[t + 1]);          // reads 10 s of footage…
        Assert.Contains("setpts=(PTS-STARTPTS)/2", plan.FilterGraph);
        Assert.Contains("atempo=2,", plan.FilterGraph);
        Assert.Contains("trim=duration=5[v0]", plan.FilterGraph); // …which lasts 5 s
        Assert.Equal(S(9), plan.Duration);
    }

    [Theory]
    [InlineData(1, "")]
    [InlineData(2, "atempo=2,")]
    [InlineData(4, "atempo=2,atempo=2,")]
    [InlineData(3, "atempo=2,atempo=1.5,")]
    [InlineData(0.5, "atempo=0.5,")]
    [InlineData(0.25, "atempo=0.5,atempo=0.5,")]
    [InlineData(0.75, "atempo=0.75,")]
    public void Tempo_chains_atempo_stages_within_its_range(double speed, string expected) =>
        Assert.Equal(expected, ExportPlan.Tempo(speed));

    [Fact]
    public void Normal_speed_adds_nothing_to_the_export()
    {
        var (p, _, _) = TwoClips();
        var g = Graph(p);
        Assert.DoesNotContain("atempo", g);
        Assert.DoesNotContain("setpts=(PTS", g);
    }
}
