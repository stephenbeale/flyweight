using Flyweight.Core.Editing;
using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

public class CopyPasteClipTests
{
    static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    static (Project p, SourceMedia m) OneFile()
    {
        var p = new Project();
        var m = new SourceMedia { OriginalPath = "a.mp4", Duration = S(30), Fps = 30, Width = 1920, Height = 1080, HasAudio = true };
        p.Media.Add(m);
        TimelineOps.Append(p, m);
        return (p, m);
    }

    [Fact]
    public void Split_copy_paste_inserts_a_copy_after_the_current_clip()
    {
        var (p, _) = OneFile();
        var right = TimelineOps.SplitAt(p, S(10))!;             // [0–10][10–30]
        var left = p.Timeline[0];
        TimelineOps.SetClipVolume(p, left.Id, 0.5);
        TimelineOps.SetClipSpeed(p, left.Id, 2);

        var copy = TimelineOps.CopyClips(p, [left.Id])!;
        var pasted = TimelineOps.PasteClips(p, copy, 1);         // after the left half
        Assert.Single(pasted);
        Assert.Equal([left.Id, pasted[0].Id, right.Id], p.Timeline.Select(c => c.Id));
        var c = pasted[0];
        Assert.NotEqual(left.Id, c.Id);
        Assert.Equal((S(0), S(10), 0.5, 2.0), (c.In, c.Out, c.Volume, c.Speed)); // same footage and settings
        Assert.Equal(S(5 + 5 + 20), TimelineOps.TotalDuration(p));
    }

    [Fact]
    public void Annotations_come_along_as_independent_copies()
    {
        var (p, _) = OneFile();
        var box = TimelineOps.CreateAnnotation(p, S(2), AnnotationKind.Box, 0, 0, .5f, .5f)!;
        var copy = TimelineOps.CopyClips(p, [p.Timeline[0].Id])!;
        var pasted = TimelineOps.PasteClips(p, copy, 1)[0];

        var onCopy = p.Annotations.Single(a => a.ClipId == pasted.Id);
        Assert.NotEqual(box.Id, onCopy.Id);
        Assert.Equal((box.Start, box.End, box.Kind), (onCopy.Start, onCopy.End, onCopy.Kind));
        onCopy.Text = "changed";
        Assert.NotEqual("changed", box.Text); // editing the copy doesn't touch the original
    }

    [Fact]
    public void A_run_across_copied_clips_stays_linked_but_separate_from_the_original()
    {
        var (p, _) = OneFile();
        TimelineOps.SplitAt(p, S(10));
        var box = TimelineOps.CreateAnnotation(p, S(8), AnnotationKind.Box, 0, 0, .5f, .5f)!;
        TimelineOps.ExtendAnnotationEnd(p, box.Id, S(14));      // runs across the cut: 2 linked pieces
        var original = box.LinkId!.Value;

        var copy = TimelineOps.CopyClips(p, p.Timeline.Select(c => c.Id))!;
        var pasted = TimelineOps.PasteClips(p, copy, 2);
        var pieces = p.Annotations.Where(a => pasted.Any(c => c.Id == a.ClipId)).ToList();
        Assert.Equal(2, pieces.Count);
        Assert.NotNull(pieces[0].LinkId);
        Assert.Equal(pieces[0].LinkId, pieces[1].LinkId);
        Assert.NotEqual(original, pieces[0].LinkId);

        // Copying only one half of the run leaves a plain annotation on the copy.
        var half = TimelineOps.PasteClips(p, TimelineOps.CopyClips(p, [p.Timeline[0].Id])!, 0)[0];
        Assert.Null(p.Annotations.Single(a => a.ClipId == half.Id).LinkId);
    }

    [Fact]
    public void Copy_survives_later_edits_and_skips_removed_media()
    {
        var (p, m) = OneFile();
        var clip = p.Timeline[0];
        var copy = TimelineOps.CopyClips(p, [clip.Id])!;
        TimelineOps.TrimOut(p, clip.Id, S(5));                  // editing the original after copying…
        Assert.Equal(S(30), TimelineOps.PasteClips(p, copy, 1)[0].Out); // …doesn't change what's pasted

        TimelineOps.RemoveMedia(p, m.Id);
        Assert.Empty(TimelineOps.PasteClips(p, copy, 0));      // the file's gone from the project
        Assert.Null(TimelineOps.CopyClips(p, []));
    }
}
