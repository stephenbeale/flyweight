using Flyweight.Core.Editing;
using Flyweight.Core.Export;
using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

public class YouTubeChaptersTests
{
    static TimeSpan S(double s) => TimeSpan.FromSeconds(s);
    static string Lines(params string[] l) => string.Join(Environment.NewLine, l);

    /// <summary>Two clips: A is 60 s, B is 120 s → 3 min timeline.</summary>
    static Project TwoClips()
    {
        var p = new Project();
        var a = new SourceMedia { OriginalPath = "a.mp4", Duration = S(60), Fps = 30 };
        var b = new SourceMedia { OriginalPath = "b.mp4", Duration = S(120), Fps = 30 };
        p.Media.AddRange(new[] { a, b });
        TimelineOps.Append(p, a);
        TimelineOps.Append(p, b);
        return p;
    }

    [Fact]
    public void Formats_youtube_timestamps_and_adds_0_00_intro()
    {
        var p = TwoClips();
        MarkerOps.Add(p, S(15.9), "Setup");
        MarkerOps.Add(p, S(83.2), "Results");
        var c = YouTubeChapters.Build(p);
        Assert.Equal(Lines("0:00 Intro", "0:15 Setup", "1:23 Results"), c.Text);
        Assert.True(c.YouTubeWillShowChapters);
        Assert.Empty(c.Warnings);
    }

    [Fact]
    public void Marker_at_zero_is_kept_and_no_intro_is_added()
    {
        var p = TwoClips();
        MarkerOps.Add(p, S(0), "Welcome");
        MarkerOps.Add(p, S(30), "Part 1");
        MarkerOps.Add(p, S(90), "Part 2");
        Assert.Equal(Lines("0:00 Welcome", "0:30 Part 1", "1:30 Part 2"), YouTubeChapters.Build(p).Text);
    }

    [Fact]
    public void Uses_hours_for_videos_an_hour_or_longer()
    {
        var p = new Project();
        var m = new SourceMedia { OriginalPath = "long.mp4", Duration = TimeSpan.FromMinutes(75) };
        p.Media.Add(m);
        TimelineOps.Append(p, m);
        MarkerOps.Add(p, TimeSpan.FromMinutes(10), "Ten");
        MarkerOps.Add(p, new TimeSpan(1, 2, 3), "Late");
        Assert.Equal(Lines("0:00:00 Intro", "0:10:00 Ten", "1:02:03 Late"), YouTubeChapters.Build(p).Text);
    }

    [Fact]
    public void Warns_when_youtube_would_ignore_the_chapters()
    {
        var p = TwoClips();
        MarkerOps.Add(p, S(5), "Too soon");    // Intro would be 5 s
        var c = YouTubeChapters.Build(p);
        Assert.False(c.YouTubeWillShowChapters);
        Assert.Contains(c.Warnings, w => w.Contains("at least 3 chapters"));
        Assert.Contains(c.Warnings, w => w.Contains("\"Intro\" is only 5s"));

        MarkerOps.Add(p, S(175), "Outro");      // last chapter 5 s before the end
        Assert.Contains(YouTubeChapters.Build(p).Warnings, w => w.Contains("\"Outro\" is only 5s"));
    }

    [Fact]
    public void No_markers_gives_empty_text_and_a_hint()
    {
        var c = YouTubeChapters.Build(TwoClips());
        Assert.Equal("", c.Text);
        Assert.False(c.YouTubeWillShowChapters);
        Assert.Contains("Press C", c.Warnings[0]); // C = chapter (M is mute)
    }

    [Fact]
    public void Markers_follow_their_clip_when_reordered_and_vanish_when_trimmed_out_or_deleted()
    {
        var p = TwoClips();
        var inB = MarkerOps.Add(p, S(90), "In B")!;   // source 30 s of clip B
        Assert.Equal(S(90), MarkerOps.TimelineTime(p, inB));

        TimelineOps.Move(p, p.Timeline[1].Id, 0);    // B first
        Assert.Equal(S(30), MarkerOps.TimelineTime(p, inB));

        TimelineOps.TrimIn(p, p.Timeline[0].Id, S(40)); // marker's moment trimmed off
        Assert.Null(MarkerOps.TimelineTime(p, inB));
        Assert.Empty(MarkerOps.Visible(p));

        TimelineOps.Delete(p, p.Timeline[0].Id);
        Assert.Empty(p.Markers);
    }

    [Fact]
    public void Split_moves_markers_after_the_cut_to_the_right_half()
    {
        var p = TwoClips();
        var early = MarkerOps.Add(p, S(10), "Early")!;
        var late = MarkerOps.Add(p, S(50), "Late")!;
        var right = TimelineOps.SplitAt(p, S(30))!;
        Assert.Equal(p.Timeline[0].Id, early.ClipId);
        Assert.Equal(right.Id, late.ClipId);
        Assert.Equal(S(50), MarkerOps.TimelineTime(p, late));
    }

    [Fact]
    public void Move_reanchors_and_default_titles_count_up()
    {
        var p = TwoClips();
        var m = MarkerOps.Add(p, S(10))!;
        Assert.Equal("Chapter 1", m.Title);
        Assert.Equal("Chapter 2", MarkerOps.Add(p, S(20))!.Title);
        MarkerOps.MoveTo(p, m.Id, S(100));
        Assert.Equal(p.Timeline[1].Id, m.ClipId);
        Assert.Equal(S(100), MarkerOps.TimelineTime(p, m));
    }

    [Fact]
    public void Markers_round_trip_and_old_projects_load_without_them()
    {
        var p = TwoClips();
        MarkerOps.Add(p, S(15), "Setup");
        var copy = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(p));
        Assert.Equal("Setup", copy.Markers.Single().Title);

        var old = """{"Media":[],"Timeline":[],"Annotations":[]}""";
        Assert.Empty(ProjectSerializer.Deserialize(old).Markers);
    }

    [Fact]
    public void Chapters_dialog_saves_beside_the_project()
    {
        var proj = Path.Combine(Path.GetTempPath(), "Holiday.flyweight");
        Assert.Equal(Path.Combine(Path.GetTempPath(), "Holiday chapters.txt"), YouTubeChapters.DefaultTextPath(proj));
    }

    [Fact]
    public void Markers_on_a_sped_up_clip_sit_at_their_footage()
    {
        var p = TwoClips();
        var clip = p.Timeline[0];
        var m = MarkerOps.Add(p, S(8), "Point")!;           // at 8 s of footage
        TimelineOps.SetClipSpeed(p, clip.Id, 2);
        Assert.Equal(S(8), m.SourceTime);                   // still on the same footage…
        Assert.Equal(S(4), MarkerOps.TimelineTime(p, m));   // …which now plays 4 s in
    }
}
