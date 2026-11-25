using Flyweight.Core.Editing;
using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

/// <summary>
/// Overlays and the audio track stay with their footage for every ripple edit (2026-11-25 review), not just delete and
/// trim: insert at playhead, freeze frame, paste and speed changes too.
/// </summary>
public class TrackSyncTests
{
    static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    static SourceMedia Video(string name, double secs) => new()
    {
        OriginalPath = name, Duration = S(secs), Fps = 30, Width = 1920, Height = 1080, HasAudio = true,
    };

    static SourceMedia Music(double secs) => new() { OriginalPath = "music.mp3", Duration = S(secs), IsAudioOnly = true, HasAudio = true };

    /// <summary>A 20 s clip with a webcam overlay at 12–15 s and a music cue at 12 s.</summary>
    static (Project p, OverlayClip cam, AudioItem cue) Setup()
    {
        var p = new Project();
        TimelineOps.Append(p, Video("talk.mp4", 20));
        var cam = TimelineOps.AddOverlay(p, Video("cam.mp4", 3), S(12));
        var cue = TimelineOps.AddAudio(p, Music(3), S(12));
        return (p, cam, cue);
    }

    [Fact]
    public void Insert_at_playhead_moves_later_overlays_and_music_with_the_footage()
    {
        var (p, cam, cue) = Setup();
        TimelineOps.InsertMediaAt(p, Video("broll.mp4", 5), S(4));
        Assert.Equal(S(17), cam.Start);
        Assert.Equal(S(17), cue.Start);
    }

    [Fact]
    public void Freeze_frame_moves_later_overlays_and_music()
    {
        var (p, cam, cue) = Setup();
        var still = new SourceMedia { OriginalPath = "f.png", IsStill = true, Duration = SourceMedia.StillMaxDuration, Fps = 30, Width = 1920, Height = 1080 };
        TimelineOps.InsertFreezeFrame(p, S(4), still, S(2));
        Assert.Equal(S(14), cam.Start);
        Assert.Equal(S(14), cue.Start);
    }

    [Fact]
    public void Paste_moves_later_overlays_and_music()
    {
        var (p, cam, cue) = Setup();
        var b = Video("b.mp4", 6);
        var bc = TimelineOps.Append(p, b);                 // 20–26 s
        var copy = TimelineOps.CopyClips(p, new[] { bc.Id })!;
        TimelineOps.PasteClips(p, copy, 0);                // 6 s at the very start
        Assert.Equal(S(18), cam.Start);
        Assert.Equal(S(18), cue.Start);
    }

    [Fact]
    public void Speeding_up_a_clip_pulls_later_items_in_and_slowing_it_pushes_them_out()
    {
        var p = new Project();
        var first = TimelineOps.Append(p, Video("a.mp4", 10));
        TimelineOps.Append(p, Video("b.mp4", 10));
        var cam = TimelineOps.AddOverlay(p, Video("cam.mp4", 3), S(12));  // on the second clip
        var cue = TimelineOps.AddAudio(p, Music(3), S(12));

        TimelineOps.SetClipSpeed(p, first.Id, 2);   // first clip 10 s → 5 s
        Assert.Equal(S(7), cam.Start);
        Assert.Equal(S(7), cue.Start);

        TimelineOps.SetClipSpeed(p, first.Id, 0.5); // 5 s → 20 s
        Assert.Equal(S(22), cam.Start);
        Assert.Equal(S(22), cue.Start);
    }

    [Fact]
    public void Lengthening_a_clip_never_leaves_a_sliver()
    {
        var p = new Project();
        var a = TimelineOps.Append(p, Video("a.mp4", 30));
        TimelineOps.TrimOut(p, a.Id, S(10));
        var cue = TimelineOps.AddAudio(p, Music(20), S(9.95));   // starts 0.05 s before the clip's end
        var snap = TimelineOps.CaptureTracks(p, a.Id)!;

        TimelineOps.TrimOut(p, a.Id, S(15), snap);               // 5 s longer
        Assert.Single(p.AudioTrack);                              // moved whole, not split into a 0.05 s piece
        Assert.Equal(S(14.95), p.AudioTrack[0].Start);
        Assert.All(p.AudioTrack, x => Assert.True(x.Duration >= TimelineOps.MinClipLength));
    }

    [Fact]
    public void Undo_right_after_an_open_edit_undoes_that_edit_and_keeps_history_sane()
    {
        var s = new ProjectSession();
        s.Execute(p => TimelineOps.Append(p, Video("a.mp4", 10)));
        var id = s.Project.Timeline[0].Id;

        s.BeginEdit();                                            // e.g. a wheel-zoom waiting for its timer
        s.Preview(p => TimelineOps.SetClipZoom(p, id, 2));
        s.Undo();                                                 // before the timer commits
        Assert.Equal(1.0, s.Project.Timeline[0].Zoom, 3);
        s.CommitEdit();                                           // the late timer: must be a no-op now
        Assert.True(s.CanRedo);
        s.Redo();
        Assert.Equal(2.0, s.Project.Timeline[0].Zoom, 3);
    }

    [Fact]
    public void Separate_audio_explains_why_a_sped_up_overlay_cannot()
    {
        var (p, cam, _) = Setup();
        Assert.Null(TimelineOps.CannotSeparateOverlayAudio(p, cam.Id));
        TimelineOps.SetOverlaySpeed(p, cam.Id, 1.5);
        Assert.Contains("1×", TimelineOps.CannotSeparateOverlayAudio(p, cam.Id));
        Assert.Null(TimelineOps.SeparateOverlayAudio(p, cam.Id));
    }
}
