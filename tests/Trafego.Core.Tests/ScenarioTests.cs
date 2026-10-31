using Trafego.Core.Geometry;
using Trafego.Core.Model;
using Trafego.Core.Serialization;

namespace Trafego.Core.Tests;

public class ScenarioTests
{
    [Fact]
    public void Arquivo_antigo_vira_o_cenario_Atual()
    {
        var json = """
            { "format": 1, "network": { "links": [ { "id": 1, "name": "Av. A", "points": [ { "x": 0, "y": 0 }, { "x": 100, "y": 0 } ],
              "lanes": [ { "width": 3.5 } ] } ], "nextId": 2 }, "simulation": { "durationMinutes": 30 } }
            """;
        var p = ProjectFile.FromJson(json);
        Assert.Single(p.Scenarios);
        Assert.Equal("Atual", p.Active.Name);
        Assert.Equal("Av. A", p.Network.Links.Single().Name);
        Assert.Equal(2, p.Network.NextId);
        Assert.Equal(30, p.Simulation.DurationMinutes);

        var back = ProjectFile.FromJson(ProjectFile.ToJson(p));
        Assert.Equal(Project.CurrentFormat, back.Format);
        Assert.Equal("Av. A", back.Network.Links.Single().Name);
        Assert.DoesNotContain("\"network\": {", ProjectFile.ToJson(p).Split("\"scenarios\"")[0]);
    }

    [Fact]
    public void Duplicar_cria_copia_independente_com_demanda_propria()
    {
        var p = new Project();
        var a = p.Network.AddLink([new(0, 0), new(100, 0)]);
        var input = p.Network.AddInput(a, 500);
        p.DuplicateScenario(0, "Proposta");
        Assert.Equal(1, p.ActiveScenario);
        Assert.Equal("Proposta", p.Active.Name);

        p.Network.FindInput(input.Id)!.VolumeVph = 900;
        p.Network.AddLink([new(0, 50), new(100, 50)]);
        Assert.Equal(500, p.Scenarios[0].Network.FindInput(input.Id)!.VolumeVph);
        Assert.Single(p.Scenarios[0].Network.Links);
        Assert.Equal(2, p.Scenarios[1].Network.Links.Count);

        var back = ProjectFile.FromJson(ProjectFile.ToJson(p));
        Assert.Equal(["Atual", "Proposta"], back.Scenarios.Select(s => s.Name));
        Assert.Equal(1, back.ActiveScenario);
        Assert.Equal(900, back.Network.FindInput(input.Id)!.VolumeVph);
    }

    [Fact]
    public void Nomes_nao_se_repetem_e_excluir_mantem_um()
    {
        var p = new Project();
        p.DuplicateScenario(0, "Atual");
        Assert.Equal("Atual (2)", p.Active.Name);
        Assert.Equal("Atual", p.UniqueScenarioNameFor("Atual", 0));
        p.RemoveScenario(1);
        Assert.Equal(0, p.ActiveScenario);
        p.RemoveScenario(0);
        Assert.Single(p.Scenarios);
    }

    [Fact]
    public void Calibrar_escala_todos_os_cenarios()
    {
        var p = new Project { Background = new Background { ImagePath = "foto.png", MetersPerPixel = 1 } };
        p.Network.AddLink([new(0, 0), new(100, 0)]);
        p.DuplicateScenario(0);
        p.Calibrate(new(0, 0), new(100, 0), 50);
        Assert.All(p.Scenarios, s => Assert.Equal(50, s.Network.Links[0].Axis().Length, 6));
    }

    [Fact]
    public void Importar_em_projeto_vazio_adota_foto_e_substitui_o_cenario_vazio()
    {
        var other = new Project
        {
            Background = new Background { ImagePath = @"C:\fotos\viaduto.png", MetersPerPixel = 0.5, Origin = new(10, 0) },
            Calibration = new Calibration { A = new(0, 0), B = new(100, 0), Meters = 50 },
        };
        other.Network.AddLink([new(0, 0), new(100, 0)]);
        other.Simulation.DurationMinutes = 45;

        var p = new Project();
        var r = p.ImportScenarios(other, "atual");
        Assert.True(r.ReplacedEmpty);
        Assert.Equal(ImportAlignment.AdoptedImage, r.Alignment);
        Assert.Equal(["atual"], p.Scenarios.Select(s => s.Name));
        Assert.Equal(0.5, p.Background!.MetersPerPixel);
        Assert.Equal(45, p.Simulation.DurationMinutes);
    }

    [Fact]
    public void Importar_com_a_mesma_foto_e_outra_calibracao_alinha_pelos_pixels()
    {
        var p = new Project { Background = new Background { ImagePath = @"C:\a\viaduto.png", MetersPerPixel = 0.5 } };
        p.Network.AddLink([new(0, 0), new(50, 0)]);

        // Mesma foto em outra pasta, calibrada com 1 m/px e origem deslocada.
        var other = new Project { Background = new Background { ImagePath = @"D:\b\VIADUTO.png", MetersPerPixel = 1, Origin = new(5, 5) } };
        var link = other.Network.AddLink([new(5, 5), new(105, 5)]); // pixels (0,0) a (100,0)
        other.Network.AddMeasurement(MeasurementKind.Count, link, 40);
        other.DuplicateScenario(0, "Proposta");

        var r = p.ImportScenarios(other, "rotatoria");
        Assert.False(r.ReplacedEmpty);
        Assert.Equal(ImportAlignment.SameImage, r.Alignment);
        Assert.Equal(["Atual", "rotatoria · Atual", "rotatoria · Proposta"], p.Scenarios.Select(s => s.Name));
        Assert.Equal(1, p.ActiveScenario);
        var imported = p.Network.Links.Single();
        Assert.Equal(new Vec2(0, 0), imported.Points[0]);
        Assert.Equal(new Vec2(50, 0), imported.Points[1]);
        Assert.Equal(20, p.Network.Measurements.Single().S, 6);
        // O projeto de origem não muda.
        Assert.Equal(new Vec2(5, 5), other.Scenarios[0].Network.Links[0].Points[0]);
    }

    [Fact]
    public void Importar_com_outra_foto_avisa()
    {
        var p = new Project { Background = new Background { ImagePath = "a.png" } };
        p.Network.AddLink([new(0, 0), new(50, 0)]);
        var other = new Project { Background = new Background { ImagePath = "b.png" } };
        other.Network.AddLink([new(0, 0), new(50, 0)]);
        Assert.Equal(ImportAlignment.DifferentImage, p.ImportScenarios(other, "b").Alignment);
        Assert.Equal(2, p.Scenarios.Count);
    }
}
