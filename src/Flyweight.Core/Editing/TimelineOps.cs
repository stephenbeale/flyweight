using Flyweight.Core.Model;

namespace Flyweight.Core.Editing;

/// <summary>Where a timeline time lands: which clip, and what source time that is.</summary>
public readonly record struct TimelineHit(int Index, Clip Clip, TimeSpan ClipStart, TimeSpan SourceTime);

/// <summary>
/// All timeline edits as pure functions over <see cref="Project"/>.
/// No UI, no undo: callers wrap these in <see cref="ProjectSession.Execute"/>.
/// </summary>
public static class TimelineOps
{
    /// <summary>Shortest clip we allow (avoids zero-length segments that break ffmpeg concat).</summary>
    public static readonly TimeSpan MinClipLength = TimeSpan.FromMilliseconds(100);

    public static readonly TimeSpan DefaultAnnotationLength = TimeSpan.FromSeconds(3);

    /// <summary>How long an imported still image runs on the timeline.</summary>
    public static readonly TimeSpan DefaultStillLength = TimeSpan.FromSeconds(8);

    public static TimeSpan TotalDuration(Project p) =>
        p.Timeline.Aggregate(TimeSpan.Zero, (sum, c) => sum + c.Duration);

    public static TimeSpan ClipStart(Project p, int index)
    {
        var t = TimeSpan.Zero;
        for (int i = 0; i < index && i < p.Timeline.Count; i++) t += p.Timeline[i].Duration;
        return t;
    }

    public static TimeSpan ClipStart(Project p, Guid clipId) =>
        ClipStart(p, p.Timeline.FindIndex(c => c.Id == clipId));

    /// <summary>Find the clip under timeline time <paramref name="t"/>. Null if past the end.</summary>
    public static TimelineHit? Locate(Project p, TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        var start = TimeSpan.Zero;
        for (int i = 0; i < p.Timeline.Count; i++)
        {
            var c = p.Timeline[i];
            if (t < start + c.Duration)
                return new TimelineHit(i, c, start, c.In + (t - start));
            start += c.Duration;
        }
        return null;
    }

    public static Clip Append(Project p, SourceMedia media)
    {
        var clip = new Clip { SourceId = media.Id, In = TimeSpan.Zero, Out = media.IsStill ? DefaultStillLength : media.Duration };
        p.Timeline.Add(clip);
        return clip;
    }

    /// <summary>
    /// Split the clip under <paramref name="t"/> into two. Annotations move to the half they
    /// start in; ones that straddle the cut are duplicated so each half keeps its part.
    /// Returns the new right-hand clip, or null if the cut is too close to an edge.
    /// </summary>
    public static Clip? SplitAt(Project p, TimeSpan t)
    {
        if (Locate(p, t) is not { } hit) return null;
        var left = hit.Clip;
        var cut = hit.SourceTime;
        if (cut - left.In < MinClipLength || left.Out - cut < MinClipLength) return null;

        var right = new Clip { SourceId = left.SourceId, In = cut, Out = left.Out };
        left.Out = cut;
        p.Timeline.Insert(hit.Index + 1, right);

        foreach (var a in p.Annotations.Where(a => a.ClipId == left.Id).ToList())
        {
            if (a.Start >= cut)
            {
                a.ClipId = right.Id;
            }
            else if (a.End > cut)
            {
                var copy = a.Clone();
                copy.Id = Guid.NewGuid();
                copy.ClipId = right.Id;
                p.Annotations.Add(copy);
            }
        }
        foreach (var m in p.Markers.Where(m => m.ClipId == left.Id && m.SourceTime >= cut))
            m.ClipId = right.Id;
        return right;
    }

    /// <summary>How long a freeze frame holds by default.</summary>
    public static readonly TimeSpan DefaultFreezeLength = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Insert a still (the frame grabbed at <paramref name="t"/>) at the playhead, held for
    /// <paramref name="length"/>. The clip under the playhead is split there first, and the rest
    /// of the timeline ripples right. At (or near) a clip boundary no split is needed.
    /// Returns the new still clip, or null if <paramref name="t"/> is outside the timeline.
    /// </summary>
    public static Clip? InsertFreezeFrame(Project p, TimeSpan t, SourceMedia still, TimeSpan length)
    {
        if (!still.IsStill) throw new ArgumentException("Freeze frames must be still images.", nameof(still));
        if (Locate(p, t) is not { } hit) return null;

        int insertAt;
        if (hit.SourceTime - hit.Clip.In < MinClipLength) insertAt = hit.Index;               // at the clip's start
        else if (hit.Clip.Out - hit.SourceTime < MinClipLength) insertAt = hit.Index + 1;     // at its end
        else { SplitAt(p, t); insertAt = hit.Index + 1; }

        if (p.FindMedia(still.Id) is null) p.Media.Add(still);
        var hold = Clamp(length, MinClipLength, still.Duration);
        var clip = new Clip { SourceId = still.Id, In = TimeSpan.Zero, Out = hold };
        p.Timeline.Insert(insertAt, clip);
        return clip;
    }

