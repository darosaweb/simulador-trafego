using System.Xml.Linq;
using Trafego.Core.Model;
using Trafego.Sumo;

namespace Trafego.Core.Tests;

public class DemandTests
{
    private static double H(double hours) => hours * 3600;
    private static double M(double h, double m) => h * 3600 + m * 60;

    /// <summary>
    /// A (0→100 m) se divide em B (por cima) e C (por baixo, mais longo), que voltam a se juntar em D.
    /// Dois caminhos de A até a saída D; B e C também saem da rede (sem conectores no fim) se não forem a D.
    /// </summary>
    private static (Network Net, Link A, Link B, Link C, Link D, VehicleInput In) TwoRoutes()
    {
        var net = new Network();
        var a = net.AddLink([new(0, 0), new(100, 0)]);
        var b = net.AddLink([new(110, -10), new(200, -10)]);
        var c = net.AddLink([new(110, 10), new(150, 60), new(200, 10)]);
        var d = net.AddLink([new(210, 0), new(300, 0)]);
        net.AddConnector(a, 0, a.Axis().Length, b, 0, 0);
        net.AddConnector(a, 0, a.Axis().Length, c, 0, 0);
        net.AddConnector(b, 0, b.Axis().Length, d, 0, 0);
        net.AddConnector(c, 0, c.Axis().Length, d, 0, 0);
        var input = net.AddInput(a, 0);
        input.TruckShare = 0;
        return (net, a, b, c, d, input);
    }

    [Fact]
    public void Janelas_seguem_os_intervalos_do_dia_e_antes_do_primeiro_vale_o_ultimo()
    {
        var d = new DemandSettings { SliceStarts = [M(7, 30), M(7, 0), M(7, 15)] };
        var w = d.Windows(M(6, 50), M(7, 40));
        Assert.Equal(
            [(M(6, 50), M(7, 0), M(7, 30)), (M(7, 0), M(7, 15), M(7, 0)), (M(7, 15), M(7, 30), M(7, 15)), (M(7, 30), M(7, 40), M(7, 30))],
            w);
        Assert.Single(new DemandSettings().Windows(H(7), H(8))); // sem intervalos: um só, o dia todo
    }

    [Fact]
    public void Saidas_e_alternativas_de_caminho_em_ordem_de_tempo()
    {
        var (net, a, b, c, d, _) = TwoRoutes();
        var analysis = ConflictAnalysis.Of(net);
        Assert.Equal([d.Id], RouteFinder.Exits(analysis.Topology).Select(l => l.Id));

        var alts = RouteFinder.Alternatives(analysis, a.Id, d.Id);
        Assert.Equal(2, alts.Count);
        Assert.True(alts[0].FreeFlowTime < alts[1].FreeFlowTime);
        Assert.Contains(alts[0].Steps, s => s is LinkStep ls && ls.LinkId == b.Id); // por cima é mais curto
        Assert.Contains(alts[1].Steps, s => s is LinkStep ls && ls.LinkId == c.Id);
        Assert.Equal(2, alts[0].ConnectorIds.Count);
    }

    [Fact]
    public void Entrada_sem_matriz_tem_um_fluxo_por_intervalo_com_o_volume_dele()
    {
        var (net, _, _, _, _, input) = TwoRoutes();
        net.Demand.SliceStarts = [H(7), M(7, 15)];
        input.VolumeVph = 600;
        input.SetVolume(M(7, 15), 1200);

        var routes = SumoExport.From(net).Routes(H(7), M(7, 30)).Root!;
        var flows = routes.Elements("flow").ToList();
        Assert.Equal(["0.1666", "0.3333"], flows.Select(f => ((string)f.Attribute("period")!)[4..10])); // 600 e 1200 veíc/h
        Assert.Equal([H(7), M(7, 15)], flows.Select(f => (double)f.Attribute("begin")!));
        Assert.Equal($"rd{input.Id}", (string?)flows[0].Attribute("route"));
        Assert.Equal(2, routes.Element("routeDistribution")!.Elements("route").Count()); // B ou C, meio a meio
    }

    [Fact]
    public void Matriz_pela_rota_mais_rapida_ate_o_usuario_escolher_as_participacoes()
    {
        var (net, a, b, c, d, input) = TwoRoutes();
        net.Demand.SetOdVolume(input.Id, d.Id, 0, 900);
        Assert.True(net.Demand.UsesMatrix(input.Id));

        var e = SumoExport.From(net);
        var dist = e.Routes(H(7), H(8)).Root!.Elements("routeDistribution").Single();
        Assert.Equal($"od{input.Id}_{d.Id}", (string?)dist.Attribute("id"));
        var only = Assert.Single(dist.Elements("route"));
        Assert.Contains(e.LinkPieces[b.Id][0].EdgeId, (string)only.Attribute("edges")!);

        var alts = RouteFinder.Alternatives(e.Analysis, a.Id, d.Id);
        net.Demand.RouteChoices =
        [
            new RouteChoice { OriginId = input.Id, DestinationLinkId = d.Id, ConnectorIds = alts[0].ConnectorIds, Share = 70 },
            new RouteChoice { OriginId = input.Id, DestinationLinkId = d.Id, ConnectorIds = alts[1].ConnectorIds, Share = 30 },
        ];
        var routes = SumoExport.From(net).Routes(H(7), H(8)).Root!.Elements("routeDistribution").Single().Elements("route").ToList();
        Assert.Equal(["70", "30"], routes.Select(r => (string)r.Attribute("probability")!));
    }

