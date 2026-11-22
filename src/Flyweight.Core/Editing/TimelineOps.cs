using Flyweight.Core.Media;
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

    public static Clip Append(Project p, SourceMedia media) => InsertMedia(p, media, p.Timeline.Count);

    /// <summary>
    /// Insert the whole of <paramref name="media"/> (or <see cref="DefaultStillLength"/> of a still)
    /// as a new clip at timeline position <paramref name="index"/> (clamped). Later clips ripple right.
    /// </summary>
    public static Clip InsertMedia(Project p, SourceMedia media, int index)
    {
        if (media.IsAudioOnly)
            throw new ArgumentException("Audio files go on the audio track (AddAudio), not the video timeline.", nameof(media));
        if (p.FindMedia(media.Id) is null) p.Media.Add(media);
        var clip = new Clip { SourceId = media.Id, In = TimeSpan.Zero, Out = media.IsStill ? DefaultStillLength : media.Duration };
        p.Timeline.Insert(Math.Clamp(index, 0, p.Timeline.Count), clip);
        return clip;
    }

    /// <summary>
    /// Projects saved before stills were detected (D17) hold images as ~0.04 s "videos", which make
    /// zero-length clips. Turn those into proper stills and give their clips a usable length.
    /// Returns true if anything changed. Called on load, so old projects just work.
    /// </summary>
    public static bool UpgradeLegacyStills(Project p)
    {
        bool changed = false;
        foreach (var m in p.Media.Where(m => !m.IsStill && m.Duration < MinClipLength && MediaProbe.IsImageFile(m.OriginalPath)))
        {
            m.IsStill = true;
            m.Duration = SourceMedia.StillMaxDuration;
            m.Fps = 30;
            m.HasAudio = false;
            foreach (var c in p.Timeline.Where(c => c.SourceId == m.Id && c.Duration < MinClipLength))
            {
                c.In = TimeSpan.Zero;
                c.Out = DefaultStillLength;
            }
            changed = true;
        }
        return changed;
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

        var right = new Clip { SourceId = left.SourceId, In = cut, Out = left.Out, Volume = left.Volume, Muted = left.Muted };
        CopyLook(left, right);
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
                a.LinkId ??= Guid.NewGuid(); // the two halves stay one annotation
                var copy = a.Clone();
                copy.Id = Guid.NewGuid();
                copy.ClipId = right.Id;
                p.Annotations.Add(copy);
            }
        }
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
    public static Clip? InsertFreezeFrame(Project p, TimeSpan t, SourceMedia still, TimeSpan length, bool copyAnnotations = false)
    {
        if (!still.IsStill) throw new ArgumentException("Freeze frames must be still images.", nameof(still));
        if (Locate(p, t) is not { } hit) return null;
        // What's on screen at the frozen moment, captured before the split below moves things around.
        var onScreen = copyAnnotations ? VisibleAt(p, t).Select(a => a.Clone()).ToList() : new List<Annotation>();

        int insertAt;
        if (hit.SourceTime - hit.Clip.In < MinClipLength) insertAt = hit.Index;               // at the clip's start
        else if (hit.Clip.Out - hit.SourceTime < MinClipLength) insertAt = hit.Index + 1;     // at its end
        else { SplitAt(p, t); insertAt = hit.Index + 1; }

        if (p.FindMedia(still.Id) is null) p.Media.Add(still);
        var hold = Clamp(length, MinClipLength, still.Duration);
        var clip = new Clip { SourceId = still.Id, In = TimeSpan.Zero, Out = hold };
        CopyLook(hit.Clip, clip); // the grabbed frame is unzoomed and as shot: freeze it as it looked
        p.Timeline.Insert(insertAt, clip);

        // Copies (not burned in) so they stay editable; they hold for the whole freeze.
        foreach (var a in onScreen)
        {
            a.Id = Guid.NewGuid();
            a.ClipId = clip.Id;
            a.Start = TimeSpan.Zero;
            a.End = hold;
            p.Annotations.Add(a);
        }
        return clip;
    }

    /// <summary>Ripple delete: remove the clip and close the gap. Its annotations go too.</summary>
    public static void Delete(Project p, Guid clipId)
    {
        p.Timeline.RemoveAll(c => c.Id == clipId);
        p.Annotations.RemoveAll(a => a.ClipId == clipId);
    }

    /// <summary>
    /// Remove a file from the media bin, with every clip and audio-track item that uses it (and those
    /// clips' annotations). The file on disk is untouched. One call = one undo step.
    /// </summary>
    public static void RemoveMedia(Project p, Guid mediaId)
    {
        foreach (var c in p.Timeline.Where(c => c.SourceId == mediaId).ToList()) Delete(p, c.Id);
        p.AudioTrack.RemoveAll(a => a.SourceId == mediaId);
        p.Media.RemoveAll(m => m.Id == mediaId);
    }

    /// <summary>How many timeline clips and audio items use this file (to warn before removing it).</summary>
    public static int UsageCount(Project p, Guid mediaId) =>
        p.Timeline.Count(c => c.SourceId == mediaId) + p.AudioTrack.Count(a => a.SourceId == mediaId);

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

    /// <summary>Loudest a clip can be turned up (200%).</summary>
    public const double MaxClipVolume = 2.0;

    /// <summary>Set a clip's audio gain (clamped to 0–<see cref="MaxClipVolume"/>). Doesn't change mute.</summary>
    public static void SetClipVolume(Project p, Guid clipId, double volume)
    {
        if (p.FindClip(clipId) is not { } c) return;
        c.Volume = double.IsFinite(volume) ? Math.Clamp(volume, 0, MaxClipVolume) : 1.0;
    }

    public static void SetClipMuted(Project p, Guid clipId, bool muted)
    {
        if (p.FindClip(clipId) is { } c) c.Muted = muted;
    }

    // ---------------- Picture: zoom and brightness/contrast (maths in ClipLook) ----------------

    /// <summary>Set a clip's zoom (clamped to 1–<see cref="ClipLook.MaxZoom"/>). Keeps where it is panned to.</summary>
    public static void SetClipZoom(Project p, Guid clipId, double zoom)
    {
        if (p.FindClip(clipId) is { } c) c.Zoom = double.IsFinite(zoom) ? Math.Clamp(zoom, 1, ClipLook.MaxZoom) : 1;
    }

    /// <summary>Where the zoomed view sits: 0–1 each way, 0.5 = centred. Null leaves that axis alone.</summary>
    public static void SetClipPan(Project p, Guid clipId, double? panX, double? panY)
    {
        if (p.FindClip(clipId) is not { } c) return;
        if (panX is { } x) c.PanX = double.IsFinite(x) ? Math.Clamp(x, 0, 1) : 0.5;
        if (panY is { } y) c.PanY = double.IsFinite(y) ? Math.Clamp(y, 0, 1) : 0.5;
    }

    /// <summary>Brightness -1…+1 (0 = as shot).</summary>
    public static void SetClipBrightness(Project p, Guid clipId, double brightness)
    {
        if (p.FindClip(clipId) is { } c) c.Brightness = double.IsFinite(brightness) ? Math.Clamp(brightness, -1, 1) : 0;
    }

    /// <summary>Contrast -1…+1 (0 = as shot).</summary>
    public static void SetClipContrast(Project p, Guid clipId, double contrast)
    {
        if (p.FindClip(clipId) is { } c) c.Contrast = double.IsFinite(contrast) ? Math.Clamp(contrast, -1, 1) : 0;
    }

    /// <summary>Back to the whole frame, as shot.</summary>
    public static void ResetClipLook(Project p, Guid clipId)
    {
        if (p.FindClip(clipId) is { } c) CopyLook(new Clip(), c);
    }

    static void CopyLook(Clip from, Clip to)
    {
        to.Zoom = from.Zoom;
        to.PanX = from.PanX;
        to.PanY = from.PanY;
        to.Brightness = from.Brightness;
        to.Contrast = from.Contrast;
    }

    // ---------------- Audio track (music / narration at fixed timeline times) ----------------

    /// <summary>Place the whole of an audio file on the audio track, starting at timeline time <paramref name="start"/>.</summary>
    public static AudioItem AddAudio(Project p, SourceMedia media, TimeSpan start)
    {
        if (!media.IsAudioOnly) throw new ArgumentException("Only pure audio files go on the audio track.", nameof(media));
        if (p.FindMedia(media.Id) is null) p.Media.Add(media);
        var item = new AudioItem { SourceId = media.Id, Start = Max(start, TimeSpan.Zero), In = TimeSpan.Zero, Out = media.Duration };
        p.AudioTrack.Add(item);
        return item;
    }

    /// <summary>Slide an audio item to a new timeline start (never before 0). Its length doesn't change.</summary>
    public static void MoveAudio(Project p, Guid id, TimeSpan start)
    {
        if (p.FindAudio(id) is { } a) a.Start = Max(start, TimeSpan.Zero);
    }

    /// <summary>
    /// Trim the front of an audio item. Its END stays put on the timeline, like dragging a clip's left
    /// edge: the start moves with the new in point.
    /// </summary>
    public static void TrimAudioIn(Project p, Guid id, TimeSpan newIn)
    {
        if (p.FindAudio(id) is not { } a) return;
        var lo = Max(TimeSpan.Zero, a.In - a.Start); // can't start before timeline 0
        var inPoint = Clamp(newIn, lo, a.Out - MinClipLength);
        a.Start += inPoint - a.In;
        a.In = inPoint;
    }

    public static void TrimAudioOut(Project p, Guid id, TimeSpan newOut)
    {
        if (p.FindAudio(id) is not { } a || p.FindMedia(a.SourceId) is not { } m) return;
        a.Out = Clamp(newOut, a.In + MinClipLength, m.Duration);
    }

    public static void SetAudioVolume(Project p, Guid id, double volume)
    {
        if (p.FindAudio(id) is { } a) a.Volume = double.IsFinite(volume) ? Math.Clamp(volume, 0, MaxClipVolume) : 1.0;
    }

    public static void SetAudioMuted(Project p, Guid id, bool muted)
    {
        if (p.FindAudio(id) is { } a) a.Muted = muted;
    }

    public static void DeleteAudio(Project p, Guid id) => p.AudioTrack.RemoveAll(a => a.Id == id);

    /// <summary>Audio items sounding at timeline time <paramref name="t"/>.</summary>
    public static IEnumerable<AudioItem> AudioAt(Project p, TimeSpan t) =>
        p.AudioTrack.Where(a => a.Start <= t && t < a.End);

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

    /// <summary>
    /// Drag an annotation bar: its length never changes (unless the clip is shorter than it). It belongs
    /// to the clip under its MIDDLE and is pushed fully inside that clip, so near a cut it slides up
    /// against the edge, then hops onto the neighbouring clip once its middle crosses over. Re-anchoring
    /// converts its times to the new clip's source time.
    /// </summary>
    public static void MoveAnnotation(Project p, Guid annotationId, TimeSpan timelineStart, TimeSpan length)
    {
        if (p.Timeline.Count == 0) return;
        var total = TotalDuration(p);
        if (length > total) length = total;
        var start = Clamp(timelineStart, TimeSpan.Zero, total - length); // length never changes
        SetRunRange(p, annotationId, start, start + length);
    }

    /// <summary>
    /// Place an annotation's whole run (all its linked pieces) at a timeline range. The piece
    /// <paramref name="annotationId"/> is kept (so selection survives) and anchored to the clip at
    /// <paramref name="timelineStart"/>; the rest of the run is rebuilt as pieces on following clips.
    /// Used for every annotation-bar drag: move, and either edge.
    /// </summary>
    public static void SetRunRange(Project p, Guid annotationId, TimeSpan timelineStart, TimeSpan timelineEnd)
    {
        if (p.FindAnnotation(annotationId) is not { } a || p.Timeline.Count == 0) return;
        if (a.LinkId is { } link) p.Annotations.RemoveAll(x => x.LinkId == link && x.Id != a.Id);
        a.LinkId = null;

        var total = TotalDuration(p);
        var s = Clamp(timelineStart, TimeSpan.Zero, Max(TimeSpan.Zero, total - MinClipLength));
        var e = Clamp(timelineEnd, s + MinClipLength, total);
        if (Locate(p, s) is not { } hit) return;
        a.ClipId = hit.Clip.Id;
        a.Start = hit.SourceTime;
        a.End = Min(hit.Clip.Out, hit.SourceTime + MinClipLength);
        ExtendAnnotationEnd(p, a.Id, e);
    }

    /// <summary>The timeline range of a whole run (first piece's start to last piece's end).</summary>
    public static (TimeSpan Start, TimeSpan End)? RunRange(Project p, Annotation a)
    {
        var ranges = LinkedPieces(p, a).Select(x => AnnotationRange(p, x)).Where(r => r is not null).Select(r => r!.Value).ToList();
        return ranges.Count == 0 ? null : (ranges.Min(r => r.Start), ranges.Max(r => r.End));
    }

    // ---------------- Linked annotations (one annotation running across cuts) ----------------

    /// <summary>All pieces of <paramref name="a"/>'s run (just <paramref name="a"/> if it isn't linked), in timeline order.</summary>
    public static List<Annotation> LinkedPieces(Project p, Annotation a) =>
        a.LinkId is not { } link ? new List<Annotation> { a }
        : p.Annotations.Where(x => x.LinkId == link).OrderBy(x => ClipIndex(p, x.ClipId)).ThenBy(x => x.Start).ToList();

    /// <summary>Copy look and position from one piece to the rest of its run (not times or clip).</summary>
    public static void SyncLinked(Project p, Guid annotationId)
    {
        if (p.FindAnnotation(annotationId) is not { LinkId: not null } src) return;
        foreach (var m in LinkedPieces(p, src).Where(m => m.Id != src.Id))
        {
            m.Kind = src.Kind;
            (m.X1, m.Y1, m.X2, m.Y2) = (src.X1, src.Y1, src.X2, src.Y2);
            m.Text = src.Text;
            m.StrokeColor = src.StrokeColor;
            m.FillColor = src.FillColor;
            m.StrokeWidth = src.StrokeWidth;
            m.FontSize = src.FontSize;
            m.FontFamily = src.FontFamily;
        }
    }

    /// <summary>Delete an annotation, and the rest of its run if it's linked.</summary>
    public static void DeleteAnnotation(Project p, Guid annotationId)
    {
        if (p.FindAnnotation(annotationId) is not { } a) return;
        if (a.LinkId is { } link) p.Annotations.RemoveAll(x => x.LinkId == link);
        else p.Annotations.Remove(a);
    }

    /// <summary>
    /// Drag the END of an annotation bar to <paramref name="timelineEnd"/>. Past its clip's end it
    /// continues onto the following clip(s) as linked pieces, each anchored to its own footage.
    /// Pieces of the run on later clips are rebuilt each call, so dragging back removes them.
    /// </summary>
    public static void ExtendAnnotationEnd(Project p, Guid annotationId, TimeSpan timelineEnd)
    {
        if (p.FindAnnotation(annotationId) is not { } a) return;
        int idx = ClipIndex(p, a.ClipId);
        if (idx < 0) return;
        if (a.LinkId is { } link)
            p.Annotations.RemoveAll(x => x.LinkId == link && x.Id != a.Id && ClipIndex(p, x.ClipId) > idx);

        var clip = p.Timeline[idx];
        var clipStart = ClipStart(p, idx);
        var clipEnd = clipStart + clip.Duration;
        var startTl = clipStart + (a.Start - clip.In);
        if (timelineEnd <= clipEnd || idx == p.Timeline.Count - 1)
        {
            RetimeAnnotation(p, annotationId, startTl, timelineEnd);
            TidyLink(p, a.LinkId);
            return;
        }

        a.End = clip.Out;
        a.LinkId ??= Guid.NewGuid();
        var remaining = timelineEnd - clipEnd;
        for (int k = idx + 1; k < p.Timeline.Count && remaining >= MinClipLength; k++)
        {
            var c = p.Timeline[k];
            var len = Min(remaining, c.Duration);
            AddPiece(p, a, c, c.In, c.In + len);
            remaining -= len;
        }
        TidyLink(p, a.LinkId);
    }

    /// <summary>Mirror of <see cref="ExtendAnnotationEnd"/> for the START edge: continues onto earlier clips.</summary>
    public static void ExtendAnnotationStart(Project p, Guid annotationId, TimeSpan timelineStart)
    {
        if (p.FindAnnotation(annotationId) is not { } a) return;
        int idx = ClipIndex(p, a.ClipId);
        if (idx < 0) return;
        if (a.LinkId is { } link)
            p.Annotations.RemoveAll(x => x.LinkId == link && x.Id != a.Id && ClipIndex(p, x.ClipId) < idx);

        var clip = p.Timeline[idx];
        var clipStart = ClipStart(p, idx);
        var endTl = clipStart + (a.End - clip.In);
        if (timelineStart >= clipStart || idx == 0)
        {
            RetimeAnnotation(p, annotationId, timelineStart, endTl);
            TidyLink(p, a.LinkId);
            return;
        }

        a.Start = clip.In;
        a.LinkId ??= Guid.NewGuid();
        var remaining = clipStart - timelineStart;
        for (int k = idx - 1; k >= 0 && remaining >= MinClipLength; k--)
        {
            var c = p.Timeline[k];
            var len = Min(remaining, c.Duration);
            AddPiece(p, a, c, c.Out - len, c.Out);
            remaining -= len;
        }
        TidyLink(p, a.LinkId);
    }

    static void AddPiece(Project p, Annotation from, Clip clip, TimeSpan start, TimeSpan end)
    {
        var piece = from.Clone();
        piece.Id = Guid.NewGuid();
        piece.ClipId = clip.Id;
        piece.Start = start;
        piece.End = end;
        p.Annotations.Add(piece);
    }

    /// <summary>A run with only one piece left is just a normal annotation again.</summary>
    static void TidyLink(Project p, Guid? link)
    {
        if (link is null) return;
        var members = p.Annotations.Where(x => x.LinkId == link).ToList();
        if (members.Count == 1) members[0].LinkId = null;
    }

    static int ClipIndex(Project p, Guid clipId) => p.Timeline.FindIndex(c => c.Id == clipId);

    /// <summary>Start of the next clip after <paramref name="t"/> (or the timeline end), for "next clip".</summary>
    public static TimeSpan NextClipBoundary(Project p, TimeSpan t) =>
        Boundaries(p).FirstOrDefault(b => b > t + BoundaryTolerance, TotalDuration(p));

    /// <summary>
    /// Start of the clip before <paramref name="t"/>: the current clip's start if you're inside it,
    /// else the previous clip's. For "previous clip".
    /// </summary>
    public static TimeSpan PreviousClipBoundary(Project p, TimeSpan t) =>
        Boundaries(p).LastOrDefault(b => b < t - BoundaryTolerance, TimeSpan.Zero);

    /// <summary>Within this of a cut counts as "on" it, so repeated presses keep moving.</summary>
    static readonly TimeSpan BoundaryTolerance = TimeSpan.FromMilliseconds(50);

    static IEnumerable<TimeSpan> Boundaries(Project p)
    {
        var t = TimeSpan.Zero;
        foreach (var c in p.Timeline)
        {
            yield return t;
            t += c.Duration;
        }
        yield return t;
    }

    static TimeSpan Clamp(TimeSpan v, TimeSpan lo, TimeSpan hi) => v < lo ? lo : v > hi ? hi : v;
    static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