    /// <summary>Ripple delete: remove the clip and close the gap. Its annotations go too.</summary>
    public static void Delete(Project p, Guid clipId)
    {
        p.Timeline.RemoveAll(c => c.Id == clipId);
        p.Annotations.RemoveAll(a => a.ClipId == clipId);
        p.Markers.RemoveAll(m => m.ClipId == clipId);
    }

    public static void Move(Project p, Guid clipId, int newIndex)
    {
        int old = p.Timeline.FindIndex(c => c.Id == clipId);
        if (old < 0) return;
        var clip = p.Timeline[old];
        p.Timeline.RemoveAt(old);
        newIndex = Math.Clamp(newIndex, 0, p.Timeline.Count);
        p.Timeline.Insert(newIndex, clip);
    }

    public static void TrimIn(Project p, Guid clipId, TimeSpan newIn)
    {
        if (p.FindClip(clipId) is not { } c) return;
        c.In = Clamp(newIn, TimeSpan.Zero, c.Out - MinClipLength);
    }

    public static void TrimOut(Project p, Guid clipId, TimeSpan newOut)
    {
        if (p.FindClip(clipId) is not { } c || p.FindMedia(c.SourceId) is not { } m) return;
        c.Out = Clamp(newOut, c.In + MinClipLength, m.Duration);
    }

    /// <summary>
    /// The timeline range in which an annotation is visible (clamped to its clip), or null if
    /// it is currently trimmed out of view or its clip no longer exists.
    /// </summary>
    public static (TimeSpan Start, TimeSpan End)? AnnotationRange(Project p, Annotation a)
    {
        int idx = p.Timeline.FindIndex(c => c.Id == a.ClipId);
        if (idx < 0) return null;
        var clip = p.Timeline[idx];
        var s = Max(a.Start, clip.In);
        var e = Min(a.End, clip.Out);
        if (e <= s) return null;
        var clipStart = ClipStart(p, idx);
        return (clipStart + (s - clip.In), clipStart + (e - clip.In));
    }

    public static IEnumerable<Annotation> VisibleAt(Project p, TimeSpan t)
    {
        if (Locate(p, t) is not { } hit) yield break;
        foreach (var a in p.Annotations)
            if (a.ClipId == hit.Clip.Id && a.Start <= hit.SourceTime && hit.SourceTime < a.End)
                yield return a;
    }

    /// <summary>Create an annotation at timeline time <paramref name="t"/> with the default length.</summary>
    public static Annotation? CreateAnnotation(Project p, TimeSpan t, AnnotationKind kind,
        float x1, float y1, float x2, float y2)
    {
        if (Locate(p, t) is not { } hit) return null;
        var a = new Annotation
        {
            ClipId = hit.Clip.Id,
            Kind = kind,
            Start = hit.SourceTime,
            End = Min(hit.SourceTime + DefaultAnnotationLength, hit.Clip.Out),
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
        };
        if (kind == AnnotationKind.Text)
        {
            a.Text = "Text";
            a.StrokeColor = "#FFFFFFFF";
            a.FillColor = "#B0000000";
        }
        p.Annotations.Add(a);
        return a;
    }

    /// <summary>Set an annotation's visible range from timeline times (e.g. dragging its bar).</summary>
    public static void RetimeAnnotation(Project p, Guid annotationId, TimeSpan timelineStart, TimeSpan timelineEnd)
    {
        if (p.FindAnnotation(annotationId) is not { } a) return;
        int idx = p.Timeline.FindIndex(c => c.Id == a.ClipId);
        if (idx < 0) return;
        var clip = p.Timeline[idx];
        var clipStart = ClipStart(p, idx);
        var s = Clamp(clip.In + (timelineStart - clipStart), clip.In, clip.Out - MinClipLength);
        var e = Clamp(clip.In + (timelineEnd - clipStart), s + MinClipLength, clip.Out);
        a.Start = s;
        a.End = e;
    }

    static TimeSpan Clamp(TimeSpan v, TimeSpan lo, TimeSpan hi) => v < lo ? lo : v > hi ? hi : v;
    static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
