using System.Xml.Linq;
using Trafego.Core.Model;
using Trafego.Sumo;

namespace Trafego.Core.Tests;

public class SplitTests
{
    [Fact]
    public void Percentual_definido_fica_e_o_resto_se_divide_igual()
    {
        Assert.Equal([0.5, 0.5], RouteFinder.Shares([null, null]));
        Assert.Equal([0.4, 0.6], RouteFinder.Shares([null, 60]));
        Assert.Equal([0.35, 0.35, 0.3], RouteFinder.Shares([null, null, 30]).Select(x => Math.Round(x, 9)));
        Assert.Equal([0.25, 0.75], RouteFinder.Shares([30, 90]).Select(x => Math.Round(x, 9))); // passou de 100%: normaliza
        Assert.Equal([0.0, 1.0], RouteFinder.Shares([null, 100])); // 100% no conector: ninguém segue
        Assert.Equal([0.5, 0.5], RouteFinder.Shares([0, 0])); // tudo zerado: divisão igual
    }

    [Fact]
    public void Caminhos_seguem_o_percentual_da_saida()
    {
        var (net, main, ramp, exit) = SumoExportTests.Diverge();
        exit.SplitPercent = 60;
        var paths = RouteFinder.PathsFrom(ConflictAnalysis.Of(net), main.Id);
        Assert.Equal(0.6, paths.Single(p => p.EndLinkId == ramp.Id).Probability, 9);
        Assert.Equal(0.4, paths.Single(p => p.EndLinkId == main.Id).Probability, 9);

        var split = RouteFinder.SplitAt(ConflictAnalysis.Of(net).Topology, ConflictAnalysis.Of(net).Topology.FromNode(exit));
        Assert.Equal([(null, 0.4), (exit.Id, 0.6)], split.Select(s => (s.ConnectorId, Math.Round(s.Share, 9))));

        exit.SplitPercent = 100;
        Assert.Single(RouteFinder.PathsFrom(ConflictAnalysis.Of(net), main.Id));
    }

    [Fact]
    public void Validacao_avisa_percentuais_que_passam_de_100()
    {
        var net = new Network();
        var a = net.AddLink([new(0, 0), new(100, 0)]);
        var b = net.AddLink([new(110, -20), new(200, -40)]);
        var c = net.AddLink([new(110, 20), new(200, 40)]);
        var cb = net.AddConnector(a, 0, a.Axis().Length, b, 0, 0);
        var cc = net.AddConnector(a, 0, a.Axis().Length, c, 0, 0);
        cb.SplitPercent = 70;
        Assert.DoesNotContain(Validation.Check(net), i => i.Message.Contains("normalizados")); // o outro fica com 30%
        cc.SplitPercent = 50;
        Assert.Contains(Validation.Check(net), i => i.Severity == Severity.Warning && i.Message.Contains("somam 120%"));
        cc.SplitPercent = 20;
        Assert.Contains(Validation.Check(net), i => i.Message.Contains("somam 90%")); // sem outra opção
        cb.SplitPercent = 150;
        Assert.Contains(Validation.Check(net), i => i.Severity == Severity.Error && i.Message.Contains("entre 0 e 100"));
    }

    [Fact]
    public void No_SUMO_60_por_cento_pegam_a_saida()
    {
        if (SumoInstall.Home is null) return;
        var (net, main, ramp, exit) = SumoExportTests.Diverge();
        net.Inputs[0].TruckShare = 0;
        exit.SplitPercent = 60;
        var dir = Directory.CreateTempSubdirectory("trafego-split").FullName;
        try
        {
            var scenario = SumoScenario.Prepare(net, dir, new SimulationSettings { StartTimeOfDay = 7 * 3600, DurationMinutes = 30 });
            var run = SumoInstall.Run("sumo", scenario.SumoArgs("--tripinfo-output", "trips.xml"), dir);
            Assert.True(run.ExitCode == 0, run.Output);
            var trips = XDocument.Load(Path.Combine(dir, "trips.xml")).Root!.Elements("tripinfo").ToList();
            var rampEdge = scenario.Export.LinkPieces[ramp.Id][0].EdgeId;
            var share = trips.Count(t => ((string)t.Attribute("arrivalLane")!).StartsWith(rampEdge + "_")) / (double)trips.Count;
            Assert.True(trips.Count > 400);
            Assert.InRange(share, 0.54, 0.66);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Percentual_vai_e_volta_no_arquivo_do_projeto()
    {
        var (net, main, _, exit) = SumoExportTests.Diverge();
        var other = net.AddConnector(main, 1, 150, net.AddLink([new(160, -20), new(220, -60)]), 0, 0);
        exit.SplitPercent = 60;
        var back = Serialization.ProjectFile.FromJson(Serialization.ProjectFile.ToJson(new Project { Network = net }));
        Assert.Equal(60, back.Network.FindConnector(exit.Id)!.SplitPercent);
        Assert.Null(back.Network.FindConnector(other.Id)!.SplitPercent); // sem percentual continua sem
        Assert.Null(SumoExportTests.Diverge().exit.SplitPercent); // padrão: divisão igual
    }
}
