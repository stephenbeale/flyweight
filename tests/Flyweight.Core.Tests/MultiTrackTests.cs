using Flyweight.Core.Editing;
using Flyweight.Core.Export;
using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

/// <summary>Overlay tracks, separating a clip's audio, and inserting at the playhead.</summary>
public class MultiTrackTests
{
    static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    static SourceMedia Video(string name, double secs, int w = 1920, int h = 1080, bool audio = true) => new()
    {
        OriginalPath = name, Duration = S(secs), Fps = 30, Width = w, Height = h, HasAudio = audio,
    };

    // ---------------- Insert at playhead ----------------

    [Fact]
    public void InsertMediaAt_splits_the_clip_under_the_playhead_and_puts_the_file_between()
    {
        var p = new Project();
        var a = Video("a.mp4", 10);
        var b = Video("b.mp4", 4);
        TimelineOps.Append(p, a);

        var inserted = TimelineOps.InsertMediaAt(p, b, S(6));

        Assert.Equal(new[] { a.Id, b.Id, a.Id }, p.Timeline.Select(c => c.SourceId));
        Assert.Equal(S(6), p.Timeline[0].Out);
        Assert.Equal(S(6), p.Timeline[2].In);
        Assert.Equal(S(6), TimelineOps.ClipStart(p, inserted.Id));
        Assert.Equal(S(14), TimelineOps.TotalDuration(p));
    }

    [Fact]
    public void InsertMediaAt_a_cut_does_not_split()
    {
        var p = new Project();
        TimelineOps.Append(p, Video("a.mp4", 5));
        TimelineOps.Append(p, Video("b.mp4", 5));

        var inserted = TimelineOps.InsertMediaAt(p, Video("c.mp4", 2), S(5));

        Assert.Equal(3, p.Timeline.Count);
        Assert.Equal(1, p.Timeline.IndexOf(inserted));
    }

    [Fact]
    public void InsertMediaAt_the_end_appends()
    {
        var p = new Project();
        TimelineOps.Append(p, Video("a.mp4", 5));
        var inserted = TimelineOps.InsertMediaAt(p, Video("b.mp4", 2), S(5));
        Assert.Equal(1, p.Timeline.IndexOf(inserted));
        Assert.Equal(2, p.Timeline.Count);
    }

    [Fact]
    public void InsertMediaAt_refuses_audio_files()
    {
        var p = new Project();
        TimelineOps.Append(p, Video("a.mp4", 5));
        var mp3 = new SourceMedia { OriginalPath = "x.mp3", Duration = S(3), HasAudio = true, IsAudioOnly = true };
        Assert.Throws<ArgumentException>(() => TimelineOps.InsertMediaAt(p, mp3, S(1)));
    }

    // ---------------- Separate audio ----------------

    [Fact]
    public void SeparateAudio_puts_the_clip_sound_on_the_audio_track_and_mutes_the_clip()
    {
        var p = new Project();
        TimelineOps.Append(p, Video("intro.mp4", 3));
        var a = Video("talk.mp4", 20);
        var clip = TimelineOps.Append(p, a);
        TimelineOps.TrimIn(p, clip.Id, S(2));
        TimelineOps.SetClipVolume(p, clip.Id, 0.7);

        var item = TimelineOps.SeparateAudio(p, clip.Id)!;

        Assert.Equal(a.Id, item.SourceId);
        Assert.Equal(S(3), item.Start);       // where the clip starts on the timeline
        Assert.Equal(S(2), item.In);          // the same footage
        Assert.Equal(S(20), item.Out);
        Assert.Equal(0.7, item.Volume, 3);
        Assert.True(clip.Muted);
        Assert.Contains(item, p.AudioTrack);

        // Independent from now on: rippling the video doesn't move it.
        TimelineOps.Delete(p, p.Timeline[0].Id);
        Assert.Equal(S(3), item.Start);
    }

