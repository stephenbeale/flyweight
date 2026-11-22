using Flyweight.Core.Model;

namespace Flyweight.Core.Tests;

public class RecentProjectsTests
{
    static string P(string name) => Path.GetFullPath(Path.Combine(Path.GetTempPath(), name + ".flyweight"));

    [Fact]
    public void Newest_first_no_duplicates_at_most_five()
    {
        var list = new List<string>();
        foreach (var n in new[] { "a", "b", "c", "d", "e", "f" }) list = RecentProjects.Add(list, P(n));
        Assert.Equal(new[] { P("f"), P("e"), P("d"), P("c"), P("b") }, list);

        list = RecentProjects.Add(list, P("d"));                  // reopening moves it to the top
        Assert.Equal(new[] { P("d"), P("f"), P("e"), P("c"), P("b") }, list);
        list = RecentProjects.Add(list, P("D").ToUpperInvariant()); // same file, different case (Windows)
        Assert.Equal(5, list.Count);
        Assert.Single(list, x => string.Equals(x, P("d"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Saves_loads_and_survives_a_damaged_file()
    {
        var dir = Directory.CreateTempSubdirectory("fw-recent-").FullName;
        try
        {
            var file = Path.Combine(dir, "recent.json");
            Assert.Empty(RecentProjects.Load(file));
            var list = RecentProjects.Add(RecentProjects.Add(new List<string>(), P("one")), P("two"));
            RecentProjects.Save(file, list);
            Assert.Equal(list, RecentProjects.Load(file));
            Assert.Equal(new[] { P("one") }, RecentProjects.Remove(list, P("two")));

            File.WriteAllText(file, "{ not json");
            Assert.Empty(RecentProjects.Load(file));
        }
        finally { Directory.Delete(dir, true); }
    }
}
