using Flyweight.Core.Editing;
using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

public class RemoveMediaTests
{
    static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void Removing_media_takes_its_clips_annotations_and_audio_items_with_it()
    {
        var p = new Project();
        var a = new SourceMedia { OriginalPath = "a.mp4", Duration = S(10), Fps = 30 };
        var b = new SourceMedia { OriginalPath = "b.mp4", Duration = S(10), Fps = 30 };
        var music = new SourceMedia { OriginalPath = "m.mp3", Duration = S(60), IsAudioOnly = true, HasAudio = true };
        p.Media.AddRange(new[] { a, b, music });
        TimelineOps.Append(p, a);
        var keep = TimelineOps.Append(p, b);
        TimelineOps.Append(p, a);
        TimelineOps.CreateAnnotation(p, S(1), AnnotationKind.Box, 0, 0, .5f, .5f);   // on the first "a" clip
        var kept = TimelineOps.CreateAnnotation(p, S(12), AnnotationKind.Box, 0, 0, .5f, .5f)!; // on "b"
        TimelineOps.AddAudio(p, music, S(0));

        Assert.Equal(2, TimelineOps.UsageCount(p, a.Id));
        Assert.Equal(1, TimelineOps.UsageCount(p, music.Id));

        TimelineOps.RemoveMedia(p, a.Id);
        Assert.DoesNotContain(a, p.Media);
        Assert.Equal(new[] { keep.Id }, p.Timeline.Select(c => c.Id));
        Assert.Equal(new[] { kept.Id }, p.Annotations.Select(x => x.Id));
        // The music keeps in sync with what's left: the stretches under the removed clips are cut out of it,
        // leaving the part that played under "b" (song 10–20) at the start of the timeline.
        Assert.Equal((S(0), S(10), S(20)), (p.AudioTrack[0].Start, p.AudioTrack[0].In, p.AudioTrack[0].Out));
        Assert.All(p.AudioTrack, x => Assert.Equal(music.Id, x.SourceId));

        TimelineOps.RemoveMedia(p, music.Id);
        Assert.Empty(p.AudioTrack);
        Assert.Equal(new[] { b.Id }, p.Media.Select(m => m.Id));
    }

    [Fact]
    public void Unused_media_is_just_removed()
    {
        var p = new Project();
        var a = new SourceMedia { OriginalPath = "a.mp4", Duration = S(10), Fps = 30 };
        p.Media.Add(a);
        Assert.Equal(0, TimelineOps.UsageCount(p, a.Id));
        TimelineOps.RemoveMedia(p, a.Id);
        Assert.Empty(p.Media);
    }
}
