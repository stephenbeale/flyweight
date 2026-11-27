using Flyweight.Core.Editing;
using Flyweight.Core.Export;
using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

public class YouTubeTextTests
{
    static TimeSpan S(double s) => TimeSpan.FromSeconds(s);
    static readonly string NL = Environment.NewLine;

    static Project ThreeMinutes()
    {
        var p = new Project();
        var m = new SourceMedia { OriginalPath = "a.mp4", Duration = S(180), Fps = 30, Width = 1920, Height = 1080 };
        p.Media.Add(m);
        TimelineOps.Append(p, m);
        return p;
    }

    [Fact]
    public void Chapters_from_markers_go_under_the_description()
    {
        var d = new YouTubeDetails { Description = "My review.\n" };
        Assert.Equal($"My review.{NL}{NL}0:00 Intro{NL}1:00 Fit", YouTubeText.FullDescription(d, $"0:00 Intro{NL}1:00 Fit"));
        Assert.Equal("My review.", YouTubeText.FullDescription(d, ""));
        Assert.Equal("0:00 Intro", YouTubeText.FullDescription(new YouTubeDetails(), "0:00 Intro"));
    }

    [Fact]
    public void Problems_flag_what_Studio_would_reject()
    {
        Assert.Empty(YouTubeText.Problems(new YouTubeDetails { Title = "Barbour buyer's guide", Tags = "barbour, wax jacket" }));
        Assert.Contains(YouTubeText.Problems(new YouTubeDetails { Title = "" }), p => p.Contains("empty"));
        Assert.Contains(YouTubeText.Problems(new YouTubeDetails { Title = new string('x', 101) }), p => p.Contains("101"));
        Assert.Contains(YouTubeText.Problems(new YouTubeDetails { Title = "a <b>" }), p => p.Contains('<'));
        var manyTags = string.Join(",", Enumerable.Repeat("abcdefghij", 50)); // 50×10 + 49 commas = 549
        Assert.Contains(YouTubeText.Problems(new YouTubeDetails { Title = "t", Tags = manyTags }), p => p.Contains("549"));
        // The chapters count towards the 5000-character description.
        var d = new YouTubeDetails { Title = "t", Description = new string('x', 4990) };
        Assert.Empty(YouTubeText.Problems(d));
        Assert.Contains(YouTubeText.Problems(d, "0:00 Intro and more"), p => p.Contains("description"));
    }

    [Fact]
    public void Tags_count_like_YouTube_does()
    {
        Assert.Equal(0, YouTubeText.TagsLength(" , "));
        Assert.Equal(3 + 1 + 3, YouTubeText.TagsLength("abc, def"));
        Assert.Equal(11 + 2, YouTubeText.TagsLength("wax jackets")); // a space means quotes
    }

    [Fact]
    public void Text_file_has_a_section_per_Studio_box_with_marker_chapters()
    {
        var p = ThreeMinutes();
        MarkerOps.Add(p, S(0), "Intro");
        MarkerOps.Add(p, S(60), "Fit");
        MarkerOps.Add(p, S(120), "Verdict");
        var chapters = YouTubeChapters.Build(p);
        var d = new YouTubeDetails { Title = "Barbour guide", Description = "Which jacket?", Tags = "a,  b ,c" };

        var text = YouTubeText.FileText(d, "Barbour guide.mp4", chapters);
        Assert.Contains("===== TITLE =====", text);
        Assert.Contains($"Which jacket?{NL}{NL}0:00 Intro{NL}1:00 Fit{NL}2:00 Verdict", text);
        Assert.Contains("a, b, c", text);
        Assert.DoesNotContain("CHECK BEFORE", text);

        var dir = Directory.CreateTempSubdirectory("fw-yt-").FullName;
        try
        {
            var file = YouTubeText.Save(d, Path.Combine(dir, "Barbour guide.mp4"), chapters);
            Assert.Equal(Path.Combine(dir, "Barbour guide - YouTube.txt"), file);
            Assert.Contains("2:00 Verdict", File.ReadAllText(file));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Chapter_rule_warnings_appear_in_the_file()
    {
        var p = ThreeMinutes();
        MarkerOps.Add(p, S(60), "Only one");
        var text = YouTubeText.FileText(new YouTubeDetails { Title = "t" }, "v.mp4", YouTubeChapters.Build(p));
        Assert.Contains("CHECK BEFORE UPLOADING", text);
        Assert.Contains("at least 3 chapters", text);
    }

    [Fact]
    public void Details_are_saved_with_the_project_and_old_files_load_empty()
    {
        var p = ThreeMinutes();
        p.YouTube = new YouTubeDetails { Title = "T", Description = "D", Tags = "x, y", SaveTextFile = false };
        var back = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(p)).YouTube;
        Assert.Equal(("T", "D", "x, y", false), (back.Title, back.Description, back.Tags, back.SaveTextFile));
        var old = """{"Media":[],"Timeline":[],"Annotations":[]}""";
        var empty = ProjectSerializer.Deserialize(old).YouTube;
        Assert.Equal(("", true), (empty.Title, empty.SaveTextFile));
    }
}