    [Fact]
    public void Apagar_conector_de_uma_rota_escolhida_tira_a_escolha()
    {
        var (net, a, _, _, d, input) = TwoRoutes();
        var alts = RouteFinder.Alternatives(ConflictAnalysis.Of(net), a.Id, d.Id);
        net.Demand.RouteChoices.Add(new RouteChoice { OriginId = input.Id, DestinationLinkId = d.Id, ConnectorIds = alts[1].ConnectorIds, Share = 1 });
        net.RemoveConnector(alts[1].ConnectorIds[0]);
        Assert.Empty(net.Demand.RouteChoices);
    }

    [Fact]
    public void Mudar_ou_apagar_intervalo_leva_os_volumes_junto()
    {
        var (net, _, _, _, d, input) = TwoRoutes();
        net.Demand.SliceStarts = [H(7), H(8)];
        input.SetVolume(H(8), 500);
        net.Demand.SetOdVolume(input.Id, d.Id, H(8), 200);
        net.Demand.MoveSlice(H(8), M(8, 30), net);
        Assert.Equal(500, input.VolumeAt(M(8, 30)));
        Assert.Equal(200, net.Demand.OdVolume(input.Id, d.Id, M(8, 30)));
        net.Demand.RemoveSlice(M(8, 30), net);
        Assert.Equal([H(7)], net.Demand.SliceStarts);
        Assert.Empty(net.Demand.Matrix);
    }

    [Fact]
    public void No_SUMO_a_matriz_divide_os_veiculos_entre_os_destinos()
    {
        if (SumoInstall.Home is null) return;
        var (net, main, ramp, _) = SumoExportTests.Diverge();
        var input = net.Inputs[0];
        input.TruckShare = 0;
        net.Demand.SetOdVolume(input.Id, main.Id, 0, 900);
        net.Demand.SetOdVolume(input.Id, ramp.Id, 0, 300);

        var dir = Directory.CreateTempSubdirectory("trafego-od").FullName;
        try
        {
            var scenario = SumoScenario.Prepare(net, dir, new SimulationSettings { StartTimeOfDay = H(7), DurationMinutes = 30 });
            var run = SumoInstall.Run("sumo", scenario.SumoArgs("--tripinfo-output", "trips.xml"), dir);
            Assert.True(run.ExitCode == 0, run.Output);
            var trips = XDocument.Load(Path.Combine(dir, "trips.xml")).Root!.Elements("tripinfo").ToList();
            var rampEdge = scenario.Export.LinkPieces[ramp.Id][0].EdgeId;
            var toRamp = trips.Count(t => ((string)t.Attribute("arrivalLane")!).StartsWith(rampEdge + "_"));
            var toMain = trips.Count - toRamp;
            Assert.InRange(trips.Count, 480, 720); // 1200 veíc/h por 30 min ≈ 600
            Assert.InRange(toMain / (double)toRamp, 2.2, 4.0); // 900 : 300
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void No_SUMO_o_volume_muda_com_o_intervalo()
    {
        if (SumoInstall.Home is null) return;
        var (net, _, _, _) = SumoExportTests.Diverge();
        var input = net.Inputs[0];
        input.TruckShare = 0;
        net.Demand.SliceStarts = [H(7), M(7, 15)];
        input.SetVolume(H(7), 300);
        input.SetVolume(M(7, 15), 1200);

        var dir = Directory.CreateTempSubdirectory("trafego-slices").FullName;
        try
        {
            var scenario = SumoScenario.Prepare(net, dir, new SimulationSettings { StartTimeOfDay = H(7), DurationMinutes = 30 });
            var run = SumoInstall.Run("sumo", scenario.SumoArgs("--tripinfo-output", "trips.xml"), dir);
            Assert.True(run.ExitCode == 0, run.Output);
            var departs = XDocument.Load(Path.Combine(dir, "trips.xml")).Root!.Elements("tripinfo")
                .Select(t => (double)t.Attribute("depart")!).ToList();
            var first = departs.Count(t => t < M(7, 15));
            var second = departs.Count(t => t >= M(7, 15) && t < M(7, 25)); // deixa os últimos minutos para chegarem
            Assert.InRange(first, 50, 110); // 300 veíc/h por 15 min ≈ 75
            Assert.InRange(second / 10.0 * 15 / first, 2.5, 6); // ≈ 4 vezes mais
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
