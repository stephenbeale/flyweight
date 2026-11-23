using System.Text.Json.Serialization;
using Flyweight.Core.Media;

namespace Flyweight.Core.Model;

/// <summary>
/// The whole editable document. Pure data: no file handles, no UI state, no runtime status.
/// Undo/redo works by snapshotting this object to JSON, so keep it serialisable.
/// </summary>
public sealed class Project
{
    public int Version { get; set; } = 1;

    /// <summary>
    /// Output size chosen when the project was created; the export dialog's default.
    /// Older project files without it load as 1080p.
    /// </summary>
    public ExportResolution OutputResolution { get; set; } = ExportResolution.P1080;

    /// <summary>Every source file imported into the media bin.</summary>
    public List<SourceMedia> Media { get; set; } = new();

    /// <summary>Single video track. Order in the list = order on the timeline.</summary>
    public List<Clip> Timeline { get; set; } = new();

    /// <summary>Arrows, boxes and text. Each is anchored to a clip.</summary>
    public List<Annotation> Annotations { get; set; } = new();

    /// <summary>
    /// Music and narration: pure audio files placed at TIMELINE times, mixed over the clips' own sound.
    /// Older project files load with none.
    /// </summary>
    public List<AudioItem> AudioTrack { get; set; } = new();

    /// <summary>
    /// Video on the overlay tracks (V2, V3…): picture-in-picture or full-frame cutaways placed at TIMELINE times over
    /// the main track, like the audio track. Higher <see cref="OverlayClip.Track"/> draws on top. Older files load with none.
    /// </summary>
    public List<OverlayClip> Overlays { get; set; } = new();

    /// <summary>Chapter markers (for YouTube timestamps). Each is anchored to a clip, like annotations.</summary>
    public List<Marker> Markers { get; set; } = new();

    /// <summary>Title, description and tags for YouTube Studio. Older project files load empty.</summary>
    public Export.YouTubeDetails YouTube { get; set; } = new();

    public SourceMedia? FindMedia(Guid id) => Media.FirstOrDefault(m => m.Id == id);
    public Clip? FindClip(Guid id) => Timeline.FirstOrDefault(c => c.Id == id);
    public AudioItem? FindAudio(Guid id) => AudioTrack.FirstOrDefault(a => a.Id == id);
    public OverlayClip? FindOverlay(Guid id) => Overlays.FirstOrDefault(o => o.Id == id);
    public Annotation? FindAnnotation(Guid id) => Annotations.FirstOrDefault(a => a.Id == id);
    public Marker? FindMarker(Guid id) => Markers.FirstOrDefault(m => m.Id == id);
}

/// <summary>An imported file plus the facts ffprobe told us about it.</summary>
public sealed class SourceMedia
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OriginalPath { get; set; } = "";
    public TimeSpan Duration { get; set; }
    public double Fps { get; set; }

    /// <summary>Display size, i.e. already swapped for rotated phone footage.</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    public bool HasAudio { get; set; }

    /// <summary>Channels in the first audio stream (1 = mono). 0 = unknown (files imported before this was read).</summary>
    public int AudioChannels { get; set; }

    /// <summary>
    /// A still image (photo, screenshot) rather than a video. Its <see cref="Duration"/> is
    /// <see cref="StillMaxDuration"/>: the longest a single image clip can be trimmed out to.
    /// </summary>
    public bool IsStill { get; set; }

    /// <summary>
    /// A pure audio file (MP3, WAV, M4A…): no picture, so it goes on the audio track, not the video
    /// timeline. <see cref="Width"/>/<see cref="Height"/>/<see cref="Fps"/> are 0.
    /// </summary>
    public bool IsAudioOnly { get; set; }

    /// <summary>How long a still image can be held on the timeline (also its proxy length).</summary>
    public static readonly TimeSpan StillMaxDuration = TimeSpan.FromMinutes(5);

    [JsonIgnore] public string DisplayName => Path.GetFileName(OriginalPath);
}

