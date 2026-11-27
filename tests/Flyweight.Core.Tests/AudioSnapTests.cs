using Flyweight.Core.Editing;
using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

/// <summary>Owner: audio items slid together "should touch and there should be some tolerance there".</summary>
public class AudioSnapTests
{
    static TimeSpan S(double s) => TimeSpan.FromSeconds(s);
    static readonly TimeSpan Tol = S(0.25);

    static SourceMedia Music(double secs) => new() { OriginalPath = "m.mp3", Duration = S(secs), IsAudioOnly = true, HasAudio = true };

    static (Project p, AudioItem first, AudioItem second) TwoItems()
    {
        var p = new Project();
        TimelineOps.Append(p, new SourceMedia { OriginalPath = "v.mp4", Duration = S(30), Fps = 30, Width = 1920, Height = 1080 });
        var first = TimelineOps.AddAudio(p, Music(5), S(0));   // 0–5
        var second = TimelineOps.AddAudio(p, Music(5), S(9));  // 9–14
        return (p, first, second);
    }

    [Fact]
    public void Dragging_close_to_the_end_of_another_item_makes_them_touch()
    {
        var (p, _, second) = TwoItems();
        Assert.Equal(S(5), TimelineOps.SnapAudioStart(p, second.Id, S(4.9), Tol));  // overlapping a hair → touches
        Assert.Equal(S(5), TimelineOps.SnapAudioStart(p, second.Id, S(5.2), Tol));  // a small gap → touches
    }

    [Fact]
    public void The_end_edge_snaps_to_the_start_of_the_next_item()
    {
        var (p, first, _) = TwoItems();
        Assert.Equal(S(4), TimelineOps.SnapAudioStart(p, first.Id, S(4.1), Tol));   // its end (9.1) sticks to 9
    }

    [Fact]
    public void Far_from_any_edge_it_moves_freely()
    {
        var (p, _, second) = TwoItems();
        Assert.Equal(S(7), TimelineOps.SnapAudioStart(p, second.Id, S(7), Tol));
    }

    [Fact]
    public void Edges_also_stick_to_clip_cuts_and_the_timeline_start()
    {
        var (p, first, _) = TwoItems();
        TimelineOps.Append(p, new SourceMedia { OriginalPath = "w.mp4", Duration = S(10), Fps = 30, Width = 1920, Height = 1080 }); // cut at 30
        Assert.Equal(S(30), TimelineOps.SnapAudioEdge(p, first.Id, S(29.9), Tol));
        Assert.Equal(S(0), TimelineOps.SnapAudioStart(p, first.Id, S(0.1), Tol));
    }
}
