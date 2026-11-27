using System.Text;

namespace Flyweight.Core.Export;

/// <summary>
/// The video's YouTube details: what goes in Studio's Title, Description and Tags boxes. Saved with the project
/// (so they're there next time) and written as a text file beside the export, ready to copy into Studio.
/// </summary>
public sealed class YouTubeDetails
{
    public string Title { get; set; } = "";

    /// <summary>The description WITHOUT chapters: those come from the chapter markers and are added below it.</summary>
    public string Description { get; set; } = "";

    /// <summary>Comma-separated, as typed into Studio's Tags box.</summary>
    public string Tags { get; set; } = "";

    /// <summary>Write the .txt beside the exported video.</summary>
    public bool SaveTextFile { get; set; } = true;

    public YouTubeDetails Clone() => (YouTubeDetails)MemberwiseClone();
}

/// <summary>
/// YouTube's limits and the "&lt;video&gt; - YouTube.txt" file. Chapters themselves come from the timeline's chapter
/// markers (<see cref="YouTubeChapters"/>) and are appended to the description, which is where YouTube reads them.
/// </summary>
public static class YouTubeText
{
    // YouTube's limits (Studio rejects or truncates beyond these).
    public const int MaxTitle = 100, MaxDescription = 5000, MaxTags = 500;

    /// <summary>The description as it should be pasted into Studio: the user's text, then the chapter lines.</summary>
    public static string FullDescription(YouTubeDetails d, string chapterLines)
    {
        var text = d.Description.TrimEnd();
        if (string.IsNullOrWhiteSpace(chapterLines)) return text;
        return text.Length == 0 ? chapterLines.Trim() : text + Environment.NewLine + Environment.NewLine + chapterLines.Trim();
    }

    /// <summary>Things Studio would reject or cut off, in plain words. Empty when it's all fine.</summary>
    public static List<string> Problems(YouTubeDetails d, string chapterLines = "")
    {
        var list = new List<string>();
        var description = FullDescription(d, chapterLines);
        if (d.Title.Trim().Length == 0) list.Add("The title is empty.");
        if (d.Title.Length > MaxTitle) list.Add($"The title is {d.Title.Length} characters; YouTube allows {MaxTitle}.");
        if (d.Title.Contains('<') || d.Title.Contains('>') || description.Contains('<') || description.Contains('>'))
            list.Add("YouTube doesn't allow < or > in the title or description.");
        if (description.Length > MaxDescription) list.Add($"The description (with chapters) is {description.Length} characters; YouTube allows {MaxDescription}.");
        var tags = TagsLength(d.Tags);
        if (tags > MaxTags) list.Add($"The tags add up to {tags} characters; YouTube allows {MaxTags}.");
        return list;
    }

    /// <summary>How YouTube counts tags: their characters plus the commas between them (a tag with a space counts its quotes too).</summary>
    public static int TagsLength(string tags)
    {
        var list = SplitTags(tags);
        return list.Sum(t => t.Length + (t.Contains(' ') ? 2 : 0)) + Math.Max(0, list.Count - 1);
    }

    public static List<string> SplitTags(string tags) =>
        [.. tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>The text file: labelled sections, so each can be copied straight into Studio's matching box.</summary>
    public static string FileText(YouTubeDetails d, string videoFileName, ChapterList? chapters = null)
    {
        var lines = chapters?.Text ?? "";
        var sb = new StringBuilder();
        sb.AppendLine($"YouTube details for {videoFileName}");
        sb.AppendLine("Copy each part into the matching box in YouTube Studio (https://studio.youtube.com/).");
        sb.AppendLine();
        sb.AppendLine("===== TITLE =====");
        sb.AppendLine(d.Title.Trim());
        sb.AppendLine();
        sb.AppendLine("===== DESCRIPTION =====");
        sb.AppendLine(FullDescription(d, lines));
        sb.AppendLine();
        sb.AppendLine("===== TAGS =====");
        sb.AppendLine(string.Join(", ", SplitTags(d.Tags)));
        var problems = Problems(d, lines);
        if (chapters is { Chapters.Count: > 0 }) problems.AddRange(chapters.Warnings);
        if (problems.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("===== CHECK BEFORE UPLOADING =====");
            foreach (var p in problems) sb.AppendLine("- " + p);
        }
        return sb.ToString();
    }

    /// <summary>"My video.mp4" → "My video - YouTube.txt" in the same folder.</summary>
    public static string FilePathFor(string videoPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(videoPath))!, Path.GetFileNameWithoutExtension(videoPath) + " - YouTube.txt");

    public static string Save(YouTubeDetails d, string videoPath, ChapterList? chapters = null)
    {
        var path = FilePathFor(videoPath);
        File.WriteAllText(path, FileText(d, Path.GetFileName(videoPath), chapters), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }
}
