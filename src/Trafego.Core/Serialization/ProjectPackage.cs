using System.IO.Compression;
using System.Text.Json;
using Trafego.Core.Model;
using Trafego.Core.Results;

namespace Trafego.Core.Serialization;

/// <summary>Descrição do pacote, gravada em <c>pacote.json</c> dentro do zip.</summary>
public sealed class PackageInfo
{
    public int Format { get; set; } = ProjectPackage.CurrentFormat;
    /// <summary>Versão do programa que gerou o pacote (ex.: "1.0.0").</summary>
    public string AppVersion { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    /// <summary>Nome do arquivo de projeto dentro do pacote.</summary>
    public string Project { get; set; } = "";
    /// <summary>Nome da foto dentro do pacote, ou nulo se o projeto não tem foto.</summary>
    public string? Image { get; set; }
    public int ResultFiles { get; set; }
}

public sealed record PackageExport(bool ImageMissing, int ResultFiles);

/// <summary>
/// Pacote para mandar o projeto a outra pessoa: um zip com o projeto, a foto aérea e, se quiser, os
/// resultados das avaliações. A foto vai com o nome original (a importação de cenários reconhece a mesma
/// foto pelo nome) e o projeto aponta para ela na mesma pasta.
/// </summary>
public static class ProjectPackage
{
    public const string Extension = ".trafego.zip";
    public const int CurrentFormat = 1;
    private const string InfoEntry = "pacote.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static bool IsPackage(string path) =>
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>Nome do projeto a partir do arquivo (sem ".trafego.json" ou ".trafego.zip").</summary>
    public static string BaseName(string path)
    {
        var name = Path.GetFileName(path);
        foreach (var ext in new[] { ProjectFile.Extension, Extension, ".zip", ".json" })
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return name[..^ext.Length];
        return Path.GetFileNameWithoutExtension(name);
    }

    /// <summary>
    /// Grava o pacote. <paramref name="projectPath"/> é onde o projeto está salvo (para achar a pasta de
    /// resultados); pode ser nulo para um projeto nunca salvo.
    /// </summary>
    public static PackageExport Export(Project project, string? projectPath, string zipPath, bool includeResults, string appVersion)
    {
        var name = projectPath is not null ? BaseName(projectPath) : BaseName(zipPath);
        var copy = ProjectFile.FromJson(ProjectFile.ToJson(project));
        string? image = null;
        var imageMissing = false;
        string? imageSource = null;
        if (copy.Background is { ImagePath.Length: > 0 } bg)
        {
            if (File.Exists(bg.ImagePath))
            {
                imageSource = bg.ImagePath;
                image = Path.GetFileName(bg.ImagePath);
                bg.ImagePath = image;
            }
            else imageMissing = true;
        }

        var results = includeResults && projectPath is not null && Directory.Exists(ResultSet.FolderFor(projectPath))
            ? Directory.GetFiles(ResultSet.FolderFor(projectPath), "*" + ResultSet.Extension)
            : [];
        var info = new PackageInfo
        {
            AppVersion = appVersion,
            Project = name + ProjectFile.Extension,
            Image = image,
            ResultFiles = results.Length,
        };

        // Grava ao lado e troca no fim: um erro no meio não deixa um pacote pela metade.
        var temp = zipPath + ".tmp";
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            Write(zip, InfoEntry, JsonSerializer.Serialize(info, Options));
            Write(zip, info.Project, ProjectFile.ToJson(copy));
            if (imageSource is not null) zip.CreateEntryFromFile(imageSource, image!, CompressionLevel.Optimal);
            var folder = name + ".resultados/";
            foreach (var r in results) zip.CreateEntryFromFile(r, folder + Path.GetFileName(r), CompressionLevel.Optimal);
        }
        File.Move(temp, zipPath, overwrite: true);
        return new PackageExport(imageMissing, results.Length);
    }

    /// <summary>Lê a descrição do pacote sem extrair nada.</summary>
    public static PackageInfo ReadInfo(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry(InfoEntry) ?? throw new InvalidDataException("Este zip não é um pacote do daRosa Tráfego (falta pacote.json).");
        using var reader = new StreamReader(entry.Open());
        var info = JsonSerializer.Deserialize<PackageInfo>(reader.ReadToEnd(), Options)
                   ?? throw new InvalidDataException("pacote.json vazio.");
        if (info.Format > CurrentFormat)
            throw new InvalidDataException("O pacote foi feito por uma versão mais nova do programa. Atualize o daRosa Tráfego.");
        if (zip.GetEntry(info.Project) is null) throw new InvalidDataException($"O pacote não tem o projeto {info.Project}.");
        return info;
    }

    /// <summary>
    /// Extrai o pacote numa pasta (criada se não existir) e devolve o caminho do projeto extraído.
    /// Não sobrescreve arquivos: a pasta deve estar vazia ou não existir.
    /// </summary>
    public static string Extract(string zipPath, string folder)
    {
        var info = ReadInfo(zipPath);
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            throw new IOException($"A pasta {folder} não está vazia.");
        var root = Path.GetFullPath(folder);
        Directory.CreateDirectory(root);
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName == InfoEntry || entry.FullName.EndsWith('/')) continue;
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            // Nada pode sair da pasta escolhida (nomes como "../x" num zip malfeito).
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Caminho inválido no pacote: {entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target);
        }
        return Path.Combine(root, info.Project);
    }

    /// <summary>Uma pasta que ainda não existe: "base", "base (2)", "base (3)"…</summary>
    public static string FreeFolder(string parent, string name)
    {
        var path = Path.Combine(parent, name);
        for (var i = 2; Directory.Exists(path) || File.Exists(path); i++) path = Path.Combine(parent, $"{name} ({i})");
        return path;
    }

    private static void Write(ZipArchive zip, string name, string text)
    {
        using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open());
        w.Write(text);
    }
}
