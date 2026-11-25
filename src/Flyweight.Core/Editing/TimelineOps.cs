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
                return new TimelineHit(i, c, start, c.SourceAt(t - start));
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
        index = Math.Clamp(index, 0, p.Timeline.Count);
        p.Timeline.Insert(index, clip);
        NormaliseLinks(p); // a run split to make room must not bridge the new clip
        InsertGapInTracks(p, ClipStart(p, index), clip.Duration); // overlays and music stay with their footage
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
            foreach (var c in p.Timeline.Where(c => c.SourceId == m.Id && c.SourceLength < MinClipLength))
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

        var right = new Clip { SourceId = left.SourceId, In = cut, Out = left.Out, Volume = left.Volume, Muted = left.Muted, Speed = left.Speed };
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
    public static Clip? InsertFreezeFrame(Project p, TimeSpan t, SourceMedia still, TimeSpan length, bool copyAnnotations = false)
    {
        if (!still.IsStill) throw new ArgumentException("Freeze frames must be still images.", nameof(still));
        if (Locate(p, t) is not { } hit) return null;
        // What's on screen at the frozen moment, captured before the split below moves things around.
        var onScreen = copyAnnotations ? VisibleAt(p, t).Select(a => a.Clone()).ToList() : new List<Annotation>();

        int insertAt = OpenGapAt(p, t, hit);

        if (p.FindMedia(still.Id) is null) p.Media.Add(still);
        var hold = Clamp(length, MinClipLength, still.Duration);
        var clip = new Clip { SourceId = still.Id, In = TimeSpan.Zero, Out = hold };
        CopyLook(hit.Clip, clip); // the grabbed frame is unzoomed and as shot: freeze it as it looked
        p.Timeline.Insert(insertAt, clip);
        InsertGapInTracks(p, ClipStart(p, insertAt), clip.Duration); // overlays and music after it wait for the freeze

        // Copies (not burned in) so they stay editable; they hold for the whole freeze.
        foreach (var a in onScreen)
        {
            a.Id = Guid.NewGuid();
            a.ClipId = clip.Id;
            a.Start = TimeSpan.Zero;
            a.End = hold;
            a.LinkId = null; // an independent copy: editing or deleting it must not touch the original run
            p.Annotations.Add(a);
        }
        NormaliseLinks(p); // the run split at the freeze point must not bridge the freeze clip
        return clip;
    }

    /// <summary>
    /// Make room for a new clip at timeline time <paramref name="t"/> and return the index it should go in: the clip
    /// under <paramref name="t"/> is split there, unless <paramref name="t"/> is at (or within <see cref="MinClipLength"/>
    /// of) one of its ends, where no split is needed.
    /// </summary>
    static int OpenGapAt(Project p, TimeSpan t, TimelineHit hit)
    {
        if (hit.SourceTime - hit.Clip.In < MinClipLength) return hit.Index;               // at the clip's start
        if (hit.Clip.Out - hit.SourceTime < MinClipLength) return hit.Index + 1;          // at its end
        SplitAt(p, t);
        return hit.Index + 1;
    }

    /// <summary>
    /// Insert the whole of <paramref name="media"/> at the playhead: the clip under <paramref name="t"/> is split there
    /// and the rest of the timeline ripples right. Past the end it is appended. The audio track and overlays after the
    /// insert point move right with their footage, as for every other ripple edit (InsertMedia opens the gap).
    /// </summary>
    public static Clip InsertMediaAt(Project p, SourceMedia media, TimeSpan t)
    {
        if (media.IsAudioOnly)
            throw new ArgumentException("Audio files go on the audio track (AddAudio), not the video timeline.", nameof(media));
        int index = Locate(p, t) is { } hit ? OpenGapAt(p, t, hit) : p.Timeline.Count;
        return InsertMedia(p, media, index);
    }

    // ---------------- Separate audio (the clip's sound becomes an audio-track item) ----------------

    /// <summary>Why <see cref="SeparateAudio"/> would refuse this clip, or null if it can be done.</summary>
    public static string? CannotSeparateAudio(Project p, Guid clipId) =>
        p.FindClip(clipId) is not { } c || p.FindMedia(c.SourceId) is not { } m ? "That clip no longer exists."
        : m.IsStill || !m.HasAudio ? "This clip has no sound to separate."
        : Math.Abs(c.Speed - 1) > 0.0005 ? "Set the clip back to 1× speed first: audio-track items play at normal speed."
        : null;

    /// <summary>
    /// Split a clip's sound off onto the audio track: a new audio item plays the same part of the same file at the
    /// clip's timeline position and volume, and the clip itself is muted. From then on the two are independent: the
    /// audio can be moved, trimmed or deleted without touching the picture, and it stays put when clips ripple.
    /// Returns the new item, or null (see <see cref="CannotSeparateAudio"/>).
    /// </summary>
    public static AudioItem? SeparateAudio(Project p, Guid clipId)
    {
        if (CannotSeparateAudio(p, clipId) is not null) return null;
        var c = p.FindClip(clipId)!;
        var item = new AudioItem { SourceId = c.SourceId, Start = ClipStart(p, clipId), In = c.In, Out = c.Out, Volume = c.Volume, Muted = c.Muted };
        p.AudioTrack.Add(item);
        c.Muted = true;
        return item;
    }

    /// <summary>The same for an overlay: its sound goes onto the audio track and the overlay is muted.</summary>
    /// <summary>Why <see cref="SeparateOverlayAudio"/> would refuse this overlay, or null if it can be done.</summary>
    public static string? CannotSeparateOverlayAudio(Project p, Guid overlayId) =>
        p.FindOverlay(overlayId) is not { } o || p.FindMedia(o.SourceId) is not { } m ? "That overlay no longer exists."
        : m.IsStill || !m.HasAudio ? "This overlay has no sound to separate."
        : Math.Abs(o.Speed - 1) > 0.0005 ? "Set the overlay back to 1× speed first: audio-track items play at normal speed."
        : null;

    public static AudioItem? SeparateOverlayAudio(Project p, Guid overlayId)
    {
        if (p.FindOverlay(overlayId) is not { } o || p.FindMedia(o.SourceId) is not { HasAudio: true, IsStill: false }) return null;
        if (Math.Abs(o.Speed - 1) > 0.0005) return null; // audio-track items play at normal speed
        var item = new AudioItem { SourceId = o.SourceId, Start = o.Start, In = o.In, Out = o.Out, Volume = o.Volume, Muted = o.Muted };
        p.AudioTrack.Add(item);
        o.Muted = true;
        return item;
    }

    // ---------------- Overlay tracks (V2, V3…: picture-in-picture / cutaways at fixed timeline times) ----------------

    public const int MaxOverlayTracks = 4;

    /// <summary>Default overlay size: a picture-in-picture about a third of the screen wide, bottom right.</summary>
    public const double DefaultOverlayScale = 0.35;

    /// <summary>Put the whole of a video or image on overlay track <paramref name="track"/>, starting at <paramref name="start"/>.</summary>
    public static OverlayClip AddOverlay(Project p, SourceMedia media, TimeSpan start, int track = 1)
    {
        if (media.IsAudioOnly) throw new ArgumentException("Audio files go on the audio track, not an overlay track.", nameof(media));
        if (p.FindMedia(media.Id) is null) p.Media.Add(media);
        var o = new OverlayClip
        {
            SourceId = media.Id, Track = Math.Clamp(track, 1, MaxOverlayTracks), Start = Max(start, TimeSpan.Zero),
            In = TimeSpan.Zero, Out = media.IsStill ? DefaultStillLength : media.Duration,
        };
        (o.X, o.Y, o.Scale) = OverlayLayout.Place(OverlayPosition.BottomRight, DefaultOverlayScale, media, o, Canvas.Aspect(p.Shape));
        p.Overlays.Add(o);
        return o;
    }

    /// <summary>The lowest overlay track with nothing on it between <paramref name="start"/> and +<paramref name="length"/> (the top track if all are busy).</summary>
    public static int FreeOverlayTrack(Project p, TimeSpan start, TimeSpan length)
    {
        var end = start + length;
        for (int t = 1; t <= MaxOverlayTracks; t++)
            if (!p.Overlays.Any(o => o.Track == t && o.Start < end && start < o.End)) return t;
        return MaxOverlayTracks;
    }

    /// <summary>Slide an overlay to a new start (never before 0) and, if given, onto another track.</summary>
    public static void MoveOverlay(Project p, Guid id, TimeSpan start, int? track = null)
    {
        if (p.FindOverlay(id) is not { } o) return;
        o.Start = Max(start, TimeSpan.Zero);
        if (track is { } t) o.Track = Math.Clamp(t, 1, MaxOverlayTracks);
    }

    /// <summary>Trim the front; the END stays put on the timeline (like an audio item).</summary>
    public static void TrimOverlayIn(Project p, Guid id, TimeSpan newIn)
    {
        if (p.FindOverlay(id) is not { } o) return;
        var end = o.End;
        var lo = Max(TimeSpan.Zero, o.In - o.SourceSpan(o.Start)); // can't start before 0 on the timeline
        var inPoint = Clamp(newIn, lo, o.Out - o.SourceSpan(MinClipLength));
        o.In = inPoint;
        o.Start = Max(TimeSpan.Zero, end - o.Duration);
    }

    public static void TrimOverlayOut(Project p, Guid id, TimeSpan newOut)
    {
        if (p.FindOverlay(id) is not { } o || p.FindMedia(o.SourceId) is not { } m) return;
        o.Out = Clamp(newOut, o.In + o.SourceSpan(MinClipLength), m.Duration);
    }

    /// <summary>
    /// Play an overlay faster or slower (0.25–4), like a main clip: its In/Out (footage) and start don't change, its
    /// length on the timeline does. Stills have no speed (drag the end to change how long they show).
    /// </summary>
    public static void SetOverlaySpeed(Project p, Guid id, double speed)
    {
        if (p.FindOverlay(id) is not { } o || p.FindMedia(o.SourceId) is { IsStill: true }) return;
        o.Speed = double.IsFinite(speed) ? Math.Clamp(speed, MinSpeed, MaxSpeed) : 1;
    }

    /// <summary>Centre (0–1 each way) and size (fraction of the canvas width). Null leaves that value alone.</summary>
    public static void SetOverlayLayout(Project p, Guid id, double? x, double? y, double? scale)
    {
        if (p.FindOverlay(id) is not { } o) return;
        if (x is { } vx) o.X = double.IsFinite(vx) ? Math.Clamp(vx, 0, 1) : 0.5;
        if (y is { } vy) o.Y = double.IsFinite(vy) ? Math.Clamp(vy, 0, 1) : 0.5;
        if (scale is { } s) o.Scale = double.IsFinite(s) ? Math.Clamp(s, OverlayLayout.MinScale, OverlayLayout.MaxScale) : DefaultOverlayScale;
    }

    /// <summary>
    /// Cut an overlay in two at timeline time <paramref name="t"/>. The right half is a copy (same placement, zoom,
    /// volume) starting at <paramref name="t"/>, so each half can then be moved, trimmed, re-zoomed or deleted on its own.
    /// Returns the new right half, or null if <paramref name="t"/> isn't at least <see cref="MinClipLength"/> inside it.
    /// </summary>
    /// <summary>
    /// Cut EVERY track at timeline time <paramref name="t"/>: the main clip under it, and every overlay and audio-track
    /// item running across it (one call = one undo step). Items whose ends are within <see cref="MinClipLength"/> of
    /// <paramref name="t"/> are left whole. Returns how many pieces were cut.
    /// </summary>
    public static int SplitAllAt(Project p, TimeSpan t)
    {
        int cuts = SplitAt(p, t) is null ? 0 : 1;
        foreach (var o in p.Overlays.Where(o => o.Start < t && t < o.End).ToList())
            if (SplitOverlayAt(p, o.Id, t) is not null) cuts++;
        foreach (var a in p.AudioTrack.Where(a => a.Start < t && t < a.End).ToList())
            if (SplitAudioAt(p, a.Id, t) is not null) cuts++;
        return cuts;
    }

    public static OverlayClip? SplitOverlayAt(Project p, Guid id, TimeSpan t)
    {
        if (p.FindOverlay(id) is not { } o || t - o.Start < MinClipLength || o.End - t < MinClipLength) return null;
        var cut = o.SourceAt(t - o.Start);
        var right = o.Clone(); // same placement, zoom, crop, volume, speed, track
        right.Id = Guid.NewGuid();
        right.Start = t;
        right.In = cut;
        o.Out = cut;
        p.Overlays.Insert(p.Overlays.IndexOf(o) + 1, right); // same stacking order as the original
        return right;
    }

    /// <summary>The same for an audio-track item: two items, each keeping the volume. Null if <paramref name="t"/> is too near an end.</summary>
    public static AudioItem? SplitAudioAt(Project p, Guid id, TimeSpan t)
    {
        if (p.FindAudio(id) is not { } a || t - a.Start < MinClipLength || a.End - t < MinClipLength) return null;
        var cut = a.In + (t - a.Start);
        var right = new AudioItem { SourceId = a.SourceId, Start = t, In = cut, Out = a.Out, Volume = a.Volume, Muted = a.Muted };
        a.Out = cut;
        p.AudioTrack.Insert(p.AudioTrack.IndexOf(a) + 1, right);
        return right;
    }

    /// <summary>Zoom into the overlay's own picture (1–4, like cropping). Keeps where it's panned to.</summary>
    public static void SetOverlayZoom(Project p, Guid id, double zoom)
    {
        if (p.FindOverlay(id) is { } o) o.Zoom = double.IsFinite(zoom) ? Math.Clamp(zoom, 1, OverlayLayout.MaxZoom) : 1;
    }

    /// <summary>Which part of the source shows when zoomed: 0–1 each way, 0.5 = centred. Null leaves that axis alone.</summary>
    public static void SetOverlayPan(Project p, Guid id, double? panX, double? panY)
    {
        if (p.FindOverlay(id) is not { } o) return;
        if (panX is { } x) o.PanX = double.IsFinite(x) ? Math.Clamp(x, 0, 1) : 0.5;
        if (panY is { } y) o.PanY = double.IsFinite(y) ? Math.Clamp(y, 0, 1) : 0.5;
    }

    /// <summary>Snap to a corner, the centre, or full frame.</summary>
    public static void PlaceOverlay(Project p, Guid id, OverlayPosition pos)
    {
        if (p.FindOverlay(id) is not { } o) return;
        // Leaving full frame for a corner: go back to the default picture-in-picture size.
        var scale = pos != OverlayPosition.FullFrame && o.Scale > 0.9 ? DefaultOverlayScale : o.Scale;
        var (x, y, s) = OverlayLayout.Place(pos, scale, p.FindMedia(o.SourceId), o, Canvas.Aspect(p.Shape));
        SetOverlayLayout(p, id, x, y, s);
    }

    public static void SetOverlayVolume(Project p, Guid id, double volume)
    {
        if (p.FindOverlay(id) is { } o) o.Volume = double.IsFinite(volume) ? Math.Clamp(volume, 0, MaxClipVolume) : 1.0;
    }

    public static void SetOverlayMuted(Project p, Guid id, bool muted)
    {
        if (p.FindOverlay(id) is { } o) o.Muted = muted;
    }

    public static void DeleteOverlay(Project p, Guid id) => p.Overlays.RemoveAll(o => o.Id == id);

    /// <summary>Overlays showing at timeline time <paramref name="t"/>, bottom first (track, then the order they were added).</summary>
    public static List<OverlayClip> OverlaysAt(Project p, TimeSpan t) =>
        p.Overlays.Where(o => o.Start <= t && t < o.End).OrderBy(o => o.Track).ToList(); // OrderBy is stable

    /// <summary>
    /// Ripple delete: remove the clip and close the gap. Its annotations and markers go too. With
    /// <paramref name="keepTracksInSync"/> (the default) the overlay tracks and the audio track close the same gap, so
    /// everything after the cut stays lined up with the footage it was placed against (see <see cref="CutRangeFromTracks"/>).
    /// </summary>
    public static void Delete(Project p, Guid clipId, bool keepTracksInSync = true)
    {
        int index = p.Timeline.FindIndex(c => c.Id == clipId);
        if (index < 0) return;
        DetachFromRuns(p, clipId); // pieces either side of a deleted middle clip are no longer one run (needs the clip still there)
        var start = ClipStart(p, index);
        var end = start + p.Timeline[index].Duration;
        p.Timeline.RemoveAt(index);
        p.Annotations.RemoveAll(a => a.ClipId == clipId);
        p.Markers.RemoveAll(m => m.ClipId == clipId);
        if (keepTracksInSync) CutRangeFromTracks(p, start, end);
    }

    /// <summary>
    /// Take timeline range [<paramref name="from"/>, <paramref name="to"/>) out of the overlay and audio tracks, as a
    /// ripple delete does to the main track: items after it slide left by its length, the part of any item inside it
    /// is removed (an item spanning the whole range is cut in two, each half staying with its footage), and leftover
    /// slivers shorter than <see cref="MinClipLength"/> are dropped.
    /// </summary>
    public static void CutRangeFromTracks(Project p, TimeSpan from, TimeSpan to)
    {
        var gap = to - from;
        if (gap <= TimeSpan.Zero) return;

        foreach (var o in p.Overlays.ToList())
        {
            if (o.End <= from) continue;
            if (o.Start >= to) { o.Start -= gap; continue; }
            if (o.Start < from && o.End > to)
            {
                // Spans the cut: the right half starts again at the cut point.
                var right = o.Clone();
                right.Id = Guid.NewGuid();
                right.Start = from;
                right.In = o.SourceAt(to - o.Start);
                o.Out = o.SourceAt(from - o.Start);
                p.Overlays.Insert(p.Overlays.IndexOf(o) + 1, right);
                if (right.Duration < MinClipLength) p.Overlays.Remove(right);
            }
            else if (o.Start < from) o.Out = o.SourceAt(from - o.Start);                    // ends inside the cut
            else if (o.End > to) { o.In = o.SourceAt(to - o.Start); o.Start = from; }       // starts inside the cut
            else { p.Overlays.Remove(o); continue; }                                       // wholly inside
            if (o.Duration < MinClipLength) p.Overlays.Remove(o);
        }

        foreach (var a in p.AudioTrack.ToList())
        {
            if (a.End <= from) continue;
            if (a.Start >= to) { a.Start -= gap; continue; }
            if (a.Start < from && a.End > to)
            {
                var right = new AudioItem { SourceId = a.SourceId, Start = from, In = a.In + (to - a.Start), Out = a.Out, Volume = a.Volume, Muted = a.Muted };
                a.Out = a.In + (from - a.Start);
                p.AudioTrack.Insert(p.AudioTrack.IndexOf(a) + 1, right);
                if (right.Duration < MinClipLength) p.AudioTrack.Remove(right);
            }
            else if (a.Start < from) a.Out -= a.End - from;
            else if (a.End > to) { a.In += to - a.Start; a.Start = from; }
            else { p.AudioTrack.Remove(a); continue; }
            if (a.Duration < MinClipLength) p.AudioTrack.Remove(a);
        }
    }

    /// <summary>
    /// Remove a file from the media bin, with every clip and audio-track item that uses it (and those
    /// clips' annotations). The file on disk is untouched. One call = one undo step.
    /// </summary>
    public static void RemoveMedia(Project p, Guid mediaId)
    {
        foreach (var c in p.Timeline.Where(c => c.SourceId == mediaId).ToList()) Delete(p, c.Id);
        p.AudioTrack.RemoveAll(a => a.SourceId == mediaId);
        p.Overlays.RemoveAll(o => o.SourceId == mediaId);
        p.Media.RemoveAll(m => m.Id == mediaId);
    }

    /// <summary>How many timeline clips, overlays and audio items use this file (to warn before removing it).</summary>
    public static int UsageCount(Project p, Guid mediaId) =>
        p.Timeline.Count(c => c.SourceId == mediaId) + p.AudioTrack.Count(a => a.SourceId == mediaId) +
        p.Overlays.Count(o => o.SourceId == mediaId);

    // ---------------- Copy / paste clips ----------------

    /// <summary>Copied clips (in timeline order) with their annotations: pure data, independent of the project.</summary>
    public sealed record ClipCopy(IReadOnlyList<Clip> Clips, IReadOnlyList<Annotation> Annotations);

    /// <summary>Snapshot the given clips and the annotations on them, for pasting later (even after undo/edits).</summary>
    public static ClipCopy? CopyClips(Project p, IEnumerable<Guid> clipIds)
    {
        var ids = clipIds.ToHashSet();
        var clips = p.Timeline.Where(c => ids.Contains(c.Id)).Select(c => c.Clone()).ToList();
        if (clips.Count == 0) return null;
        var annotations = p.Annotations.Where(a => ids.Contains(a.ClipId)).Select(a => a.Clone()).ToList();
        return new ClipCopy(clips, annotations);
    }

    /// <summary>
    /// Insert fresh copies of <paramref name="copy"/> at timeline position <paramref name="index"/> (clamped); later
    /// clips ripple right. Each copy gets new ids, keeps its settings (trim, volume, speed, zoom…) and its annotations.
    /// Annotations that ran across cuts stay linked only among the pasted pieces. Clips whose media has since been
    /// removed from the project are skipped. Returns the new clips.
    /// </summary>
    public static List<Clip> PasteClips(Project p, ClipCopy copy, int index)
    {
        index = Math.Clamp(index, 0, p.Timeline.Count);
        var pasted = new List<Clip>();
        var newIds = new Dictionary<Guid, Guid>();
        foreach (var src in copy.Clips)
        {
            if (p.FindMedia(src.SourceId) is null) continue;
            var c = src.Clone();
            c.Id = Guid.NewGuid();
            newIds[src.Id] = c.Id;
            p.Timeline.Insert(index + pasted.Count, c);
            pasted.Add(c);
        }
        var links = new Dictionary<Guid, Guid>();
        foreach (var src in copy.Annotations)
        {
            if (!newIds.TryGetValue(src.ClipId, out var clipId)) continue;
            var a = src.Clone();
            a.Id = Guid.NewGuid();
            a.ClipId = clipId;
            // A run across cuts gets a NEW link id, so the copy doesn't merge with the original run.
            if (a.LinkId is { } old) a.LinkId = links.TryGetValue(old, out var nl) ? nl : links[old] = Guid.NewGuid();
            p.Annotations.Add(a);
        }
        foreach (var link in links.Values) TidyLink(p, link); // a run cut down to one piece is a plain annotation
        NormaliseLinks(p); // pasting into the middle of a run splits it
        if (pasted.Count > 0)
            InsertGapInTracks(p, ClipStart(p, index), pasted.Aggregate(TimeSpan.Zero, (sum, c) => sum + c.Duration));
        return pasted;
    }

    public static void Move(Project p, Guid clipId, int newIndex)
    {
        int old = p.Timeline.FindIndex(c => c.Id == clipId);
        if (old < 0) return;
        DetachFromRuns(p, clipId); // a moved clip leaves its runs; what was either side of it splits too
        var clip = p.Timeline[old];
        p.Timeline.RemoveAt(old);
        newIndex = Math.Clamp(newIndex, 0, p.Timeline.Count);
        p.Timeline.Insert(newIndex, clip);
        NormaliseLinks(p);
    }

    /// <summary>
    /// Move a clip's in point (source time). Later clips ripple. With <paramref name="syncFrom"/> (tracks captured by
    /// <see cref="CaptureTracks"/> when the drag began) the overlay and audio tracks ripple with it, from that snapshot,
    /// so dragging back and forth never loses an item: trimming footage off the front cuts the same stretch out of
    /// them, bringing footage back opens a gap in them (see <see cref="CutRangeFromTracks"/>, <see cref="InsertGapInTracks"/>).
    /// </summary>
    public static void TrimIn(Project p, Guid clipId, TimeSpan newIn, TrackSnapshot? syncFrom = null)
    {
        if (p.FindClip(clipId) is not { } c) return;
        var start = ClipStart(p, clipId);
        c.In = Clamp(newIn, TimeSpan.Zero, c.Out - MinClipLength);
        if (syncFrom is { } snap) SyncTracksToTrim(p, snap, clipId, start, atEnd: false);
    }

    /// <summary>Move a clip's out point (source time). Later clips ripple; tracks follow as for <see cref="TrimIn"/>.</summary>
    public static void TrimOut(Project p, Guid clipId, TimeSpan newOut, TrackSnapshot? syncFrom = null)
    {
        if (p.FindClip(clipId) is not { } c || p.FindMedia(c.SourceId) is not { } m) return;
        var start = ClipStart(p, clipId);
        c.Out = Clamp(newOut, c.In + MinClipLength, m.Duration);
        if (syncFrom is { } snap) SyncTracksToTrim(p, snap, clipId, start, atEnd: true);
    }

    /// <summary>The overlay and audio tracks, and one clip's length, at the start of a trim drag.</summary>
    public sealed record TrackSnapshot(IReadOnlyList<OverlayClip> Overlays, IReadOnlyList<AudioItem> Audio, Guid ClipId, TimeSpan ClipLength);

    /// <summary>Snapshot the tracks before trimming <paramref name="clipId"/>, so the drag can keep them in sync.</summary>
    public static TrackSnapshot? CaptureTracks(Project p, Guid clipId) =>
        p.FindClip(clipId) is { } c
            ? new TrackSnapshot(p.Overlays.Select(o => o.Clone()).ToList(), p.AudioTrack.Select(a => a.Clone()).ToList(), clipId, c.Duration)
            : null;

    /// <summary>Restore the tracks from the snapshot, then cut or open the gap the trim made, at the trimmed edge.</summary>
    static void SyncTracksToTrim(Project p, TrackSnapshot snap, Guid clipId, TimeSpan start, bool atEnd)
    {
        if (snap.ClipId != clipId || p.FindClip(clipId) is not { } c) return;
        p.Overlays.Clear();
        p.Overlays.AddRange(snap.Overlays.Select(o => o.Clone()));
        p.AudioTrack.Clear();
        p.AudioTrack.AddRange(snap.Audio.Select(a => a.Clone()));
        var change = c.Duration - snap.ClipLength; // + = longer
        var edge = atEnd ? start + snap.ClipLength : start;
        if (change < TimeSpan.Zero) CutRangeFromTracks(p, atEnd ? edge + change : edge, atEnd ? edge : edge - change);
        else if (change > TimeSpan.Zero) InsertGapInTracks(p, edge, change);
    }

    /// <summary>
    /// Open a gap of <paramref name="length"/> at timeline time <paramref name="at"/> in the overlay and audio tracks,
    /// as inserting footage on the main track does: items from there on slide right, and an item running across
    /// <paramref name="at"/> is split so each part stays with its footage.
    /// </summary>
    public static void InsertGapInTracks(Project p, TimeSpan at, TimeSpan length)
    {
        if (length <= TimeSpan.Zero) return;
        foreach (var o in p.Overlays.ToList())
        {
            // Within MinClipLength of an end, don't leave a sliver: move the whole item, or leave it whole.
            if (o.Start >= at || at - o.Start < MinClipLength) { o.Start += length; continue; }
            if (o.End <= at || o.End - at < MinClipLength) continue;
            var right = o.Clone();
            right.Id = Guid.NewGuid();
            right.In = o.SourceAt(at - o.Start);
            right.Start = at + length;
            o.Out = right.In;
            p.Overlays.Insert(p.Overlays.IndexOf(o) + 1, right);
        }
        foreach (var a in p.AudioTrack.ToList())
        {
            if (a.Start >= at || at - a.Start < MinClipLength) { a.Start += length; continue; }
            if (a.End <= at || a.End - at < MinClipLength) continue;
            var right = a.Clone();
            right.Id = Guid.NewGuid();
            right.In = a.In + (at - a.Start);
            right.Start = at + length;
            a.Out = right.In;
            p.AudioTrack.Insert(p.AudioTrack.IndexOf(a) + 1, right);
        }
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

    // ---------------- Speed ----------------

    public const double MinSpeed = 0.25, MaxSpeed = 4.0;

    /// <summary>
    /// The speeds offered in the UI: fine steps near normal speed (owner: "1.25 is quite fast"), coarser further out.
    /// </summary>
    public static readonly double[] SpeedPresets =
    {
        0.25, 0.5, 0.6, 0.7, 0.75, 0.8, 0.85, 0.9, 0.95, 1,
        1.05, 1.1, 1.15, 1.2, 1.25, 1.3, 1.4, 1.5, 1.75, 2, 2.5, 3, 4,
    };

    /// <summary>
    /// Play a clip faster or slower (clamped to 0.25–4). Its In/Out don't change, so its annotations stay on the same
    /// footage; its length on the timeline does, and later clips ripple. Stills have no speed (use their length).
    /// </summary>
    public static void SetClipSpeed(Project p, Guid clipId, double speed)
    {
        if (p.FindClip(clipId) is not { } c || p.FindMedia(c.SourceId) is { IsStill: true }) return;
        var start = ClipStart(p, clipId);
        var before = c.Duration;
        c.Speed = double.IsFinite(speed) ? Math.Clamp(speed, MinSpeed, MaxSpeed) : 1;
        // The clip's END moves; overlays and music after it move with the footage that follows (as for a trim).
        var change = c.Duration - before;
        var oldEnd = start + before;
        if (change < TimeSpan.Zero) CutRangeFromTracks(p, oldEnd + change, oldEnd);
        else if (change > TimeSpan.Zero) InsertGapInTracks(p, oldEnd, change);
    }

    /// <summary>What fills the bars around clips that aren't 16:9, for the whole project (D40).</summary>
    public static void SetBackgroundFill(Project p, BackgroundFill fill) => p.Background = fill;

    /// <summary>
    /// Landscape 16:9 or vertical 9:16 (D41). Positions are normalised, so everything keeps its place as a fraction of
    /// the frame; arrows, boxes and overlays change shape on screen and may need moving (the UI warns first).
    /// </summary>
    public static void SetCanvasShape(Project p, CanvasShape shape) => p.Shape = shape;

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
        to.CropLeft = from.CropLeft;
        to.CropTop = from.CropTop;
        to.CropRight = from.CropRight;
        to.CropBottom = from.CropBottom;
    }

    // ---------------- Crop (main clips and overlays) ----------------

    /// <summary>Cut <paramref name="amount"/> (0–0.45 of the source) off one edge of a clip. Undo-able like any edit.</summary>
    public static void SetClipCrop(Project p, Guid clipId, CropSide side, double amount)
    {
        if (p.FindClip(clipId) is not { } c) return;
        var v = ClipLook.CleanCrop(amount);
        switch (side)
        {
            case CropSide.Left: c.CropLeft = v; break;
            case CropSide.Top: c.CropTop = v; break;
            case CropSide.Right: c.CropRight = v; break;
            default: c.CropBottom = v; break;
        }
    }

    /// <summary>
    /// The same for an overlay. Only that edge of its box moves: the picture stays exactly where it is on screen and the
    /// cut part just disappears (the centre shifts by half the change to make that so).
    /// </summary>
    public static void SetOverlayCrop(Project p, Guid overlayId, CropSide side, double amount)
    {
        if (p.FindOverlay(overlayId) is not { } o) return;
        var v = ClipLook.CleanCrop(amount);
        // Size of the uncropped picture on the canvas: the crop fractions are of this.
        double fullW = o.Scale, fullH = o.Scale * Canvas.Aspect(p.Shape) / OverlayLayout.Aspect(p.FindMedia(o.SourceId));
        switch (side)
        {
            case CropSide.Left: o.X += (v - ClipLook.CleanCrop(o.CropLeft)) * fullW / 2; o.CropLeft = v; break;
            case CropSide.Right: o.X -= (v - ClipLook.CleanCrop(o.CropRight)) * fullW / 2; o.CropRight = v; break;
            case CropSide.Top: o.Y += (v - ClipLook.CleanCrop(o.CropTop)) * fullH / 2; o.CropTop = v; break;
            default: o.Y -= (v - ClipLook.CleanCrop(o.CropBottom)) * fullH / 2; o.CropBottom = v; break;
        }
    }

    /// <summary>
    /// Drag one edge of an overlay's box to <paramref name="edge"/> (a canvas fraction: across for Left/Right, down for
    /// Top/Bottom). What's on screen stays exactly where it is, zoomed or not: the box just shows less (or more) of it.
    /// <see cref="SetOverlayCrop"/> alone can't promise that once the overlay is zoomed, because zoom works inside the
    /// crop, so changing the crop also re-maps the zoom; here crop and pan are solved together with zoom and size fixed.
    /// </summary>
    public static void TrimOverlayEdge(Project p, Guid overlayId, CropSide side, double edge)
    {
        if (p.FindOverlay(overlayId) is not { } o || !double.IsFinite(edge)) return;
        var media = p.FindMedia(o.SourceId);
        double ca = Canvas.Aspect(p.Shape);
        var (bx, by, bw, bh) = OverlayLayout.Rect(o, media, ca);
        bool across = side is CropSide.Left or CropSide.Right, fromStart = side is CropSide.Left or CropSide.Top;
        double start = across ? bx : by, end = start + (across ? bw : bh);
        double now = fromStart ? start : end, other = fromStart ? end : start;

        // Past a crop limit the solver would have to move the OTHER edge (or slide the picture). Instead the drag
        // stops at the furthest point where only this edge moves: try the pointer, else search back towards now.
        var best = SolveTrim(o, media, side, now, ca);
        var tryEdge = SolveTrim(o, media, side, edge, ca);
        if (Keeps(tryEdge)) best = tryEdge;
        else
        {
            double lo = now, hi = edge;
            for (int i = 0; i < 40; i++)
            {
                double mid = (lo + hi) / 2;
                var t = SolveTrim(o, media, side, mid, ca);
                if (Keeps(t)) { best = t; lo = mid; } else hi = mid;
            }
        }
        if (best is not { } r) return;
        if (across) { o.CropLeft = r.C0; o.CropRight = r.C1; o.PanX = r.Pan; o.X = r.Centre; }
        else { o.CropTop = r.C0; o.CropBottom = r.C1; o.PanY = r.Pan; o.Y = r.Centre; }

        bool Keeps(Trim? t) => t is { } v && Math.Abs((fromStart ? v.End : v.Start) - other) < 1e-9;
    }

    /// <summary>One axis of an overlay after a trim: start/end crop, pan, box centre and box start/end on the canvas.</summary>
    readonly record struct Trim(double C0, double C1, double Pan, double Centre, double Start, double End);

    /// <summary>
    /// Crop and pan (zoom and size fixed) that put <paramref name="side"/> at <paramref name="edge"/> with the picture
    /// unmoved, clamped to the crop limits. Doesn't change <paramref name="o"/>. Null if the overlay has no size.
    /// </summary>
    static Trim? SolveTrim(OverlayClip o, SourceMedia? media, CropSide side, double edge, double canvasAspect)
    {
        var (bx, by, bw, bh) = OverlayLayout.Rect(o, media, canvasAspect);
        var (vx, vy, vw, vh) = OverlayLayout.CropRect(o); // the part of the source on screen now
        double z = double.IsFinite(o.Zoom) ? Math.Clamp(o.Zoom, 1, OverlayLayout.MaxZoom) : 1, s = 1 / z;
        bool across = side is CropSide.Left or CropSide.Right, fromStart = side is CropSide.Left or CropSide.Top;

        // Along the dragged axis: box start/size on the canvas, visible start/size in the source, and canvas per source.
        double b0 = across ? bx : by, bs = across ? bw : bh, v0 = across ? vx : vy, vs = across ? vw : vh;
        if (bs <= 0 || vs <= 0) return null;
        double mag = bs / vs;
        double minVisible = (1 - 2 * ClipLook.MaxCropEach) * s; // what the tightest allowed crop shows at this zoom

        double nv0 = v0, nvs;
        if (fromStart)
        {
            double cut = Math.Clamp((edge - b0) / mag, -v0, vs - minVisible); // negative = uncrop
            nv0 = v0 + cut;
            nvs = vs - cut;
        }
        else nvs = Math.Clamp((edge - b0) / mag, minVisible, 1 - v0);

        // The crop that shows exactly that at the same zoom (pan placed to keep it where it was).
        double pan = across ? o.PanX : o.PanY;
        pan = double.IsFinite(pan) ? Math.Clamp(pan, 0, 1) : 0.5;
        double cs = Math.Min(1, nvs * z);
        double c0 = nv0 - pan * (1 - s) * cs;
        c0 = Math.Clamp(c0, Math.Max(0, 1 - cs - ClipLook.MaxCropEach), Math.Min(ClipLook.MaxCropEach, 1 - cs));
        double slack = (1 - s) * cs;
        if (slack > 1e-9) pan = Math.Clamp((nv0 - c0) / slack, 0, 1);

        // Where that lands on the canvas.
        double shown0 = c0 + pan * slack, shownSize = s * cs;
        double nb0 = b0 + (shown0 - v0) * mag, nbs = shownSize * mag;
        return new Trim(ClipLook.CleanCrop(c0), ClipLook.CleanCrop(1 - c0 - cs), pan, nb0 + nbs / 2, nb0, nb0 + nbs);
    }

    public static void ResetOverlayCrop(Project p, Guid overlayId)
    {
        if (p.FindOverlay(overlayId) is { } o) o.CropLeft = o.CropTop = o.CropRight = o.CropBottom = 0;
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
        return (clipStart + clip.TimelineOffset(s), clipStart + clip.TimelineOffset(e));
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
            End = Min(hit.SourceTime + hit.Clip.SourceSpan(DefaultAnnotationLength), hit.Clip.Out), // 3 s on the timeline at any speed
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
        };
        if (kind == AnnotationKind.Text)
        {
            a.Text = "Text";
            a.StrokeColor = "#FFFFFFFF";
            a.FillColor = "#B0000000";
        }
        p.Annotations.Add(a);
        // Always the default length on the timeline: near a cut it continues onto the next clip(s) as a
        // linked run instead of being cut short to whatever was left of this clip (which looked random).
        var startTl = hit.ClipStart + hit.Clip.TimelineOffset(a.Start);
        ExtendAnnotationEnd(p, a.Id, Min(startTl + DefaultAnnotationLength, TotalDuration(p)));
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
        var s = Clamp(clip.SourceAt(timelineStart - clipStart), clip.In, clip.Out - MinClipLength);
        var e = Clamp(clip.SourceAt(timelineEnd - clipStart), s + MinClipLength, clip.Out);
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
            m.FadeIn = src.FadeIn;
            m.FadeOut = src.FadeOut;
        }
    }

    // ---------------- Annotation fades ----------------

    /// <summary>Longest fade the UI offers, in seconds.</summary>
    public const double MaxFade = 5;

    /// <summary>
    /// The fade-in and fade-out (seconds) that actually apply to this PIECE: fade in only on the first piece of a run,
    /// fade out only on the last, each clamped so together they never exceed the piece's on-screen length. The preview,
    /// Save frame and the export all use this, so they match.
    /// </summary>
    public static (double In, double Out, TimeSpan Start, TimeSpan End)? FadeFor(Project p, Annotation a)
    {
        if (AnnotationRange(p, a) is not { } r) return null;
        var pieces = LinkedPieces(p, a);
        double fi = pieces[0].Id == a.Id ? Math.Clamp(a.FadeIn, 0, MaxFade) : 0;
        double fo = pieces[^1].Id == a.Id ? Math.Clamp(a.FadeOut, 0, MaxFade) : 0;
        double len = (r.End - r.Start).TotalSeconds;
        if (fi + fo > len && fi + fo > 0) { double k = len / (fi + fo); fi *= k; fo *= k; }
        return (fi, fo, r.Start, r.End);
    }

    /// <summary>Opacity 0–1 of the annotation at timeline time <paramref name="t"/> (1 when it has no fades).</summary>
    public static float AnnotationOpacity(Project p, Annotation a, TimeSpan t)
    {
        if (FadeFor(p, a) is not { } f) return 1f;
        double x = 1;
        double since = (t - f.Start).TotalSeconds, until = (f.End - t).TotalSeconds;
        if (f.In > 0) x = Math.Min(x, since / f.In);
        if (f.Out > 0) x = Math.Min(x, until / f.Out);
        return (float)Math.Clamp(x, 0, 1);
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
        var startTl = clipStart + clip.TimelineOffset(a.Start);
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
            AddPiece(p, a, c, c.In, Min(c.Out, c.In + c.SourceSpan(len)));
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
        var endTl = clipStart + clip.TimelineOffset(a.End);
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
            AddPiece(p, a, c, Max(c.In, c.Out - c.SourceSpan(len)), c.Out);
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

    /// <summary>
    /// A linked run must cover NEIGHBOURING clips. After an insert, paste, delete or move, split any run whose pieces
    /// are no longer on adjacent clips into separate runs (the first keeps its id), and turn one-piece runs back into
    /// plain annotations. Otherwise a run silently bridges a clip it was never on, and the next drag of its bar
    /// spreads it over that clip.
    /// </summary>
    /// <summary>
    /// Before a clip is deleted or moved: cut every run it is part of at that clip. Pieces before it keep the run's
    /// id, pieces after it get a new id, and the clip's own pieces become plain annotations. Needed because once the
    /// clip is gone, its neighbours become adjacent and would otherwise look like one continuous run.
    /// </summary>
    static void DetachFromRuns(Project p, Guid clipId)
    {
        int idx = ClipIndex(p, clipId);
        foreach (var link in p.Annotations.Where(a => a.ClipId == clipId && a.LinkId is not null).Select(a => a.LinkId!.Value).Distinct().ToList())
        {
            var after = Guid.NewGuid();
            foreach (var a in p.Annotations.Where(a => a.LinkId == link))
            {
                if (a.ClipId == clipId) a.LinkId = null;
                else if (ClipIndex(p, a.ClipId) > idx) a.LinkId = after;
            }
            TidyLink(p, link);
            TidyLink(p, after);
        }
    }

    public static void NormaliseLinks(Project p)
    {
        foreach (var group in p.Annotations.Where(a => a.LinkId is not null).GroupBy(a => a.LinkId!.Value).ToList())
        {
            var pieces = group.OrderBy(a => ClipIndex(p, a.ClipId)).ThenBy(a => a.Start).ToList();
            var chains = new List<List<Annotation>> { new() { pieces[0] } };
            for (int i = 1; i < pieces.Count; i++)
            {
                int gap = ClipIndex(p, pieces[i].ClipId) - ClipIndex(p, pieces[i - 1].ClipId);
                if (gap <= 1) chains[^1].Add(pieces[i]); else chains.Add(new() { pieces[i] });
            }
            for (int c = 0; c < chains.Count; c++)
            {
                Guid? id = chains[c].Count == 1 ? null : c == 0 ? group.Key : Guid.NewGuid();
                foreach (var a in chains[c]) a.LinkId = id;
            }
        }
    }

    static int ClipIndex(Project p, Guid clipId) => p.Timeline.FindIndex(c => c.Id == clipId);

    /// <summary>Store the YouTube title/description/tags entered in the export dialog (D31).</summary>
    public static void SetYouTubeDetails(Project p, Export.YouTubeDetails details) => p.YouTube = details.Clone();

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
