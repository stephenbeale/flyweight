using Flyweight.Core.Editing;
using Flyweight.Core.Export;
using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

public class AudioTrackTests
{
    static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    static (Project p, SourceMedia song) VideoAndSong(double videoSecs = 10, double songSecs = 30)
    {
        var p = new Project();
        var video = new SourceMedia { OriginalPath = "v.mp4", Duration = S(videoSecs), Fps = 30, HasAudio = true };
        var song = new SourceMedia { OriginalPath = "song.mp3", Duration = S(songSecs), HasAudio = true, IsAudioOnly = true };
        p.Media.AddRange([video, song]);
        TimelineOps.Append(p, video);
        return (p, song);
    }

    static string Graph(Project p) =>
        ExportPlan.Build(p, new ExportSettings(), new Dictionary<Guid, string>(), "out.mp4").FilterGraph;

    [Fact]
    public void AddAudio_places_the_whole_file_at_the_given_time()
    {
        var (p, song) = VideoAndSong();
        var a = TimelineOps.AddAudio(p, song, S(2));
        Assert.Equal(S(2), a.Start);
        Assert.Equal((S(0), S(30)), (a.In, a.Out));
        Assert.Equal(S(32), a.End);
        Assert.Single(p.AudioTrack);
        Assert.Single(p.Timeline); // the video timeline is untouched
    }

    [Fact]
    public void Audio_files_cannot_go_on_the_video_timeline_and_video_cannot_go_on_the_audio_track()
    {
        var (p, song) = VideoAndSong();
        Assert.Throws<ArgumentException>(() => TimelineOps.Append(p, song));
        Assert.Throws<ArgumentException>(() => TimelineOps.AddAudio(p, p.Media[0], S(0)));
    }

    [Fact]
    public void MoveAudio_never_goes_before_zero()
    {
        var (p, song) = VideoAndSong();
        var a = TimelineOps.AddAudio(p, song, S(5));
        TimelineOps.MoveAudio(p, a.Id, S(-3));
        Assert.Equal(S(0), a.Start);
        TimelineOps.MoveAudio(p, a.Id, S(7));
        Assert.Equal(S(7), a.Start);
        Assert.Equal(S(30), a.Duration);
    }

    [Fact]
    public void Audio_stays_at_its_timeline_time_when_video_is_trimmed()
    {
        var (p, song) = VideoAndSong();
        var a = TimelineOps.AddAudio(p, song, S(4));
        TimelineOps.TrimIn(p, p.Timeline[0].Id, S(3));
        Assert.Equal(S(4), a.Start);
    }

    [Fact]
    public void TrimAudioIn_keeps_the_end_fixed_and_can_be_dragged_back()
    {
        var (p, song) = VideoAndSong();
        var a = TimelineOps.AddAudio(p, song, S(2));
        TimelineOps.TrimAudioIn(p, a.Id, S(5));
        Assert.Equal((S(7), S(5), S(32)), (a.Start, a.In, a.End));

        TimelineOps.TrimAudioIn(p, a.Id, S(-4)); // back past the file's start: stops at in 0
        Assert.Equal((S(2), S(0), S(32)), (a.Start, a.In, a.End));
    }

    [Fact]
    public void TrimAudioIn_cannot_reveal_audio_before_timeline_zero()
    {
        var (p, song) = VideoAndSong();
        var a = TimelineOps.AddAudio(p, song, S(1));
        TimelineOps.TrimAudioIn(p, a.Id, S(10)); // start 11, in 10
        TimelineOps.MoveAudio(p, a.Id, S(2));    // start 2, in 10
        TimelineOps.TrimAudioIn(p, a.Id, S(0));  // only 2 s of room before timeline 0
        Assert.Equal((S(0), S(8)), (a.Start, a.In));
    }

    [Fact]
    public void TrimAudioOut_is_clamped_to_the_file_length_and_minimum()
    {
        var (p, song) = VideoAndSong();
        var a = TimelineOps.AddAudio(p, song, S(0));
        TimelineOps.TrimAudioOut(p, a.Id, S(99));
        Assert.Equal(S(30), a.Out);
        TimelineOps.TrimAudioOut(p, a.Id, S(-1));
        Assert.Equal(TimelineOps.MinClipLength, a.Out);
    }

    [Fact]
    public void AudioAt_finds_items_sounding_at_a_time()
    {
        var (p, song) = VideoAndSong();
        var a = TimelineOps.AddAudio(p, song, S(2));
        TimelineOps.TrimAudioOut(p, a.Id, S(3)); // 2–5
        Assert.Empty(TimelineOps.AudioAt(p, S(1.9)));
        Assert.Single(TimelineOps.AudioAt(p, S(2)));
        Assert.Empty(TimelineOps.AudioAt(p, S(5)));
    }

