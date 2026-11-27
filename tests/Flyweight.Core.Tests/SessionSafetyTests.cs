using Flyweight.Core.Editing;
using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

/// <summary>Every change to the project must be undoable and mark it dirty (so autosave and the save prompt see it).</summary>
public class SessionSafetyTests
{
    static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    static (ProjectSession s, Guid clipId) OneClip()
    {
        var s = new ProjectSession();
        var m = new SourceMedia { OriginalPath = "a.mp4", Duration = S(10), Fps = 30 };
        s.Execute(p => { p.Media.Add(m); TimelineOps.Append(p, m); });
        return (s, s.Project.Timeline[0].Id);
    }

    [Fact]
    public void Undo_in_the_middle_of_a_drag_leaves_the_rest_of_the_drag_undoable()
    {
        var (s, id) = OneClip();
        s.BeginEdit();
        s.Preview(p => TimelineOps.TrimIn(p, id, S(2)));

        s.Undo(); // Ctrl+Z while the mouse is still down: the drag so far becomes a step and is undone
        Assert.Equal(TimeSpan.Zero, s.Project.Timeline[0].In);

        // The mouse keeps moving, then is released.
        s.Preview(p => TimelineOps.TrimIn(p, id, S(3)));
        s.CommitEdit();
        Assert.Equal(S(3), s.Project.Timeline[0].In);
        Assert.True(s.IsDirty);

        s.Undo(); // undoes the rest of the drag, not the clip that was there before it
        Assert.Single(s.Project.Timeline);
        Assert.Equal(TimeSpan.Zero, s.Project.Timeline[0].In);
    }

    [Fact]
    public void A_preview_without_BeginEdit_still_becomes_one_undo_step_and_marks_the_project_dirty()
    {
        var (s, id) = OneClip();
        s.Load(s.Project, null); // fresh: clean, no history
        Assert.False(s.IsDirty);

        s.Preview(p => TimelineOps.TrimIn(p, id, S(1)));
        s.Preview(p => TimelineOps.TrimIn(p, id, S(2)));
        s.CommitEdit();

        Assert.True(s.IsDirty);
        Assert.True(s.CanUndo);
        s.Undo();
        Assert.Equal(TimeSpan.Zero, s.Project.Timeline[0].In);
        Assert.False(s.CanUndo);
    }

    [Fact]
    public void An_edit_that_throws_is_rolled_back_and_adds_no_undo_step()
    {
        var (s, _) = OneClip();
        var before = ProjectSerializer.Serialize(s.Project);
        int changed = 0;
        s.Changed += (_, _) => changed++;

        Assert.Throws<InvalidOperationException>(() => s.Execute(p =>
        {
            p.Timeline.Clear(); // half-applied when it fails
            throw new InvalidOperationException("boom");
        }));

        Assert.Equal(before, ProjectSerializer.Serialize(s.Project));
        Assert.Equal(1, changed); // the UI is told to redraw the restored project
        s.Undo();
        Assert.Empty(s.Project.Timeline); // the one undo step left is the original append
        Assert.False(s.CanUndo);
    }
}
