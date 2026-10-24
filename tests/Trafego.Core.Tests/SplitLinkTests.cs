using System.Xml.Linq;
using Trafego.Core.Geometry;
using Trafego.Core.Model;
using Trafego.Sumo;

namespace Trafego.Core.Tests;

public class SplitLinkTests
{
    [Fact]
    public void Divide_em_duas_partes_ligadas_por_um_conector_de_todas_as_faixas()
    {
        var net = new Network();
        var link = net.AddLink([new(0, 0), new(200, 0)], laneCount: 2);
        link.SpeedKmh = 80;
        var (a, b, join) = net.SplitLink(link, 100);

        Assert.Same(link, a); // a parte A mantém o id (e o que aponta para ele)
        Assert.Equal([new Vec2(0, 0), new Vec2(99, 0)], a.Points);
        Assert.Equal([new Vec2(101, 0), new Vec2(200, 0)], b.Points);
        Assert.Equal(2, b.Lanes.Count);
        Assert.Equal(80, b.SpeedKmh);
        Assert.Equal((a.Id, b.Id, 2, 99.0, 0.0), (join.FromLinkId, join.ToLinkId, join.LaneCount, join.FromS, join.ToS));
        Assert.Empty(Validation.Check(net));
    }

    [Fact]
    public void O_que_fica_depois_do_ponto_vai_para_a_parte_B()
    {
        var net = new Network();
        var link = net.AddLink([new(0, 0), new(200, 0)]);
        var side = net.AddLink([new(150, 30), new(160, 90)]);
        var feeder = net.AddLink([new(10, 60), new(25, 5)]);
        var exit = net.AddConnector(link, 0, 150, side, 0, 0); // sai depois do ponto
        var merge = net.AddConnector(feeder, 0, feeder.Axis().Length, link, 0, 30); // chega antes
        var input = net.AddInput(link);
        var count = net.AddMeasurement(MeasurementKind.Count, link, 170);
        var early = net.AddMeasurement(MeasurementKind.Count, link, 40);

        var (a, b, _) = net.SplitLink(link, 100);

        Assert.Equal((b.Id, 49.0), (exit.FromLinkId, exit.FromS));
        Assert.Equal((a.Id, 30.0), (merge.ToLinkId, merge.ToS));
        Assert.Equal(a.Id, input.LinkId);
        Assert.Equal((b.Id, 69.0), (count.LinkId, count.S));
        Assert.Equal((a.Id, 40.0), (early.LinkId, early.S));
        Assert.Empty(Validation.Check(net).Where(i => i.Severity == Severity.Error));
    }

    [Fact]
    public void Curva_suave_mantem_os_pontos_de_controle_de_cada_lado()
    {
        var net = new Network();
        var curve = net.AddLink([new(0, 0), new(60, 0), new(100, 40), new(100, 120)]);
        curve.Smooth = true;
        var len = curve.Axis().Length;
        var (a, b, _) = net.SplitLink(curve, len / 2);
        Assert.True(a.Smooth && b.Smooth);
        Assert.Equal(new Vec2(0, 0), a.Points[0]);
        Assert.Equal(new Vec2(100, 120), b.Points[^1]);
        Assert.InRange(a.Axis().Length + b.Axis().Length + Network.SplitGap, len * 0.95, len * 1.05);
    }

    [Fact]
    public void Perto_da_ponta_nao_divide()
    {
        var net = new Network();
        var link = net.AddLink([new(0, 0), new(100, 0)]);
        Assert.Throws<ArgumentException>(() => net.SplitLink(link, 1));
        Assert.Throws<ArgumentException>(() => net.SplitLink(link, 99.5));
    }

    [Fact]
    public void Matriz_e_rotas_escolhidas_continuam_valendo_depois_de_dividir()
    {
        var (net, main, ramp, _) = SumoExportTests.Diverge();
        var input = net.Inputs[0];
        net.Demand.SetOdVolume(input.Id, main.Id, 0, 900);
        net.Demand.SetOdVolume(input.Id, ramp.Id, 0, 300);
        var analysis = ConflictAnalysis.Of(net);
        var straight = RouteFinder.Alternatives(analysis, main.Id, main.Id).Single();
        net.Demand.RouteChoices.Add(new RouteChoice
        {
            OriginId = input.Id, DestinationLinkId = main.Id, ConnectorIds = straight.ConnectorIds, Share = 1,
        });

        var (_, b, join) = net.SplitLink(main, 170); // depois da saída para a alça (aos 100 m)

        Assert.Equal(900, net.Demand.OdVolume(input.Id, b.Id, 0)); // o destino virou a parte B
        Assert.Equal(300, net.Demand.OdVolume(input.Id, ramp.Id, 0));
        var choice = Assert.Single(net.Demand.RouteChoices);
        Assert.Equal(b.Id, choice.DestinationLinkId);
        Assert.Equal([join.Id], choice.ConnectorIds);

        // A escolha ainda encontra o caminho (não cai na "mais rápida" por não achar).
        var e = SumoExport.From(net);
        var routes = e.ChosenRoutes(input, b.Id);
        Assert.Equal([join.Id], Assert.Single(routes).Path.ConnectorIds);
    }

    [Fact]
    public void No_SUMO_a_rede_dividida_simula_como_antes()
    {
        if (SumoInstall.Home is null) return;
        int Completed(bool split)
        {
            var (net, main, _, _) = SumoExportTests.Diverge();
            net.Inputs[0].TruckShare = 0;
            if (split) net.SplitLink(main, 50);
            var dir = Directory.CreateTempSubdirectory("trafego-splitlink").FullName;
            try
            {
                var scenario = SumoScenario.Prepare(net, dir, new SimulationSettings { StartTimeOfDay = 7 * 3600, DurationMinutes = 20, Seed = 3 });
                var run = SumoInstall.Run("sumo", scenario.SumoArgs("--statistic-output", "stats.xml"), dir);
                Assert.True(run.ExitCode == 0, run.Output);
                var stats = XDocument.Load(Path.Combine(dir, "stats.xml")).Root!;
                Assert.Equal(0, (int)stats.Element("teleports")!.Attribute("total")!);
                var v = stats.Element("vehicles")!;
                return (int)v.Attribute("inserted")! - (int)v.Attribute("running")!;
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        var before = Completed(split: false);
        var after = Completed(split: true);
        Assert.InRange(after, before * 0.9, before * 1.1);
    }
}
