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

    public SourceMedia? FindMedia(Guid id) => Media.FirstOrDefault(m => m.Id == id);
    public Clip? FindClip(Guid id) => Timeline.FirstOrDefault(c => c.Id == id);
    public AudioItem? FindAudio(Guid id) => AudioTrack.FirstOrDefault(a => a.Id == id);
    public Annotation? FindAnnotation(Guid id) => Annotations.FirstOrDefault(a => a.Id == id);
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

    [JsonIgnore] public TimeSpan Duration => Out - In;

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