    [Fact]
    public void SeparateAudio_refuses_silent_stills_and_sped_up_clips()
    {
        var p = new Project();
        var silent = TimelineOps.Append(p, Video("silent.mp4", 5, audio: false));
        var fast = TimelineOps.Append(p, Video("fast.mp4", 5));
        TimelineOps.SetClipSpeed(p, fast.Id, 2);

        Assert.Null(TimelineOps.SeparateAudio(p, silent.Id));
        Assert.Null(TimelineOps.SeparateAudio(p, fast.Id));
        Assert.NotNull(TimelineOps.CannotSeparateAudio(p, fast.Id));
        Assert.Empty(p.AudioTrack);
        Assert.False(fast.Muted);
    }

    [Fact]
    public void Separated_audio_from_a_video_file_exports_from_that_file()
    {
        var p = new Project();
        var a = Video("C:/v/talk.mp4", 10);
        var clip = TimelineOps.Append(p, a);
        TimelineOps.SeparateAudio(p, clip.Id);

        var plan = ExportPlan.Build(p, new ExportSettings { UseHardwareEncoder = false }, new Dictionary<Guid, string>(), "out.mp4");

        Assert.Equal(2, plan.Arguments.Count(x => x == "C:/v/talk.mp4")); // the clip's picture, and its sound on the audio track
        Assert.Contains("anullsrc", plan.FilterGraph);                      // the muted clip itself is silent
        Assert.Contains("amix=inputs=2", plan.FilterGraph);
    }

    // ---------------- Overlays ----------------

    [Fact]
    public void AddOverlay_places_a_picture_in_picture_bottom_right_inside_the_frame()
    {
        var p = new Project();
        TimelineOps.Append(p, Video("main.mp4", 30));
        var cam = Video("cam.mp4", 10);

        var o = TimelineOps.AddOverlay(p, cam, S(4));

        Assert.Contains(o, p.Overlays);
        Assert.Contains(cam, p.Media);
        Assert.Equal((S(4), S(0), S(10), 1), (o.Start, o.In, o.Out, o.Track));
        var (x, y, w, h) = OverlayLayout.Rect(o, cam);
        Assert.Equal(TimelineOps.DefaultOverlayScale, w, 3);
        Assert.Equal(w, h, 3); // 16:9 footage on a 16:9 canvas: same fraction both ways
        Assert.InRange(x + w, 0.95, 0.98);
        Assert.InRange(y + h, 0.93, 0.96);
        Assert.True(x > 0.5 && y > 0.5);
    }

    [Fact]
    public void Overlay_keeps_its_shape_for_vertical_footage()
    {
        var phone = Video("phone.mp4", 5, 1080, 1920);
        var o = new OverlayClip { Scale = 0.2, X = 0.5, Y = 0.5 };
        var (_, _, w, h) = OverlayLayout.Rect(o, phone);
        // 0.2 of 1920 px wide = 384 px; 9:16 → 682.7 px tall = 0.632 of 1080.
        Assert.Equal(0.2, w, 3);
        Assert.Equal(384.0 * 16 / 9 / 1080, h, 3);

        var (px, py, pw, ph) = OverlayLayout.PixelRect(o, phone, 1920, 1080);
        Assert.Equal(0, pw % 2);
        Assert.Equal(0, ph % 2);
        Assert.Equal(384, pw);
        Assert.InRange(ph, 682, 684);
        Assert.Equal(960 - pw / 2, px);
        Assert.Equal(540 - ph / 2, py);
    }

    [Fact]
    public void FullFrame_placement_fills_the_canvas_and_corners_go_back_to_picture_in_picture()
    {
        var p = new Project();
        TimelineOps.Append(p, Video("main.mp4", 30));
        var o = TimelineOps.AddOverlay(p, Video("broll.mp4", 5), S(0));

        TimelineOps.PlaceOverlay(p, o.Id, OverlayPosition.FullFrame);
        Assert.Equal((0.5, 0.5, 1.0), (o.X, o.Y, o.Scale));
        Assert.Equal((0, 0, 1920, 1080), OverlayLayout.PixelRect(o, p.FindMedia(o.SourceId), 1920, 1080));

        TimelineOps.PlaceOverlay(p, o.Id, OverlayPosition.TopLeft);
        Assert.Equal(TimelineOps.DefaultOverlayScale, o.Scale, 3);
        Assert.True(o.X < 0.5 && o.Y < 0.5);
    }

