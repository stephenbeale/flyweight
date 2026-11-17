using System.Text.Json.Serialization;

namespace Flyweight.Core.Model;

/// <summary>
/// The whole editable document. Pure data: no file handles, no UI state, no runtime status.
/// Undo/redo works by snapshotting this object to JSON, so keep it serialisable.
/// </summary>
public sealed class Project
{
    public int Version { get; set; } = 1;

    /// <summary>Every source file imported into the media bin.</summary>
    public List<SourceMedia> Media { get; set; } = new();

    /// <summary>Single video track. Order in the list = order on the timeline.</summary>
    public List<Clip> Timeline { get; set; } = new();

    /// <summary>Arrows, boxes and text. Each is anchored to a clip.</summary>
    public List<Annotation> Annotations { get; set; } = new();

    public SourceMedia? FindMedia(Guid id) => Media.FirstOrDefault(m => m.Id == id);
    public Clip? FindClip(Guid id) => Timeline.FirstOrDefault(c => c.Id == id);
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
