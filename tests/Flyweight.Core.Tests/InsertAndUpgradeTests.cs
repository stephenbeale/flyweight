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
    public void IsImageFile_matches_common_photo_extensions_only()
    {
        Assert.True(MediaProbe.IsImageFile("a.jpeg"));
        Assert.True(MediaProbe.IsImageFile("B.PNG"));
        Assert.False(MediaProbe.IsImageFile("c.gif"));
        Assert.False(MediaProbe.IsImageFile("d.mov"));
    }
}
