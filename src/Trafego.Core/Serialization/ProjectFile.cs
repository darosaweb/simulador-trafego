using System.Text.Json;
using System.Text.Json.Nodes;
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
        var node = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("Arquivo de projeto vazio.");
        var format = Prop(node, "format")?.GetValue<int>() ?? 1;
        if (format > Project.CurrentFormat)
            throw new InvalidDataException(
                $"Projeto no formato {format}, mais novo que o suportado ({Project.CurrentFormat}).");
        // Formato 1: uma rede só, que vira o cenário "Atual".
        if (Prop(node, "scenarios") is null && Prop(node, "network") is { } network)
        {
            node.Remove(Key(node, "network")!);
            node["scenarios"] = new JsonArray(new JsonObject { ["name"] = "Atual", ["network"] = network });
        }
        var project = node.Deserialize<Project>(Options) ?? throw new InvalidDataException("Arquivo de projeto vazio.");
        if (project.Scenarios.Count == 0) project.Scenarios.Add(new Scenario());
        project.ActiveScenario = Math.Clamp(project.ActiveScenario, 0, project.Scenarios.Count - 1);
        project.Format = Project.CurrentFormat;
        return project;
    }

    private static string? Key(JsonObject node, string name) =>
        node.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

    private static JsonNode? Prop(JsonObject node, string name) => Key(node, name) is { } k ? node[k] : null;

    /// <summary>Cópia independente de uma rede (mesmos ids).</summary>
    public static Network CloneNetwork(Network network) =>
        JsonSerializer.Deserialize<Network>(JsonSerializer.Serialize(network, Options), Options)!;

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
