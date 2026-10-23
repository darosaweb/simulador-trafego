using System.Xml.Linq;
using Trafego.Core.Model;
using Trafego.Sumo;

namespace Trafego.Core.Tests;

public class SumoExportTests
{
    /// <summary>Principal de 3 faixas (0→200 m, leste) com saída pela faixa da direita aos 100 m para uma alça.</summary>
    internal static (Network net, Link main, Link ramp, Connector exit) Diverge()
    {
        var net = new Network();
        var main = net.AddLink([new(0, 0), new(200, 0)], laneCount: 3);
        main.SpeedKmh = 80;
        var ramp = net.AddLink([new(130, 20), new(160, 90)]);
        var exit = net.AddConnector(main, 0, 100, ramp, 0, 0);
        net.AddInput(main, 1200);
        return (net, main, ramp, exit);
    }

    private static List<XElement> All(XDocument doc, string name) => [.. doc.Root!.Elements(name)];

    [Fact]
    public void Link_e_cortado_onde_o_conector_sai()
    {
        var (net, main, ramp, exit) = Diverge();
        var e = SumoExport.From(net);

        var pieces = e.LinkPieces[main.Id];
        Assert.Equal(2, pieces.Count);
        Assert.Equal(100, pieces[0].S1, 6);
        Assert.Single(e.LinkPieces[ramp.Id]);
        Assert.Equal($"C{exit.Id}", e.ConnectorEdges[exit.Id]);

        var edges = All(e.Edges, "edge");
        Assert.Equal(4, edges.Count); // 2 pedaços da principal, alça, conector
        var first = edges.Single(x => (string?)x.Attribute("id") == pieces[0].EdgeId);
        Assert.Equal("3", (string?)first.Attribute("numLanes"));
        Assert.Equal("22.222", (string?)first.Attribute("speed"));
        // Y invertido: o editor tem Y para baixo e o SUMO para cima.
        var rampEdge = edges.Single(x => (string?)x.Attribute("id") == e.LinkPieces[ramp.Id][0].EdgeId);
        Assert.StartsWith("130,-20", (string?)rampEdge.Attribute("shape"));
    }

    [Fact]
    public void Conexoes_ligam_faixa_a_faixa_e_o_conector_sai_da_faixa_certa()
    {
        var (net, main, ramp, exit) = Diverge();
        var e = SumoExport.From(net);
        var p = e.LinkPieces[main.Id];
        var c = e.ConnectorEdges[exit.Id];
        var cons = All(e.Connections, "connection")
            .Select(x => ((string)x.Attribute("from")!, (int)x.Attribute("fromLane")!, (string)x.Attribute("to")!, (int)x.Attribute("toLane")!))
            .ToHashSet();

        Assert.Equal(
            new HashSet<(string, int, string, int)>
            {
                (p[0].EdgeId, 0, p[1].EdgeId, 0),
                (p[0].EdgeId, 1, p[1].EdgeId, 1),
                (p[0].EdgeId, 2, p[1].EdgeId, 2),
                (p[0].EdgeId, 0, c, 0),
                (c, 0, e.LinkPieces[ramp.Id][0].EdgeId, 0),
            },
            cons);
    }

    [Fact]
    public void Caminhos_dividem_a_chance_igualmente_em_cada_escolha()
    {
        var (net, main, ramp, exit) = Diverge();
        var e = SumoExport.From(net);
        var paths = e.PathsFrom(main.Id);
        Assert.Equal(2, paths.Count);
        Assert.Equal(1, paths.Sum(x => x.Probability), 9);
        Assert.Contains(paths, x => x.Edges.Count == 2 && x.Probability == 0.5); // segue na principal
        Assert.Contains(paths, x => x.Edges.Contains(e.ConnectorEdges[exit.Id]) && x.Edges[^1] == e.LinkPieces[ramp.Id][0].EdgeId);
    }

    [Fact]
    public void Caminhos_numa_rotatoria_terminam_sem_repetir_edge()
    {
        // Anel de um link só, fechado por um conector do fim para o início; entra aos 20 m e sai aos 60 m.
        var net = new Network();
        var ring = net.AddLink([new(0, 0), new(40, 0), new(40, 40), new(0, 40), new(0, 2)]);
        var approach = net.AddLink([new(-50, -30), new(15, -8)]);
        var leave = net.AddLink([new(50, 25), new(100, 30)]);
        net.AddConnector(ring, 0, ring.Axis().Length, ring, 0, 0);
        net.AddConnector(approach, 0, approach.Axis().Length, ring, 0, 20);
        net.AddConnector(ring, 0, 60, leave, 0, 0);

        var e = SumoExport.From(net);
        var paths = e.PathsFrom(approach.Id);
        Assert.NotEmpty(paths);
        Assert.All(paths, p => Assert.Equal(p.Edges.Count, p.Edges.Distinct().Count()));
        Assert.Contains(paths, p => p.Edges[^1] == e.LinkPieces[leave.Id][0].EdgeId);
        Assert.Equal(1, paths.Sum(p => p.Probability), 9);
    }

    [Fact]
    public void Rotas_tem_um_fluxo_por_tipo_com_chegadas_de_poisson()
    {
        var (net, _, _, _) = Diverge();
        var input = net.Inputs[0];
        input.TruckShare = 0.25;
        var routes = SumoExport.From(net).Routes(0, 900);

        var dist = Assert.Single(All(routes, "routeDistribution"));
        Assert.Equal(2, dist.Elements("route").Count());
        var flows = All(routes, "flow");
        Assert.Equal(["car", "truck"], flows.Select(f => (string)f.Attribute("type")!));
        Assert.Equal("exp(0.25)", (string?)flows[0].Attribute("period")); // 900 veíc/h
        Assert.Equal("exp(0.08333333333333333)", (string?)flows[1].Attribute("period")); // 300 veíc/h
        Assert.All(flows, f => Assert.Equal($"rd{input.Id}", (string?)f.Attribute("route")));
    }

    [Fact]
    public void Corte_muito_perto_da_ponta_gruda_na_ponta()
    {
        var net = new Network();
        var a = net.AddLink([new(0, 0), new(100, 0)]);
        var b = net.AddLink([new(110, 0), new(200, 0)]);
        net.AddConnector(a, 0, 99.6, b, 0, 0.3);
        var e = SumoExport.From(net);
        Assert.Single(e.LinkPieces[a.Id]);
        Assert.Single(e.LinkPieces[b.Id]);
        Assert.Empty(e.Warnings);
    }
}
