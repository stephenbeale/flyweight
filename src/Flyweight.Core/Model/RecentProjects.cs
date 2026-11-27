using System.Text.Json;

namespace Flyweight.Core.Model;

/// <summary>
/// The "Open ▸ recent" list: the last few project files opened, saved or created, newest first.
/// App settings, not project data, so it lives in its own small JSON file (rule 3).
/// </summary>
public static class RecentProjects
{
    public const int Max = 5;

    /// <summary>Put <paramref name="path"/> at the top (moving it if it's already there) and keep the newest <see cref="Max"/>.</summary>
    public static List<string> Add(IEnumerable<string> list, string path)
    {
        var full = Path.GetFullPath(path);
        return [.. list.Where(p => !string.Equals(p, full, StringComparison.OrdinalIgnoreCase))
                   .Prepend(full).Take(Max)];
    }

    public static List<string> Remove(IEnumerable<string> list, string path) =>
        [.. list.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase))];

    /// <summary>Read the list; a missing or damaged file is just an empty list.</summary>
    public static List<string> Load(string file)
    {
        try
        {
            if (!File.Exists(file)) return [];
            var paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(file)) ?? [];
            return [.. paths.Where(p => !string.IsNullOrWhiteSpace(p)).Take(Max)];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    public static void Save(string file, IEnumerable<string> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            File.WriteAllText(file, JsonSerializer.Serialize(list.Take(Max).ToList()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a recent list isn't worth an error */ }
    }
}
