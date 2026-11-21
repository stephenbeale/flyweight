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

    /// <summary>Chapter markers (for YouTube timestamps). Each is anchored to a clip, like annotations.</summary>
    public List<Marker> Markers { get; set; } = new();

    public SourceMedia? FindMedia(Guid id) => Media.FirstOrDefault(m => m.Id == id);
    public Clip? FindClip(Guid id) => Timeline.FirstOrDefault(c => c.Id == id);
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

    /// <summary>
    /// A still image (photo, screenshot) rather than a video. Its <see cref="Duration"/> is
    /// <see cref="StillMaxDuration"/>: the longest a single image clip can be trimmed out to.
    /// </summary>
    public bool IsStill { get; set; }

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

    [JsonIgnore] public TimeSpan Duration => Out - In;
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
