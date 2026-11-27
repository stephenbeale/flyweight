using Flyweight.Core.Editing;
using Flyweight.Core.Export;
using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

/// <summary>
/// Owner (2026-11-26): "there should be an option to link or unlink clips. also to delete or ripple delete."
/// Del leaves a gap (nothing moves), Shift+Del ripple deletes (tracks stay in sync), Unlink/Link moves a clip's own
/// sound to the audio track and back. Plus (2026-11-25) "extend time of images on the timeline and this should push
/// all tracks along".
/// </summary>
public class DeleteGapLinkAndStillLengthTests
{
    static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    static SourceMedia Video(string name, double secs) => new()
    {
        OriginalPath = name, Duration = S(secs), Fps = 30, Width = 1920, Height = 1080, HasAudio = true,
    };

    static SourceMedia Music(double secs) => new() { OriginalPath = "music.mp3", Duration = S(secs), IsAudioOnly = true, HasAudio = true };

    static SourceMedia Still() => new()
    {
        OriginalPath = "photo.png", IsStill = true, Duration = SourceMedia.StillMaxDuration, Fps = 30, Width = 1920, Height = 1080,
    };

    static Annotation Anno(Project p, Guid clipId, TimeSpan start)
    {
        var a = new Annotation { ClipId = clipId, Kind = AnnotationKind.Box, Start = start, End = start + S(3) };
        p.Annotations.Add(a);
        return a;
    }

    // ---------------- Delete = leave a gap ----------------

    [Fact]
    public void Lift_leaves_a_gap_and_nothing_else_moves()
    {
        var p = new Project();
        var a = TimelineOps.Append(p, Video("a.mp4", 10));     // 0–10
        var b = TimelineOps.Append(p, Video("b.mp4", 10));     // 10–20
        var music = TimelineOps.AddAudio(p, Music(30), S(0));
        var cam = TimelineOps.AddOverlay(p, Video("cam.mp4", 3), S(12));
        Anno(p, a.Id, S(1));

        TimelineOps.Lift(p, a.Id);

        Assert.Equal(2, p.Timeline.Count);
        Assert.True(p.Timeline[0].IsGap);
        Assert.Equal(S(10), p.Timeline[0].Duration);
        Assert.Equal(S(10), TimelineOps.ClipStart(p, b.Id));   // b stays put
        Assert.Equal(S(20), TimelineOps.TotalDuration(p));
        Assert.Equal((S(0), S(30)), (music.Start, music.Duration));
        Assert.Equal(S(12), cam.Start);
        Assert.Empty(p.Annotations);                           // its annotations go with it
    }

    [Fact]
    public void Lifting_the_last_clip_leaves_no_gap_at_the_end()
    {
        var p = new Project();
        TimelineOps.Append(p, Video("a.mp4", 10));
        var b = TimelineOps.Append(p, Video("b.mp4", 10));
        TimelineOps.Lift(p, b.Id);
        Assert.Single(p.Timeline);
        Assert.Equal(S(10), TimelineOps.TotalDuration(p));
    }

    [Fact]
    public void Neighbouring_gaps_join_up_and_deleting_a_gap_closes_it_with_tracks_in_sync()
    {
        var p = new Project();
        var a = TimelineOps.Append(p, Video("a.mp4", 10));
        var b = TimelineOps.Append(p, Video("b.mp4", 5));
        var c = TimelineOps.Append(p, Video("c.mp4", 10));     // 15–25
        var cue = TimelineOps.AddAudio(p, Music(3), S(16));

        TimelineOps.Lift(p, a.Id);
        TimelineOps.Lift(p, b.Id);

        Assert.Equal(2, p.Timeline.Count);                     // one 15 s gap, then c
        var gap = p.Timeline[0];
        Assert.True(gap.IsGap);
        Assert.Equal(S(15), gap.Duration);

        TimelineOps.Lift(p, gap.Id);                           // Del on a gap closes it (ripple)
        Assert.Single(p.Timeline);
        Assert.Equal(c.Id, p.Timeline[0].Id);
        Assert.Equal(S(1), cue.Start);                         // the audio track closed the same gap
    }

    [Fact]
    public void Ripple_delete_still_cuts_the_audio_track_to_keep_it_in_sync()
    {
        var p = new Project();
        var a = TimelineOps.Append(p, Video("a.mp4", 10));
        TimelineOps.Append(p, Video("b.mp4", 10));
        var cue = TimelineOps.AddAudio(p, Music(3), S(12));
        TimelineOps.Delete(p, a.Id);
        Assert.Equal(S(2), cue.Start);
    }

