using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flyweight.Core.Model;

public static class ProjectSerializer
{
    public const string FileExtension = ".flyweight";

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(Project p) => JsonSerializer.Serialize(p, Options);

    public static Project Deserialize(string json) =>
        JsonSerializer.Deserialize<Project>(json, Options) ?? new Project();

    public static Project Load(string path) => Deserialize(File.ReadAllText(path));

    /// <summary>Atomic save: write a temp file then swap, so a crash never leaves a half-written project.</summary>
    public static void Save(Project p, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Serialize(p));
        File.Move(tmp, path, overwrite: true);
    }
}
