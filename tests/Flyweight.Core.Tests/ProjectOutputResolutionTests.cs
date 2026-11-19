using Flyweight.Core.Editing;
using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

public class ProjectOutputResolutionTests
{
    [Fact]
    public void New_project_defaults_to_1080p()
    {
        Assert.Equal(ExportResolution.P1080, new Project().OutputResolution);
        var session = new ProjectSession();
        session.New();
        Assert.Equal(ExportResolution.P1080, session.Project.OutputResolution);
    }

    [Theory]
    [InlineData(ExportResolution.P1080)]
    [InlineData(ExportResolution.P1440)]
    [InlineData(ExportResolution.P2160)]
    public void Session_New_stores_chosen_size_and_starts_clean(ExportResolution r)
    {
        var session = new ProjectSession();
        session.New(r);
        Assert.Equal(r, session.Project.OutputResolution);
        Assert.False(session.IsDirty);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void Output_size_survives_save_and_load_as_readable_name()
    {
        var json = ProjectSerializer.Serialize(new Project { OutputResolution = ExportResolution.P2160 });
        Assert.Contains("\"OutputResolution\": \"P2160\"", json);
        Assert.Equal(ExportResolution.P2160, ProjectSerializer.Deserialize(json).OutputResolution);
    }

    [Fact]
    public void Project_files_from_before_the_setting_load_as_1080p()
    {
        var legacy = """{ "Version": 1, "Media": [], "Timeline": [], "Annotations": [] }""";
        Assert.Equal(ExportResolution.P1080, ProjectSerializer.Deserialize(legacy).OutputResolution);
    }

    [Fact]
    public void Dropdown_list_index_matches_enum_value()
    {
        var all = ExportResolutions.All;
        Assert.Equal(Enum.GetValues<ExportResolution>().Length, all.Count);
        for (var i = 0; i < all.Count; i++) Assert.Equal(i, (int)all[i].Value);
    }
}