    [Fact]
    public void A_gap_can_be_trimmed_split_and_given_a_length_but_not_a_speed()
    {
        var p = new Project();
        var a = TimelineOps.Append(p, Video("a.mp4", 10));
        TimelineOps.Append(p, Video("b.mp4", 10));
        TimelineOps.Lift(p, a.Id);
        var gap = p.Timeline[0];

        TimelineOps.TrimOut(p, gap.Id, S(30));                 // gaps have no file length to stop at
        Assert.Equal(S(30), gap.Duration);
        TimelineOps.SetStillLength(p, gap.Id, S(4));
        Assert.Equal(S(4), gap.Duration);
        TimelineOps.SetClipSpeed(p, gap.Id, 2);
        Assert.Equal(1, gap.Speed);
        Assert.NotNull(TimelineOps.SplitAt(p, S(2)));
        Assert.True(p.Timeline[1].IsGap);
    }

    [Fact]
    public void Annotations_on_a_merged_gap_keep_their_timeline_time()
    {
        var p = new Project();
        var a = TimelineOps.Append(p, Video("a.mp4", 10));
        var b = TimelineOps.Append(p, Video("b.mp4", 10));
        TimelineOps.Append(p, Video("c.mp4", 10));
        TimelineOps.Lift(p, b.Id);                             // gap 10–20
        var title = Anno(p, p.Timeline[1].Id, S(2)); // at 12 s on the gap
        TimelineOps.Lift(p, a.Id);                             // gap 0–10 joins it: one 20 s gap

        Assert.Equal(S(12), TimelineOps.AnnotationRange(p, title)!.Value.Start);
    }

