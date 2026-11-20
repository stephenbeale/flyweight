using Flyweight.Core.Editing;
using Flyweight.Core.Export;
using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

public class ClipVolumeTests
{
    static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    static (Project p, Clip clip) OneClip(bool audio = true)
    {
        var p = new Project();
        var m = new SourceMedia { OriginalPath = "a.mp4", Duration = S(10), Fps = 30, HasAudio = audio };
        p.Media.Add(m);
        return (p, TimelineOps.Append(p, m));
    }

    static string Graph(Project p) =>
        ExportPlan.Build(p, new ExportSettings(), new Dictionary<Guid, string>(), "out.mp4").FilterGraph;

    [Fact]
    public void New_clips_play_at_full_volume_unmuted()
    {
        var (_, c) = OneClip();
        Assert.Equal(1.0, c.Volume);
        Assert.False(c.Muted);
        Assert.Equal(1.0, c.EffectiveVolume);
    }

    [Theory]
    [InlineData(0.5, 0.5)]
    [InlineData(2.0, 2.0)]
    [InlineData(3.5, 2.0)]
    [InlineData(-1, 0)]
    [InlineData(double.NaN, 1.0)]
    public void SetClipVolume_clamps_to_0_to_200_percent(double input, double expected)
    {
        var (p, c) = OneClip();
        TimelineOps.SetClipVolume(p, c.Id, input);
        Assert.Equal(expected, c.Volume);
    }

    [Fact]
    public void Mute_keeps_the_volume_so_unmute_restores_it()
    {
        var (p, c) = OneClip();
        TimelineOps.SetClipVolume(p, c.Id, 1.5);
        TimelineOps.SetClipMuted(p, c.Id, true);
        Assert.Equal(0, c.EffectiveVolume);
        TimelineOps.SetClipMuted(p, c.Id, false);
        Assert.Equal(1.5, c.EffectiveVolume);
    }

    [Fact]
    public void Split_halves_keep_the_clip_volume_and_mute()
    {
        var (p, c) = OneClip();
        TimelineOps.SetClipVolume(p, c.Id, 0.4);
        TimelineOps.SetClipMuted(p, c.Id, true);
        var right = TimelineOps.SplitAt(p, S(5))!;
        Assert.Equal(0.4, right.Volume);
        Assert.True(right.Muted);
    }

    [Fact]
    public void Volume_and_mute_survive_save_and_undo()
    {
        var session = new ProjectSession();
        session.New();
        var m = new SourceMedia { OriginalPath = "a.mp4", Duration = S(10), Fps = 30, HasAudio = true };
        Clip? clip = null;
        session.Execute(p => { p.Media.Add(m); clip = TimelineOps.Append(p, m); });
        session.Execute(p => TimelineOps.SetClipVolume(p, clip!.Id, 1.25));
        session.Execute(p => TimelineOps.SetClipMuted(p, clip!.Id, true));

        var loaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(session.Project)).Timeline[0];
        Assert.Equal(1.25, loaded.Volume);
        Assert.True(loaded.Muted);

        session.Undo();
        Assert.False(session.Project.Timeline[0].Muted);
        session.Undo();
        Assert.Equal(1.0, session.Project.Timeline[0].Volume);
    }

    [Fact]
    public void Project_files_from_before_clip_volume_load_at_full_volume()
    {
        var id = Guid.NewGuid();
        var legacy = $$"""
            { "Version": 1,
              "Media": [ { "Id": "{{id}}", "OriginalPath": "a.mp4", "Duration": "00:00:10", "Fps": 30, "HasAudio": true } ],
              "Timeline": [ { "Id": "{{Guid.NewGuid()}}", "SourceId": "{{id}}", "In": "00:00:00", "Out": "00:00:10" } ],
              "Annotations": [] }
            """;
        var c = ProjectSerializer.Deserialize(legacy).Timeline[0];
        Assert.Equal(1.0, c.Volume);
        Assert.False(c.Muted);
    }

    [Fact]
    public void Export_leaves_full_volume_audio_untouched()
    {
        var (p, _) = OneClip();
        Assert.DoesNotContain("volume=", Graph(p));
        Assert.Contains("[0:a]", Graph(p));
    }

    [Fact]
    public void Export_applies_the_clip_gain_with_invariant_numbers()
    {
        var (p, c) = OneClip();
        TimelineOps.SetClipVolume(p, c.Id, 1.5);
        Assert.Contains("volume=1.5,", Graph(p));
    }

    [Fact]
    public void Export_replaces_a_muted_or_zero_volume_clip_with_silence()
    {
        var (p, c) = OneClip();
        TimelineOps.SetClipMuted(p, c.Id, true);
        Assert.DoesNotContain("[0:a]", Graph(p));
        Assert.Contains("anullsrc", Graph(p));

        TimelineOps.SetClipMuted(p, c.Id, false);
        TimelineOps.SetClipVolume(p, c.Id, 0);
        Assert.DoesNotContain("[0:a]", Graph(p));
    }
}
