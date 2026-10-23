using System.Xml.Linq;
using Trafego.Core.Model;
using Trafego.Sumo;

namespace Trafego.Core.Tests;

/// <summary>
/// Rodam o netconvert e o sumo de verdade. Sem SUMO instalado (SUMO_HOME), passam sem fazer nada.
/// </summary>
[Collection(Libsumo.Collection)]
public class SumoIntegrationTests
{
    private static string TempDir() => Directory.CreateTempSubdirectory("trafego-sumo").FullName;

    [Fact]
    public void Netconvert_mantem_exatamente_as_conexoes_exportadas()
    {
        if (SumoInstall.Home is null) return;
        var (net, main, _, exit) = SumoExportTests.Diverge();
        var dir = TempDir();
        try
        {
            var scenario = SumoScenario.Prepare(net, dir, new SimulationSettings { StartTimeOfDay = 0, DurationMinutes = 10 });
            var netXml = XDocument.Load(scenario.NetPath);
            var firstPiece = scenario.Export.LinkPieces[main.Id][0].EdgeId;

            var fromFirst = netXml.Root!.Elements("connection")
                .Where(c => (string?)c.Attribute("from") == firstPiece)
                .Select(c => ((string)c.Attribute("to")!, (int)c.Attribute("fromLane")!, (int)c.Attribute("toLane")!))
                .ToHashSet();
            var next = scenario.Export.LinkPieces[main.Id][1].EdgeId;
            var conn = scenario.Export.ConnectorEdges[exit.Id];
            Assert.Equal(new HashSet<(string, int, int)> { (next, 0, 0), (next, 1, 1), (next, 2, 2), (conn, 0, 0) }, fromFirst);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Simulacao_em_linha_de_comando_leva_os_veiculos_ate_o_fim_sem_colisao()
    {
        if (SumoInstall.Home is null) return;
        var (net, _, _, _) = SumoExportTests.Diverge();
        var dir = TempDir();
        try
        {
            var scenario = SumoScenario.Prepare(net, dir, new SimulationSettings { StartTimeOfDay = 0, DurationMinutes = 10 });
            var result = SumoInstall.Run("sumo", scenario.SumoArgs("--statistic-output", "stats.xml"), dir);
            Assert.True(result.ExitCode == 0, result.Output);

            var stats = XDocument.Load(Path.Combine(dir, "stats.xml")).Root!;
            var vehicles = stats.Element("vehicles")!;
            var inserted = (int)vehicles.Attribute("inserted")!;
            var arrived = inserted - (int)vehicles.Attribute("running")!;
            // 1200 veíc/h por 600 s ≈ 200 veículos.
            Assert.InRange(inserted, 150, 250);
            Assert.True(arrived > inserted * 0.8, $"chegaram {arrived} de {inserted}");
            Assert.Equal(0, (int)stats.Element("teleports")!.Attribute("total")!);
            Assert.Equal(0, (int)stats.Element("safety")!.Attribute("collisions")!);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Libsumo_roda_dentro_do_processo_e_mostra_veiculos_sobre_a_rede()
    {
        if (!LibsumoSession.Available) return;
        var (net, main, ramp, _) = SumoExportTests.Diverge();
        var dir = TempDir();
        try
        {
            var scenario = SumoScenario.Prepare(net, dir, new SimulationSettings { StartTimeOfDay = 0, DurationMinutes = 2 });
            SimSnapshot snap;
            using (var session = new LibsumoSession(scenario))
            {
                snap = session.Step(600); // 60 s
                Assert.Equal(60, session.Time, 6);
            }
            Assert.NotEmpty(snap.Vehicles);
            Assert.True(snap.Departed >= snap.Vehicles.Count);

            // Todo veículo está sobre a principal (y≈0, até 200 m) ou perto da alça (x 100–160, y 0–90).
            foreach (var v in snap.Vehicles)
            {
                var onMain = Math.Abs(v.Y) < main.TotalWidth && v.X > -1 && v.X < 201;
                var onRamp = v.X is > 95 and < 165 && v.Y is > -2 and < 95;
                Assert.True(onMain || onRamp, $"{v.Id} fora da rede em ({v.X:0.0}, {v.Y:0.0})");
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
