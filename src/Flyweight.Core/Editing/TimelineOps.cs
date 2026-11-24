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
        p.Timeline.Insert(Math.Clamp(index, 0, p.Timeline.Count), clip);
        NormaliseLinks(p); // a run split to make room must not bridge the new clip
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
    /// and the rest of the timeline ripples right. Past the end it is appended. The audio track and overlays stay at
    /// their timeline times, as for every other ripple edit.
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
    public static AudioItem? SeparateOverlayAudio(Project p, Guid overlayId)
    {
        if (p.FindOverlay(overlayId) is not { } o || p.FindMedia(o.SourceId) is not { HasAudio: true, IsStill: false }) return null;
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
        (o.X, o.Y, o.Scale) = OverlayLayout.Place(OverlayPosition.BottomRight, DefaultOverlayScale, media);
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
        var lo = Max(TimeSpan.Zero, o.In - o.Start);
        var inPoint = Clamp(newIn, lo, o.Out - MinClipLength);
        o.Start += inPoint - o.In;
        o.In = inPoint;
    }

    public static void TrimOverlayOut(Project p, Guid id, TimeSpan newOut)
    {
        if (p.FindOverlay(id) is not { } o || p.FindMedia(o.SourceId) is not { } m) return;
        o.Out = Clamp(newOut, o.In + MinClipLength, m.Duration);
    }

    /// <summary>Centre (0–1 each way) and size (fraction of the canvas width). Null leaves that value alone.</summary>
    public static void SetOverlayLayout(Project p, Guid id, double? x, double? y, double? scale)
    {
        if (p.FindOverlay(id) is not { } o) return;
        if (x is { } vx) o.X = double.IsFinite(vx) ? Math.Clamp(vx, 0, 1) : 0.5;
        if (y is { } vy) o.Y = double.IsFinite(vy) ? Math.Clamp(vy, 0, 1) : 0.5;
        if (scale is { } s) o.Scale = double.IsFinite(s) ? Math.Clamp(s, OverlayLayout.MinScale, OverlayLayout.MaxScale) : DefaultOverlayScale;
    }

    /// <summary>Snap to a corner, the centre, or full frame.</summary>
    public static void PlaceOverlay(Project p, Guid id, OverlayPosition pos)
    {
        if (p.FindOverlay(id) is not { } o) return;
        // Leaving full frame for a corner: go back to the default picture-in-picture size.
        var scale = pos != OverlayPosition.FullFrame && o.Scale > 0.9 ? DefaultOverlayScale : o.Scale;
        var (x, y, s) = OverlayLayout.Place(pos, scale, p.FindMedia(o.SourceId));
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

    /// <summary>Ripple delete: remove the clip and close the gap. Its annotations go too.</summary>
    public static void Delete(Project p, Guid clipId)
    {
        DetachFromRuns(p, clipId); // pieces either side of a deleted middle clip are no longer one run
        p.Timeline.RemoveAll(c => c.Id == clipId);
        p.Annotations.RemoveAll(a => a.ClipId == clipId);
        p.Markers.RemoveAll(m => m.ClipId == clipId);
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

    // ---------------- Speed ----------------

    public const double MinSpeed = 0.25, MaxSpeed = 4.0;

    /// <summary>The speeds offered in the UI.</summary>
    public static readonly double[] SpeedPresets = { 0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4 };

    /// <summary>
    /// Play a clip faster or slower (clamped to 0.25–4). Its In/Out don't change, so its annotations stay on the same
    /// footage; its length on the timeline does, and later clips ripple. Stills have no speed (use their length).
    /// </summary>
    public static void SetClipSpeed(Project p, Guid clipId, double speed)
    {
        if (p.FindClip(clipId) is not { } c || p.FindMedia(c.SourceId) is { IsStill: true }) return;
        c.Speed = double.IsFinite(speed) ? Math.Clamp(speed, MinSpeed, MaxSpeed) : 1;
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