    [Fact]
    public void Overlay_layout_and_tracks_are_clamped()
    {
        var p = new Project();
        var o = TimelineOps.AddOverlay(p, Video("cam.mp4", 10), S(-5), track: 9);
        Assert.Equal(TimeSpan.Zero, o.Start);
        Assert.Equal(TimelineOps.MaxOverlayTracks, o.Track);

        TimelineOps.SetOverlayLayout(p, o.Id, 2, -1, 5);
        Assert.Equal((1.0, 0.0, OverlayLayout.MaxScale), (o.X, o.Y, o.Scale));
        TimelineOps.SetOverlayLayout(p, o.Id, null, null, 0.01);
        Assert.Equal(OverlayLayout.MinScale, o.Scale);

        TimelineOps.MoveOverlay(p, o.Id, S(3), 0);
        Assert.Equal((S(3), 1), (o.Start, o.Track));
    }

    [Fact]
    public void Trimming_an_overlay_front_keeps_its_end_in_place()
    {
        var p = new Project();
        var o = TimelineOps.AddOverlay(p, Video("cam.mp4", 10), S(5));
        TimelineOps.TrimOverlayIn(p, o.Id, S(2));
        Assert.Equal((S(7), S(2), S(15)), (o.Start, o.In, o.End));

        TimelineOps.TrimOverlayOut(p, o.Id, S(99));
        Assert.Equal(S(10), o.Out); // not past the end of the file
    }

    [Fact]
    public void OverlaysAt_lists_the_overlays_showing_bottom_track_first()
    {
        var p = new Project();
        var top = TimelineOps.AddOverlay(p, Video("a.mp4", 10), S(0), track: 2);
        var bottom = TimelineOps.AddOverlay(p, Video("b.mp4", 10), S(2), track: 1);
        TimelineOps.AddOverlay(p, Video("c.mp4", 1), S(20));

        Assert.Equal(new[] { top }, TimelineOps.OverlaysAt(p, S(1)));
        Assert.Equal(new[] { bottom, top }, TimelineOps.OverlaysAt(p, S(5)));
        Assert.Empty(TimelineOps.OverlaysAt(p, S(15)));
    }

    [Fact]
    public void FreeOverlayTrack_picks_the_lowest_track_with_room()
    {
        var p = new Project();
        TimelineOps.AddOverlay(p, Video("a.mp4", 10), S(0), track: 1);
        Assert.Equal(2, TimelineOps.FreeOverlayTrack(p, S(5), S(3)));
        Assert.Equal(1, TimelineOps.FreeOverlayTrack(p, S(10), S(3))); // starts as the other ends
    }

    [Fact]
    public void Removing_media_removes_its_overlays_and_counts_them_first()
    {
        var p = new Project();
        TimelineOps.Append(p, Video("main.mp4", 30));
        var cam = Video("cam.mp4", 10);
        TimelineOps.AddOverlay(p, cam, S(1));
        TimelineOps.AddOverlay(p, cam, S(15));

        Assert.Equal(2, TimelineOps.UsageCount(p, cam.Id));
        TimelineOps.RemoveMedia(p, cam.Id);
        Assert.Empty(p.Overlays);
    }

    [Fact]
    public void SeparateOverlayAudio_moves_its_sound_to_the_audio_track()
    {
        var p = new Project();
        var o = TimelineOps.AddOverlay(p, Video("cam.mp4", 10), S(4));
        TimelineOps.TrimOverlayIn(p, o.Id, S(1));

        var item = TimelineOps.SeparateOverlayAudio(p, o.Id)!;

        Assert.Equal((S(5), S(1), S(10)), (item.Start, item.In, item.Out));
        Assert.True(o.Muted);
    }

