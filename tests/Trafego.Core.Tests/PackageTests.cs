using System.IO.Compression;
using Trafego.Core.Model;
using Trafego.Core.Results;
using Trafego.Core.Serialization;

namespace Trafego.Core.Tests;

public sealed class PackageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trafego-pacote-" + Guid.NewGuid().ToString("N"));

    public PackageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>Projeto salvo em "trabalho/Viaduto.trafego.json", com a foto noutra pasta e um resultado.</summary>
    private string SavedProject(bool withResult = true)
    {
        var photos = Directory.CreateDirectory(Path.Combine(_dir, "fotos")).FullName;
        var image = Path.Combine(photos, "viaduto.png");
        File.WriteAllBytes(image, [1, 2, 3, 4]);
        var project = new Project { Background = new Background { ImagePath = image, MetersPerPixel = 0.5, Origin = new(10, 0) } };
        var link = project.Network.AddLink([new(0, 0), new(100, 0)]);
        project.Network.AddInput(link, 900).Name = "Entrada oeste";
        project.Scenarios.Add(new Scenario { Name = "Proposta", Network = ProjectFile.CloneNetwork(project.Network) });

        var work = Directory.CreateDirectory(Path.Combine(_dir, "trabalho")).FullName;
        var path = Path.Combine(work, "Viaduto" + ProjectFile.Extension);
        ProjectFile.Save(project, path);
        if (withResult)
        {
            Directory.CreateDirectory(ResultSet.FolderFor(path));
            File.WriteAllText(Path.Combine(ResultSet.FolderFor(path), "r1" + ResultSet.Extension), "{}");
        }
        return path;
    }

    [Fact]
    public void Export_and_extract_keep_scenarios_photo_and_results()
    {
        var path = SavedProject();
        var zip = Path.Combine(_dir, "Viaduto" + ProjectPackage.Extension);
        var export = ProjectPackage.Export(ProjectFile.Load(path), path, zip, includeResults: true, "1.2.3");
        Assert.False(export.ImageMissing);
        Assert.Equal(1, export.ResultFiles);

        var info = ProjectPackage.ReadInfo(zip);
        Assert.Equal("1.2.3", info.AppVersion);
        Assert.Equal("Viaduto.trafego.json", info.Project);
        Assert.Equal("viaduto.png", info.Image);

        var target = Path.Combine(_dir, "recebido");
        var opened = ProjectPackage.Extract(zip, target);
        var project = ProjectFile.Load(opened);
        Assert.Equal(["Atual", "Proposta"], project.Scenarios.Select(s => s.Name));
        Assert.Equal(Path.Combine(target, "viaduto.png"), project.Background!.ImagePath);
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(project.Background.ImagePath));
        Assert.Equal(0.5, project.Background.MetersPerPixel);
        Assert.Equal("Entrada oeste", project.Network.Inputs[0].Name);
        Assert.True(File.Exists(Path.Combine(ResultSet.FolderFor(opened), "r1" + ResultSet.Extension)));
        Assert.False(File.Exists(Path.Combine(target, "pacote.json")));
    }

    [Fact]
    public void Results_are_optional()
    {
        var path = SavedProject();
        var zip = Path.Combine(_dir, "sem.trafego.zip");
        Assert.Equal(0, ProjectPackage.Export(ProjectFile.Load(path), path, zip, includeResults: false, "1.0.0").ResultFiles);
        using var archive = ZipFile.OpenRead(zip);
        Assert.DoesNotContain(archive.Entries, e => e.FullName.Contains(".resultados/"));
    }

    [Fact]
    public void Missing_photo_is_reported_and_unsaved_project_is_named_after_the_zip()
    {
        var project = new Project { Background = new Background { ImagePath = Path.Combine(_dir, "sumiu.png") } };
        var zip = Path.Combine(_dir, "Rascunho.trafego.zip");
        var export = ProjectPackage.Export(project, null, zip, includeResults: true, "1.0.0");
        Assert.True(export.ImageMissing);
        Assert.Equal("Rascunho.trafego.json", ProjectPackage.ReadInfo(zip).Project);
        Assert.Null(ProjectPackage.ReadInfo(zip).Image);
    }

    [Fact]
    public void Extract_never_overwrites()
    {
        var path = SavedProject(withResult: false);
        var zip = Path.Combine(_dir, "v.trafego.zip");
        ProjectPackage.Export(ProjectFile.Load(path), path, zip, false, "1.0.0");
        var target = Directory.CreateDirectory(Path.Combine(_dir, "ocupada")).FullName;
        File.WriteAllText(Path.Combine(target, "meu.txt"), "x");
        Assert.Throws<IOException>(() => ProjectPackage.Extract(zip, target));

        Assert.Equal(Path.Combine(_dir, "ocupada (2)"), ProjectPackage.FreeFolder(_dir, "ocupada"));
    }

    [Fact]
    public void Rejects_other_zips_and_paths_outside_the_folder()
    {
        var plain = Path.Combine(_dir, "qualquer.zip");
        using (var z = ZipFile.Open(plain, ZipArchiveMode.Create)) z.CreateEntry("a.txt");
        Assert.Throws<InvalidDataException>(() => ProjectPackage.ReadInfo(plain));

        var evil = Path.Combine(_dir, "mau.trafego.zip");
        using (var z = ZipFile.Open(evil, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("pacote.json").Open())) w.Write("{\"format\":1,\"project\":\"p.trafego.json\"}");
            using (var w = new StreamWriter(z.CreateEntry("p.trafego.json").Open())) w.Write(ProjectFile.ToJson(new Project()));
            z.CreateEntry("../fora.txt");
        }
        Assert.Throws<InvalidDataException>(() => ProjectPackage.Extract(evil, Path.Combine(_dir, "alvo")));
        Assert.False(File.Exists(Path.Combine(_dir, "fora.txt")));
    }

    [Fact]
    public void Real_sample_round_trips_with_its_photo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "samples", "cruzamento.trafego.json"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var sample = Path.Combine(dir.FullName, "samples", "cruzamento.trafego.json");
        var original = ProjectFile.Load(sample);

        var zip = Path.Combine(_dir, "cruzamento" + ProjectPackage.Extension);
        Assert.False(ProjectPackage.Export(original, sample, zip, includeResults: false, "1.0.0").ImageMissing);
        var copy = ProjectFile.Load(ProjectPackage.Extract(zip, Path.Combine(_dir, "colega")));

        Assert.True(File.Exists(copy.Background!.ImagePath));
        Assert.Equal(File.ReadAllBytes(original.Background!.ImagePath), File.ReadAllBytes(copy.Background.ImagePath));
        Assert.Equal(original.Scenarios.Select(s => s.Name), copy.Scenarios.Select(s => s.Name));
        Assert.Equal(original.Background.MetersPerPixel, copy.Background.MetersPerPixel);
        // Mesma rede, cenário por cenário (comparando o JSON de cada uma).
        for (var i = 0; i < original.Scenarios.Count; i++)
            Assert.Equal(ProjectFile.ToJson(new Project { Scenarios = [original.Scenarios[i]] }),
                         ProjectFile.ToJson(new Project { Scenarios = [copy.Scenarios[i]] }));
    }

    [Fact]
    public void BaseName_strips_known_extensions()
    {
        Assert.Equal("Viaduto Norte", ProjectPackage.BaseName(@"C:\x\Viaduto Norte.trafego.zip"));
        Assert.Equal("Viaduto Norte", ProjectPackage.BaseName(@"C:\x\Viaduto Norte.trafego.json"));
        Assert.Equal("pacote", ProjectPackage.BaseName("pacote.zip"));
    }
}
