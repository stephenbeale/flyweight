using Flyweight.Core.Model;

namespace Flyweight.Core.Editing;

/// <summary>Chapter markers: add, move, and map between source time and timeline time.</summary>
public static class MarkerOps
{
    /// <summary>Add a marker at timeline time <paramref name="t"/>, anchored to the clip there.</summary>
    public static Marker? Add(Project p, TimeSpan t, string? title = null)
    {
        if (TimelineOps.Locate(p, t) is not { } hit) return null;
        var m = new Marker
        {
            ClipId = hit.Clip.Id,
            SourceTime = hit.SourceTime,
            Title = string.IsNullOrWhiteSpace(title) ? NextDefaultTitle(p) : title.Trim(),
        };
        p.Markers.Add(m);
        return m;
    }

    /// <summary>Re-anchor a marker to whatever is at timeline time <paramref name="t"/> (dragging it).</summary>
    public static void MoveTo(Project p, Guid markerId, TimeSpan t)
    {
        if (p.FindMarker(markerId) is not { } m || TimelineOps.Locate(p, t) is not { } hit) return;
        m.ClipId = hit.Clip.Id;
        m.SourceTime = hit.SourceTime;
    }

    /// <summary>Where the marker sits on the timeline, or null if its clip is gone or it is trimmed out of view.</summary>
    public static TimeSpan? TimelineTime(Project p, Marker m)
    {
        int idx = p.Timeline.FindIndex(c => c.Id == m.ClipId);
        if (idx < 0) return null;
        var clip = p.Timeline[idx];
        if (m.SourceTime < clip.In || m.SourceTime >= clip.Out) return null;
        return TimelineOps.ClipStart(p, idx) + clip.TimelineOffset(m.SourceTime); // speed-aware (D28)
    }

    /// <summary>All visible markers in timeline order.</summary>
    public static List<(Marker Marker, TimeSpan Time)> Visible(Project p) =>
        p.Markers
            .Select(m => (Marker: m, Time: TimelineTime(p, m)))
            .Where(x => x.Time is not null)
            .Select(x => (x.Marker, x.Time!.Value))
            .OrderBy(x => x.Item2)
            .ToList();

    public static string NextDefaultTitle(Project p) => $"Chapter {p.Markers.Count + 1}";

    public static void Rename(Project p, Guid markerId, string title)
    {
        if (p.FindMarker(markerId) is { } m && !string.IsNullOrWhiteSpace(title)) m.Title = title.Trim();
    }

    public static void Delete(Project p, Guid markerId) => p.Markers.RemoveAll(m => m.Id == markerId);
}