/// <summary>A segment of a source file placed on the timeline.</summary>
public sealed class Clip
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceId { get; set; }

    /// <summary>In point, in source time.</summary>
    public TimeSpan In { get; set; }

    /// <summary>Out point (exclusive), in source time.</summary>
    public TimeSpan Out { get; set; }

    /// <summary>
    /// Gain for the clip's own audio: 1 = as recorded, 0–2 (0–200%). Older project files load as 1.
    /// Kept separate from <see cref="Muted"/> so unmuting restores the level.
    /// </summary>
    public double Volume { get; set; } = 1.0;

    public bool Muted { get; set; }

    /// <summary>
    /// Punch-in zoom of the 16:9 picture: 1 = whole frame, up to <see cref="Media.ClipLook.MaxZoom"/>.
    /// The same for the whole clip (no animation). Older project files load as 1. See <see cref="Media.ClipLook"/>.
    /// </summary>
    public double Zoom { get; set; } = 1.0;

    /// <summary>Where the zoomed view sits left–right: 0 = left edge, 0.5 = centre, 1 = right edge.</summary>
    public double PanX { get; set; } = 0.5;

    /// <summary>Where the zoomed view sits top–bottom: 0 = top, 0.5 = centre, 1 = bottom.</summary>
    public double PanY { get; set; } = 0.5;

    /// <summary>-1 … +1, 0 = as shot. Lifts (or darkens) the mid-tones with a gamma curve, so whites don't blow out.</summary>
    public double Brightness { get; set; }

    /// <summary>-1 … +1, 0 = as shot.</summary>
    public double Contrast { get; set; }

    /// <summary>
    /// Playback speed: 1 = as shot, 0.25–4. Older project files load as 1. Stills are always 1.
    /// In/Out stay in SOURCE time; the clip's length on the timeline is <see cref="Duration"/>.
    /// </summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>How much of the source file the clip uses (Out − In).</summary>
    [JsonIgnore] public TimeSpan SourceLength => Out - In;

    /// <summary>
    /// Length ON THE TIMELINE: <see cref="SourceLength"/> ÷ <see cref="Speed"/>. Everything that lays clips out in
    /// timeline time uses this; convert between the two only with <see cref="SourceAt"/> / <see cref="TimelineOffset"/>.
    /// </summary>
    [JsonIgnore] public TimeSpan Duration => Speed == 1 ? Out - In : TimeSpan.FromTicks((long)Math.Round((Out - In).Ticks / SafeSpeed));

    /// <summary>Source time at <paramref name="offset"/> into the clip on the timeline.</summary>
    public TimeSpan SourceAt(TimeSpan offset) => Speed == 1 ? In + offset : In + TimeSpan.FromTicks((long)Math.Round(offset.Ticks * SafeSpeed));

    /// <summary>How far into the clip, on the timeline, source time <paramref name="source"/> plays.</summary>
    public TimeSpan TimelineOffset(TimeSpan source) =>
        Speed == 1 ? source - In : TimeSpan.FromTicks((long)Math.Round((source - In).Ticks / SafeSpeed));

    /// <summary>Source time covered by <paramref name="timelineLength"/> of this clip on the timeline.</summary>
    public TimeSpan SourceSpan(TimeSpan timelineLength) =>
        Speed == 1 ? timelineLength : TimeSpan.FromTicks((long)Math.Round(timelineLength.Ticks * SafeSpeed));

    double SafeSpeed => double.IsFinite(Speed) && Speed > 0 ? Speed : 1;

    /// <summary>A copy with the same footage and settings (same Id: callers give it a new one).</summary>
    public Clip Clone() => (Clip)MemberwiseClone();

    /// <summary>The gain actually applied (0 when muted).</summary>
    [JsonIgnore] public double EffectiveVolume => Muted ? 0 : Volume;
}

/// <summary>
/// A segment of an audio file on the audio track. Unlike a <see cref="Clip"/> it sits at a fixed
/// TIMELINE time (<see cref="Start"/>), so trimming or reordering video doesn't move it.
/// </summary>
public sealed class AudioItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceId { get; set; }

    /// <summary>Where it starts playing, in timeline time.</summary>
    public TimeSpan Start { get; set; }

    /// <summary>In point, in source time.</summary>
    public TimeSpan In { get; set; }

    /// <summary>Out point (exclusive), in source time.</summary>
    public TimeSpan Out { get; set; }

    /// <summary>Gain 0–2 (0–200%), like <see cref="Clip.Volume"/>.</summary>
    public double Volume { get; set; } = 1.0;

    public bool Muted { get; set; }

    [JsonIgnore] public TimeSpan Duration => Out - In;
    [JsonIgnore] public TimeSpan End => Start + Duration;
    [JsonIgnore] public double EffectiveVolume => Muted ? 0 : Volume;
}

/// <summary>
/// A video (or still) on an overlay track, drawn over the main track at a fixed TIMELINE time (<see cref="Start"/>),
/// so trimming or reordering the main clips doesn't move it. It is scaled to <see cref="Scale"/> of the canvas width,
/// keeps its own shape, and sits with its CENTRE at (<see cref="X"/>, <see cref="Y"/>) on the 16:9 canvas (0–1).
/// See <see cref="Editing.OverlayLayout"/> for the maths shared by preview and export.
/// </summary>
public sealed class OverlayClip
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceId { get; set; }

    /// <summary>1 = V2 (just above the main track), 2 = V3… Higher tracks draw on top.</summary>
    public int Track { get; set; } = 1;

    /// <summary>Where it appears, in timeline time.</summary>
    public TimeSpan Start { get; set; }

    /// <summary>In point, in source time.</summary>
    public TimeSpan In { get; set; }

    /// <summary>Out point (exclusive), in source time.</summary>
    public TimeSpan Out { get; set; }

    /// <summary>Width as a fraction of the canvas width: 1 = full width (a full-frame cutaway for 16:9 footage).</summary>
    public double Scale { get; set; } = 0.35;

    /// <summary>Centre, as a fraction of the canvas (0 = left/top, 1 = right/bottom).</summary>
    public double X { get; set; } = 0.78;
    public double Y { get; set; } = 0.76;

    /// <summary>
    /// Zoom INTO the overlay's own picture, like cropping: 1 = the whole frame, up to 4. The box on screen keeps its
    /// size and place; only less of the source fills it. Older files load as 1. See <see cref="Editing.OverlayLayout.CropRect"/>.
    /// </summary>
    public double Zoom { get; set; } = 1.0;

    /// <summary>Which part of the source shows when zoomed: 0 = left/top edge, 0.5 = centre, 1 = right/bottom edge.</summary>
    public double PanX { get; set; } = 0.5;
    public double PanY { get; set; } = 0.5;

    /// <summary>Gain for its own audio, 0–2 like <see cref="Clip.Volume"/>.</summary>
    public double Volume { get; set; } = 1.0;

    public bool Muted { get; set; }

    [JsonIgnore] public TimeSpan Duration => Out - In;
    [JsonIgnore] public TimeSpan End => Start + Duration;
    [JsonIgnore] public double EffectiveVolume => Muted ? 0 : Volume;
}

/// <summary>
/// A named chapter point. Anchored to a clip in SOURCE time (like annotations), so it stays on the
/// same moment of footage when clips are trimmed or reordered.
/// </summary>
public sealed class Marker
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClipId { get; set; }
    public TimeSpan SourceTime { get; set; }
    public string Title { get; set; } = "";
}
