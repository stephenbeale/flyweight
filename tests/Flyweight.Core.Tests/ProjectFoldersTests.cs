using Flyweight.Core.Model;
using Xunit;

namespace Flyweight.Core.Tests;

public class ProjectFoldersTests
{
    static readonly string Root = Path.Combine(Path.GetTempPath(), "fw-folders");

    [Fact]
    public void New_project_gets_its_own_folder_named_after_it()
    {
        var path = ProjectFolders.NewProjectPath(Root, "My video");
        Assert.Equal(Path.Combine(Root, "My video", "My video.flyweight"), path);
        Assert.Equal(Path.Combine(Root, "My video", "Thumbnails"), ProjectFolders.ThumbnailsDirectory(path));
        Assert.Equal(Path.Combine(Root, "My video", "Freeze frames"), ProjectFolders.FreezeFramesDirectory(path));
    }

    [Fact]
    public void Thumbnails_sit_beside_a_loose_project_file_from_before_project_folders()
    {
        var loose = Path.Combine(Root, "Footage", "Old project.flyweight");
        Assert.Equal(Path.Combine(Root, "Footage", "Thumbnails"), ProjectFolders.ThumbnailsDirectory(loose));
    }

    [Fact]
    public void Next_new_project_goes_beside_the_current_projects_folder_not_inside_it()
    {
        var current = ProjectFolders.NewProjectPath(Root, "My video");
        Assert.Equal(Root, ProjectFolders.DefaultParent(current, "fallback"));
        // A loose project file: beside it, as before.
        Assert.Equal(Path.Combine(Root, "Footage"), ProjectFolders.DefaultParent(Path.Combine(Root, "Footage", "Old.flyweight"), "fallback"));
        Assert.Equal("fallback", ProjectFolders.DefaultParent(null, "fallback"));
    }

    [Fact]
    public void Create_subfolders_makes_thumbnails_and_freeze_frames_and_is_repeatable()
    {
        var dir = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        try
        {
            var path = ProjectFolders.NewProjectPath(dir, "Test");
            ProjectFolders.CreateSubfolders(path);
            ProjectFolders.CreateSubfolders(path);
            Assert.True(Directory.Exists(Path.Combine(dir, "Test", "Thumbnails")));
            Assert.True(Directory.Exists(Path.Combine(dir, "Test", "Freeze frames")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }
}
