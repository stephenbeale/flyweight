namespace Flyweight.Core.Model;

/// <summary>
/// Where a project's files live. A new project gets a folder of its own named after it, with the project file and a
/// Thumbnails and a Freeze frames folder inside (owner, 2026-11-28):
/// <code>
/// My video\
///   My video.flyweight
///   Thumbnails\
///   Freeze frames\
/// </code>
/// Projects saved before this (the .flyweight file loose beside the footage) keep working: their folders are made
/// beside the project file the first time something is saved into them.
/// </summary>
public static class ProjectFolders
{
    public const string ThumbnailsName = "Thumbnails";
    public const string FreezeFramesName = "Freeze frames";

    /// <summary>The project file for a new project called <paramref name="name"/>, inside its own folder under <paramref name="parent"/>.</summary>
    public static string NewProjectPath(string parent, string name) =>
        Path.Combine(parent, name, name + ProjectSerializer.FileExtension);

    /// <summary>The project's own folder (the one holding the project file).</summary>
    public static string ProjectDirectory(string projectFilePath) =>
        Path.GetDirectoryName(Path.GetFullPath(projectFilePath))!;

    /// <summary>Where YouTube thumbnails for this project go.</summary>
    public static string ThumbnailsDirectory(string projectFilePath) =>
        Path.Combine(ProjectDirectory(projectFilePath), ThumbnailsName);

    /// <summary>Where freeze-frame stills grabbed for this project go.</summary>
    public static string FreezeFramesDirectory(string projectFilePath) =>
        Path.Combine(ProjectDirectory(projectFilePath), FreezeFramesName);

    /// <summary>Make the project folder's subfolders (safe to call again).</summary>
    public static void CreateSubfolders(string projectFilePath)
    {
        Directory.CreateDirectory(ThumbnailsDirectory(projectFilePath));
        Directory.CreateDirectory(FreezeFramesDirectory(projectFilePath));
    }

    /// <summary>
    /// Where a NEW project's folder should go, given the one open now: beside the current project's own folder
    /// (so "New" after "My video\My video.flyweight" doesn't nest inside "My video"), else beside a loose project
    /// file, else <paramref name="fallback"/>.
    /// </summary>
    public static string DefaultParent(string? currentProjectPath, string fallback)
    {
        if (string.IsNullOrEmpty(currentProjectPath)) return fallback;
        var dir = ProjectDirectory(currentProjectPath);
        bool ownFolder = string.Equals(Path.GetFileName(dir), Path.GetFileNameWithoutExtension(currentProjectPath), StringComparison.OrdinalIgnoreCase);
        return (ownFolder ? Path.GetDirectoryName(dir) : dir) is { Length: > 0 } parent ? parent : fallback;
    }
}
