using Flyweight.Core.Editing;
using Flyweight.Core.Model;

namespace Flyweight.Core.Export;

/// <summary>A chapter list ready to paste into a YouTube description, plus any reasons YouTube would ignore it.</summary>
public sealed record ChapterList(string Text, IReadOnlyList<(TimeSpan Time, string Title)> Chapters, IReadOnlyList<string> Warnings)
{
    /// <summary>True when YouTube's chapter rules are all met, so the chapters will show on the video.</summary>
    public bool YouTubeWillShowChapters => Warnings.Count == 0 && Chapters.Count > 0;
}

/// <summary>
/// Turns chapter markers into YouTube description timestamps:
/// <code>
/// 0:00 Intro
/// 1:23 Setup
/// 12:05 Results
/// </code>
/// YouTube only shows chapters when: the first starts at 0:00, there are at least 3, and each is at
/// least 10 seconds long. We add the 0:00 one if it's missing and warn about the others, rather than
/// silently changing the user's chapters.
/// </summary>
public static class YouTubeChapters
{
    public const int MinChapters = 3;
    public static readonly TimeSpan MinChapterLength = TimeSpan.FromSeconds(10);

    public static ChapterList Build(Project p, string firstChapterTitle = "Intro")
    {
        var total = TimelineOps.TotalDuration(p);
        var markers = MarkerOps.Visible(p);
        if (markers.Count == 0)
            return new ChapterList("", [],
                ["There are no chapter markers yet. Press M at each new section to add one."]);

        // YouTube works in whole seconds; floor so a chapter never starts after the moment it marks.
        var chapters = new List<(TimeSpan Time, string Title)>();
        foreach (var (m, t) in markers)
        {
            var at = TimeSpan.FromSeconds(Math.Floor(t.TotalSeconds));
            var title = string.IsNullOrWhiteSpace(m.Title) ? $"Chapter {chapters.Count + 1}" : m.Title.Trim();
            if (chapters.Count > 0 && chapters[^1].Time == at)
                chapters[^1] = (at, title); // two markers in the same second: the later one wins
            else
                chapters.Add((at, title));
        }
        if (chapters[0].Time > TimeSpan.Zero) chapters.Insert(0, (TimeSpan.Zero, firstChapterTitle));

        var warnings = new List<string>();
        if (chapters.Count < MinChapters)
            warnings.Add($"YouTube needs at least {MinChapters} chapters; you have {chapters.Count}.");
        for (int i = 0; i < chapters.Count; i++)
        {
            var end = i + 1 < chapters.Count ? chapters[i + 1].Time : total;
            var length = end - chapters[i].Time;
            if (length < MinChapterLength)
                warnings.Add($"\"{chapters[i].Title}\" is only {Math.Max(0, (int)length.TotalSeconds)}s long; " +
                             $"YouTube needs every chapter to be at least {(int)MinChapterLength.TotalSeconds}s.");
        }

        bool hours = total >= TimeSpan.FromHours(1);
        var text = string.Join(Environment.NewLine, chapters.Select(c => $"{Format(c.Time, hours)} {c.Title}"));
        return new ChapterList(text, chapters, warnings);
    }

    /// <summary>"0:00", "1:23", "12:05"; or "0:00:00", "1:02:03" when the video is an hour or longer.</summary>
    public static string Format(TimeSpan t, bool hours) =>
        hours ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    /// <summary>Default place for "Save .txt" in the chapters dialog: beside the project file, else Documents\Flyweight.</summary>
    public static string DefaultTextPath(string? projectFilePath)
    {
        var folder = projectFilePath is not null && Path.GetDirectoryName(projectFilePath) is { Length: > 0 } dir
            ? dir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Flyweight");
        var name = projectFilePath is not null ? Path.GetFileNameWithoutExtension(projectFilePath) : "My video";
        return Path.Combine(folder, name + " chapters.txt");
    }
}