    [Fact]
    public void Overlays_survive_save_and_old_files_load_without_any()
    {
        var p = new Project();
        TimelineOps.Append(p, Video("main.mp4", 30));
        var o = TimelineOps.AddOverlay(p, Video("cam.mp4", 10), S(4), track: 2);
        TimelineOps.SetOverlayLayout(p, o.Id, 0.25, 0.3, 0.5);
        o.Muted = true;

        var back = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(p)).Overlays.Single();
        Assert.Equal((o.Id, o.SourceId, 2, S(4), 0.25, 0.3, 0.5, true),
            (back.Id, back.SourceId, back.Track, back.Start, back.X, back.Y, back.Scale, back.Muted));

        var json = ProjectSerializer.Serialize(new Project()).Replace("\"Overlays\": [],", "").Replace("\"Overlays\":[],", "");
        Assert.DoesNotContain("Overlays", json);
        Assert.Empty(ProjectSerializer.Deserialize(json).Overlays);
    }

    [Fact]
    public void Export_scales_and_places_overlays_over_the_main_track_under_the_annotations()
    {
        var p = new Project();
        TimelineOps.Append(p, Video("C:/v/main.mp4", 20));
        var o = TimelineOps.AddOverlay(p, Video("C:/v/cam.mp4", 10), S(4));
        TimelineOps.SetOverlayLayout(p, o.Id, 0.5, 0.5, 0.5);
        TimelineOps.AddOverlay(p, Video("C:/v/late.mp4", 5), S(25)); // after the video ends: left out
        var box = TimelineOps.CreateAnnotation(p, S(5), AnnotationKind.Box, 0, 0, .5f, .5f)!;

        var plan = ExportPlan.Build(p, new ExportSettings { UseHardwareEncoder = false },
            new Dictionary<Guid, string> { [box.Id] = "box.png" }, "out.mp4");
        var g = plan.FilterGraph;

        Assert.Contains("C:/v/cam.mp4", plan.Arguments);
        Assert.DoesNotContain("C:/v/late.mp4", plan.Arguments);
        Assert.Contains("scale=960:540", g);
        Assert.Contains("setpts=PTS+4/TB", g);
        Assert.Contains("[vcat][ov0]overlay=480:270:eof_action=pass:enable='between(t,4,14)'[vo0]", g);
        Assert.Contains("[vo0][", g); // the annotation PNG goes on top of the overlay
        Assert.Contains("amix=inputs=2", g); // the overlay's own sound is mixed in
    }

    [Fact]
    public void Overlay_zoom_crops_the_source_and_keeps_the_box()
    {
        var p = new Project();
        var o = TimelineOps.AddOverlay(p, Video("cam.mp4", 10), S(0));
        var box = OverlayLayout.Rect(o, p.FindMedia(o.SourceId));
        Assert.Equal((0.0, 0.0, 1.0, 1.0), OverlayLayout.CropRect(o));
        Assert.Equal("", OverlayLayout.CropFilter(o));

        TimelineOps.SetOverlayZoom(p, o.Id, 2);
        Assert.Equal((0.25, 0.25, 0.5, 0.5), OverlayLayout.CropRect(o)); // centred: the middle half each way
        TimelineOps.SetOverlayPan(p, o.Id, 1, 0);
        Assert.Equal((0.5, 0.0, 0.5, 0.5), OverlayLayout.CropRect(o));   // top-right quarter
        Assert.Equal(box, OverlayLayout.Rect(o, p.FindMedia(o.SourceId))); // same box on screen

        TimelineOps.SetOverlayZoom(p, o.Id, 99);
        Assert.Equal(OverlayLayout.MaxZoom, o.Zoom);
        TimelineOps.SetOverlayZoom(p, o.Id, 0.2);
        Assert.Equal(1, o.Zoom);
    }

    [Fact]
    public void Zoomed_overlay_is_cropped_before_scaling_in_the_export()
    {
        var p = new Project();
        TimelineOps.Append(p, Video("main.mp4", 20));
        var o = TimelineOps.AddOverlay(p, Video("cam.mp4", 10), S(1));
        TimelineOps.SetOverlayZoom(p, o.Id, 2);

        var g = ExportPlan.Build(p, new ExportSettings { UseHardwareEncoder = false }, new Dictionary<Guid, string>(), "out.mp4").FilterGraph;

        Assert.Contains("crop=w='2*trunc(iw*0.5/2)':h='2*trunc(ih*0.5/2)':x='2*trunc(iw*0.25/2)':y='2*trunc(ih*0.25/2)',scale=", g);
    }

    [Fact]
    public void SplitOverlayAt_cuts_it_into_two_independent_halves()
    {
        var p = new Project();
        var o = TimelineOps.AddOverlay(p, Video("cam.mp4", 10), S(4), track: 2);
        TimelineOps.SetOverlayZoom(p, o.Id, 2);
        TimelineOps.SetOverlayLayout(p, o.Id, 0.3, 0.4, 0.5);

        var right = TimelineOps.SplitOverlayAt(p, o.Id, S(7))!;

        Assert.Equal((S(4), S(0), S(3)), (o.Start, o.In, o.Out));
        Assert.Equal((S(7), S(3), S(10), S(14)), (right.Start, right.In, right.Out, right.End));
        Assert.Equal((2, 0.3, 0.4, 0.5, 2.0), (right.Track, right.X, right.Y, right.Scale, right.Zoom));
        Assert.NotEqual(o.Id, right.Id);
        Assert.Equal(p.Overlays.IndexOf(o) + 1, p.Overlays.IndexOf(right));

        // Each half is its own now.
        TimelineOps.SetOverlayZoom(p, right.Id, 1);
        Assert.Equal(2, o.Zoom);
    }

    [Theory]
    [InlineData(4.0)]   // at its start
    [InlineData(14.0)]  // at its end
    [InlineData(20.0)]  // not on it
    public void SplitOverlayAt_refuses_cuts_at_or_outside_its_ends(double t)
    {
        var p = new Project();
        var o = TimelineOps.AddOverlay(p, Video("cam.mp4", 10), S(4));
        Assert.Null(TimelineOps.SplitOverlayAt(p, o.Id, S(t)));
        Assert.Single(p.Overlays);
    }

    [Fact]
    public void SplitAudioAt_cuts_an_audio_item_in_two()
    {
        var p = new Project();
        var clip = TimelineOps.Append(p, Video("talk.mp4", 10));
        var a = TimelineOps.SeparateAudio(p, clip.Id)!;
        TimelineOps.SetAudioVolume(p, a.Id, 0.5);

        var right = TimelineOps.SplitAudioAt(p, a.Id, S(6))!;

        Assert.Equal((S(0), S(6)), (a.In, a.Out));
        Assert.Equal((S(6), S(6), S(10), 0.5), (right.Start, right.In, right.Out, right.Volume));
        Assert.Equal(2, p.AudioTrack.Count);
    }

    [Fact]
    public void Cropping_an_overlay_shrinks_its_box_around_its_centre()
    {
        var p = new Project();
        var cam = Video("cam.mp4", 10);
        var o = TimelineOps.AddOverlay(p, cam, S(0));
        TimelineOps.SetOverlayLayout(p, o.Id, 0.5, 0.5, 0.4);
        var (_, _, w0, h0) = OverlayLayout.Rect(o, cam);

        TimelineOps.SetOverlayCrop(p, o.Id, CropSide.Left, 0.25);
        TimelineOps.SetOverlayCrop(p, o.Id, CropSide.Right, 0.25);

        var (x, y, w, h) = OverlayLayout.Rect(o, cam);
        Assert.Equal(w0 / 2, w, 6);   // half the width kept → half as wide
        Assert.Equal(h0, h, 6);       // height untouched
        Assert.Equal(0.5, x + w / 2, 6); // same centre
        Assert.Equal((0.25, 0.0, 0.5, 1.0), OverlayLayout.CropRect(o));

        // Zoom then works inside what's left.
        TimelineOps.SetOverlayZoom(p, o.Id, 2);
        var (cx, cy, cw, ch) = OverlayLayout.CropRect(o);
        Assert.Equal((0.375, 0.25, 0.25, 0.5), (Math.Round(cx, 6), Math.Round(cy, 6), Math.Round(cw, 6), Math.Round(ch, 6)));
    }

    [Fact]
    public void Crop_is_clamped_and_full_frame_fills_with_the_cropped_picture()
    {
        var p = new Project();
        var cam = Video("cam.mp4", 10);
        var o = TimelineOps.AddOverlay(p, cam, S(0));
        TimelineOps.SetOverlayCrop(p, o.Id, CropSide.Top, 0.9);
        Assert.Equal(ClipLook.MaxCropEach, o.CropTop);
        TimelineOps.SetOverlayCrop(p, o.Id, CropSide.Top, -1);
        Assert.Equal(0, o.CropTop);

        // Cut the sides to a square-ish 8:9 picture: full frame makes it as tall as the frame.
        TimelineOps.SetOverlayCrop(p, o.Id, CropSide.Left, 0.25);
        TimelineOps.SetOverlayCrop(p, o.Id, CropSide.Right, 0.25);
        TimelineOps.PlaceOverlay(p, o.Id, OverlayPosition.FullFrame);
        var (x, y, w, h) = OverlayLayout.Rect(o, cam);
        Assert.Equal((0.25, 0.0, 0.5, 1.0), (Math.Round(x, 6), Math.Round(y, 6), Math.Round(w, 6), Math.Round(h, 6)));
    }

    [Fact]
    public void Clip_crop_is_applied_before_the_fit_in_export_and_kept_by_split_and_reset_clears_it()
    {
        var p = new Project();
        var clip = TimelineOps.Append(p, Video("C:/v/main.mp4", 20));
        TimelineOps.SetClipCrop(p, clip.Id, CropSide.Bottom, 0.2);
        Assert.True(ClipLook.IsCropped(clip));
        Assert.False(ClipLook.IsDefault(clip));
        Assert.Equal(16.0 / 9 / 0.8, ClipLook.CroppedAspect(16.0 / 9, 0, 0, 0, 0.2), 6);

        var g = ExportPlan.Build(p, new ExportSettings { UseHardwareEncoder = false }, new Dictionary<Guid, string>(), "out.mp4").FilterGraph;
        Assert.Contains("crop=w='2*trunc(iw*1/2)':h='2*trunc(ih*0.8/2)':x='2*trunc(iw*0/2)':y='2*trunc(ih*0/2)',scale=1920:1080", g);

        var right = TimelineOps.SplitAt(p, S(10))!;
        Assert.Equal(0.2, right.CropBottom);

        TimelineOps.ResetClipLook(p, clip.Id);
        Assert.Equal(0, clip.CropBottom);
    }

    [Fact]
    public void Old_files_load_overlays_unzoomed()
    {
        var p = new Project();
        TimelineOps.AddOverlay(p, Video("cam.mp4", 10), S(0));
        var json = ProjectSerializer.Serialize(p).Replace("\"Zoom\": 1,", "").Replace("\"PanX\": 0.5,", "").Replace("\"PanY\": 0.5,", "");
        Assert.DoesNotContain("PanX", json);
        var o = ProjectSerializer.Deserialize(json).Overlays.Single();
        Assert.Equal((1.0, 0.5, 0.5), (o.Zoom, o.PanX, o.PanY));
    }

    [Fact]
    public void Muted_overlay_is_not_heard_in_the_export()
    {
        var p = new Project();
        TimelineOps.Append(p, Video("main.mp4", 20));
        var o = TimelineOps.AddOverlay(p, Video("cam.mp4", 10), S(4));
        TimelineOps.SetOverlayMuted(p, o.Id, true);

        var plan = ExportPlan.Build(p, new ExportSettings { UseHardwareEncoder = false }, new Dictionary<Guid, string>(), "out.mp4");

        Assert.DoesNotContain("amix", plan.FilterGraph);
    }
}
