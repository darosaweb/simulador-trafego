using System.Text.Json;
using Trafego.Core.Model;

namespace Trafego.Core.Serialization;

/// <summary>Leitura e gravação do projeto em JSON (extensão .trafego.json).</summary>
public static class ProjectFile
{
    public const string Extension = ".trafego.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string ToJson(Project project) => JsonSerializer.Serialize(project, Options);

    public static Project FromJson(string json)
    {
        var project = JsonSerializer.Deserialize<Project>(json, Options)
            ?? throw new InvalidDataException("Arquivo de projeto vazio.");
        if (project.Format > Project.CurrentFormat)
            throw new InvalidDataException(
                $"Projeto no formato {project.Format}, mais novo que o suportado ({Project.CurrentFormat}).");
        return project;
    }

    /// <summary>Grava o projeto; o caminho da foto vira relativo à pasta do arquivo.</summary>
    public static void Save(Project project, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var copy = FromJson(ToJson(project));
        if (copy.Background is { ImagePath.Length: > 0 } bg && Path.IsPathRooted(bg.ImagePath))
            bg.ImagePath = Path.GetRelativePath(dir, bg.ImagePath);
        File.WriteAllText(path, ToJson(copy));
    }

    /// <summary>Lê o projeto; o caminho da foto volta a ser absoluto.</summary>
    public static Project Load(string path)
    {
        var project = FromJson(File.ReadAllText(path));
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (project.Background is { ImagePath.Length: > 0 } bg && !Path.IsPathRooted(bg.ImagePath))
            bg.ImagePath = Path.GetFullPath(Path.Combine(dir, bg.ImagePath));
        return project;
    }
}