    [Fact]
    public void Gap_survives_save_and_load()
    {
        var p = new Project();
        var a = TimelineOps.Append(p, Video("a.mp4", 10));
        TimelineOps.Append(p, Video("b.mp4", 10));
        TimelineOps.Lift(p, a.Id);
        var back = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(p));
        Assert.True(back.Timeline[0].IsGap);
        Assert.Equal(S(10), back.Timeline[0].Duration);
    }

    [Fact]
    public void Export_renders_a_gap_as_black_and_silence_without_an_input()
    {
        var p = new Project();
        var a = TimelineOps.Append(p, Video("a.mp4", 10));
        TimelineOps.Append(p, Video("b.mp4", 10));
        TimelineOps.Lift(p, a.Id);
        var plan = ExportPlan.Build(p, new ExportSettings(), new Dictionary<Guid, string>(), "out.mp4");

        Assert.Contains("color=c=black:s=1920x1080", plan.FilterGraph);
        Assert.Contains("[0:v]", plan.FilterGraph);            // b is input 0: the gap took none
        Assert.DoesNotContain("[1:v]", plan.FilterGraph);
        Assert.Equal(S(20), plan.Duration);
    }

    [Fact]
    public void Dragging_a_clip_past_the_end_places_it_there_with_a_gap_and_nothing_else_moves()
    {
        var p = new Project();
        var a = TimelineOps.Append(p, Video("a.mp4", 10));     // 0–10
        var b = TimelineOps.Append(p, Video("b.mp4", 10));     // 10–20
        var c = TimelineOps.Append(p, Video("c.mp4", 10));     // 20–30
        var cue = TimelineOps.AddAudio(p, Music(3), S(12));
        var note = Anno(p, b.Id, S(1));

        Assert.Equal(S(30), TimelineOps.EndWithout(p, b.Id));
        TimelineOps.MoveToTime(p, b.Id, S(40));                // b: from 10 to 40

        Assert.Equal(S(10), TimelineOps.ClipStart(p, c.Id) - S(10)); // c still at 20
        Assert.Equal(S(20), TimelineOps.ClipStart(p, c.Id));
        Assert.Equal(S(40), TimelineOps.ClipStart(p, b.Id));
        Assert.Equal(S(50), TimelineOps.TotalDuration(p));
        Assert.True(p.Timeline[1].IsGap);                      // where b was
        Assert.True(p.Timeline[3].IsGap);                      // 30–40
        Assert.Equal(S(12), cue.Start);                        // the audio track doesn't move
        Assert.Equal(b.Id, note.ClipId);                       // b's annotation goes with it
        Assert.Equal(S(0), TimelineOps.ClipStart(p, a.Id));
    }

    [Fact]
    public void Dragging_the_last_clip_further_right_just_adds_space_before_it()
    {
        var p = new Project();
        TimelineOps.Append(p, Video("a.mp4", 10));
        var b = TimelineOps.Append(p, Video("b.mp4", 10));     // 10–20
        TimelineOps.MoveToTime(p, b.Id, S(15));
        Assert.Equal(3, p.Timeline.Count);                     // a, 5 s gap, b
        Assert.Equal(S(5), p.Timeline[1].Duration);
        Assert.Equal(S(15), TimelineOps.ClipStart(p, b.Id));
        TimelineOps.MoveToTime(p, b.Id, S(25));                // further still: the gaps join up
        Assert.Equal(3, p.Timeline.Count);
        Assert.Equal(S(15), p.Timeline[1].Duration);
    }

    // ---------------- Link / unlink a clip's sound ----------------

    [Fact]
    public void Unlink_then_link_puts_the_sound_back_on_the_clip()
    {
        var p = new Project();
        var a = TimelineOps.Append(p, Video("a.mp4", 10));
        TimelineOps.SetClipVolume(p, a.Id, 0.5);
        var sound = TimelineOps.SeparateAudio(p, a.Id)!;       // Unlink
        Assert.True(a.Muted);
        Assert.Equal(sound.Id, Assert.Single(TimelineOps.UnlinkedSoundOf(p, a.Id)).Id);
        Assert.Equal(a.Id, TimelineOps.LinkedClipOf(p, sound.Id)?.Id);
        Assert.NotNull(TimelineOps.CannotSeparateAudio(p, a.Id)); // already unlinked

        Assert.Equal(1, TimelineOps.LinkAudio(p, a.Id));
        Assert.False(a.Muted);
        Assert.Equal(0.5, a.Volume);
        Assert.Empty(p.AudioTrack);
    }

    [Fact]
    public void Sound_moved_out_of_sync_cannot_be_linked()
    {
        var p = new Project();
        var a = TimelineOps.Append(p, Video("a.mp4", 10));
        var sound = TimelineOps.SeparateAudio(p, a.Id)!;
        TimelineOps.MoveAudio(p, sound.Id, S(2));
        Assert.NotNull(TimelineOps.CannotLinkAudio(p, a.Id));
        Assert.Equal(0, TimelineOps.LinkAudio(p, a.Id));
        Assert.True(a.Muted);
    }

    [Fact]
    public void Linking_keeps_the_part_of_the_sound_outside_the_clip()
    {
        var p2 = new Project();
        var v = TimelineOps.Append(p2, Video("v.mp4", 10));
        TimelineOps.SeparateAudio(p2, v.Id);                   // 0–10
        TimelineOps.TrimOut(p2, v.Id, S(6));                   // picture 0–6; sound still 0–10 and in sync
        Assert.Equal(1, TimelineOps.LinkAudio(p2, v.Id));
        var rest = Assert.Single(p2.AudioTrack);               // 6–10 stays on the audio track
        Assert.Equal((S(6), S(6), S(10)), (rest.Start, rest.In, rest.Out));
    }

    [Fact]
    public void Lift_after_unlink_keeps_the_sound_where_it_was()
    {
        var p = new Project();
        var a = TimelineOps.Append(p, Video("a.mp4", 10));
        TimelineOps.Append(p, Video("b.mp4", 10));
        var sound = TimelineOps.SeparateAudio(p, a.Id)!;
        TimelineOps.Lift(p, a.Id);
        Assert.Equal((S(0), S(10)), (sound.Start, sound.Duration)); // the picture is gone; its sound plays over the gap
        Assert.Single(p.AudioTrack);
    }

    // ---------------- Still length ----------------

    [Fact]
    public void Lengthening_a_still_pushes_later_clips_overlays_and_audio_along()
    {
        var p = new Project();
        var img = TimelineOps.Append(p, Still());              // 0–8 (default)
        TimelineOps.Append(p, Video("b.mp4", 10));             // 8–18
        var cam = TimelineOps.AddOverlay(p, Video("cam.mp4", 3), S(10));
        var cue = TimelineOps.AddAudio(p, Music(3), S(10));

        TimelineOps.SetStillLength(p, img.Id, S(20));

        Assert.Equal(S(20), img.Duration);
        Assert.Equal(S(20), TimelineOps.ClipStart(p, 1));
        Assert.Equal(S(22), cam.Start);
        Assert.Equal(S(22), cue.Start);

        TimelineOps.SetStillLength(p, img.Id, S(5));          // and shortening pulls them back in
        Assert.Equal(S(7), cam.Start);
        Assert.Equal(S(7), cue.Start);
    }

    [Fact]
    public void Still_length_is_clamped_and_videos_are_ignored()
    {
        var p = new Project();
        var img = TimelineOps.Append(p, Still());
        var vid = TimelineOps.Append(p, Video("b.mp4", 10));
        TimelineOps.SetStillLength(p, img.Id, TimeSpan.FromHours(2));
        Assert.Equal(SourceMedia.StillMaxDuration, img.Duration);
        TimelineOps.SetStillLength(p, vid.Id, S(3));
        Assert.Equal(S(10), vid.Duration);
    }
}