    [Fact]
    public void Volume_mute_and_delete()
    {
        var (p, song) = VideoAndSong();
        var a = TimelineOps.AddAudio(p, song, S(0));
        TimelineOps.SetAudioVolume(p, a.Id, 9);
        Assert.Equal(2.0, a.Volume);
        TimelineOps.SetAudioMuted(p, a.Id, true);
        Assert.Equal(0, a.EffectiveVolume);
        TimelineOps.DeleteAudio(p, a.Id);
        Assert.Empty(p.AudioTrack);
    }

    [Fact]
    public void Audio_track_survives_save_and_old_files_load_with_none()
    {
        var (p, song) = VideoAndSong();
        var a = TimelineOps.AddAudio(p, song, S(1.5));
        TimelineOps.SetAudioVolume(p, a.Id, 0.3);
        var loaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(p));
        var b = Assert.Single(loaded.AudioTrack);
        Assert.Equal((a.Id, S(1.5), 0.3), (b.Id, b.Start, b.Volume));
        Assert.True(loaded.FindMedia(song.Id)!.IsAudioOnly);

        var legacy = """{ "Version": 1, "Media": [], "Timeline": [], "Annotations": [] }""";
        Assert.Empty(ProjectSerializer.Deserialize(legacy).AudioTrack);
    }

    [Fact]
    public void Export_without_audio_items_has_no_mix()
    {
        var (p, _) = VideoAndSong();
        var g = Graph(p);
        Assert.DoesNotContain("amix", g);
        Assert.Contains("[vcat][aout]", g);
    }

    [Fact]
    public void Export_delays_and_mixes_audio_items_at_their_own_levels()
    {
        var (p, song) = VideoAndSong();
        var a = TimelineOps.AddAudio(p, song, S(1.5));
        TimelineOps.TrimAudioIn(p, a.Id, S(2)); // start 3.5, in 2
        TimelineOps.SetAudioVolume(p, a.Id, 0.25);
        var plan = ExportPlan.Build(p, new ExportSettings(), new Dictionary<Guid, string>(), "out.mp4");

        Assert.Contains("volume=0.25,adelay=3500:all=1[m0]", plan.FilterGraph);
        Assert.Contains("[acat][m0]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[aout]", plan.FilterGraph);
        // The song is input 1, seeked to its in point and cut to its length.
        var args = plan.Arguments.ToList();
        int songInput = args.IndexOf("song.mp3");
        Assert.Equal(new[] { "-ss", "2", "-t", "28", "-i" }, args.GetRange(songInput - 5, 5));
        Assert.Equal(TimeSpan.FromSeconds(10), plan.Duration); // still the video's length
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(0, false)] // unknown (imported before channels were read): ffmpeg's default upmix
    public void Mono_audio_is_copied_to_both_speakers_at_full_level(int channels, bool expectPan)
    {
        var (p, song) = VideoAndSong();
        song.AudioChannels = channels;
        TimelineOps.AddAudio(p, song, S(0));
        Assert.Equal(expectPan, Graph(p).Contains("pan=stereo|c0=c0|c1=c0"));
    }

    [Fact]
    public void Export_skips_muted_items_and_items_after_the_video_ends()
    {
        var (p, song) = VideoAndSong();
        var muted = TimelineOps.AddAudio(p, song, S(0));
        TimelineOps.SetAudioMuted(p, muted.Id, true);
        TimelineOps.AddAudio(p, song, S(10)); // starts exactly when the 10 s video ends
        var plan = ExportPlan.Build(p, new ExportSettings(), new Dictionary<Guid, string>(), "out.mp4");
        Assert.DoesNotContain("amix", plan.FilterGraph);
        Assert.DoesNotContain("song.mp3", plan.Arguments);
    }

    [Fact]
    public void Overlay_inputs_are_numbered_after_audio_inputs()
    {
        var (p, song) = VideoAndSong();
        TimelineOps.AddAudio(p, song, S(0));
        var box = TimelineOps.CreateAnnotation(p, S(1), AnnotationKind.Box, 0, 0, .5f, .5f)!;
        var g = ExportPlan.Build(p, new ExportSettings(), new Dictionary<Guid, string> { [box.Id] = "box.png" }, "out.mp4").FilterGraph;
        Assert.Contains("[1:a]", g);      // the song
        Assert.Contains("[vcat][2:v]overlay", g); // the PNG
    }
}
