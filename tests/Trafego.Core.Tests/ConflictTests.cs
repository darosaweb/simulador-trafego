using System.Xml.Linq;
using Trafego.Core.Model;
using Trafego.Sumo;

namespace Trafego.Core.Tests;

public class ConflictTests
{
    /// <summary>
    /// Cruzamento de duas vias de mão única, uma faixa cada: oeste → leste e sul → norte
    /// (no editor Y é para baixo, então "norte" é Y negativo). As aproximações terminam a 10 m
    /// do centro e os movimentos são conectores, como no Vissim.
    /// </summary>
    internal sealed record Cross(Network Net, Link WIn, Link EOut, Link SIn, Link NOut, Connector WE, Connector SN);

    internal static Cross BuildCross()
    {
        var net = new Network();
        var wIn = net.AddLink([new(-150, 0), new(-10, 0)]);
        var eOut = net.AddLink([new(10, 0), new(150, 0)]);
        var sIn = net.AddLink([new(0, 150), new(0, 10)]);
        var nOut = net.AddLink([new(0, -10), new(0, -150)]);
        var we = net.AddConnector(wIn, 0, wIn.Axis().Length, eOut, 0, 0);
        var sn = net.AddConnector(sIn, 0, sIn.Axis().Length, nOut, 0, 0);
        return new Cross(net, wIn, eOut, sIn, nOut, we, sn);
    }

    [Fact]
    public void Conectores_que_se_cruzam_formam_um_no_com_um_conflito()
    {
        var x = BuildCross();
        var a = ConflictAnalysis.Of(x.Net);

        var j = Assert.Single(a.Junctions);
        Assert.Equal(x.WE.Id, j.Key);
        Assert.Equal([x.WE.Id, x.SN.Id], j.ConnectorIds);
        Assert.Equal(4, j.Nodes.Count);
        var c = Assert.Single(a.Conflicts);
        Assert.Equal(ConflictKind.Crossing, c.Kind);
        Assert.Equal(j.Key, c.JunctionKey);
        Assert.Equal(0, c.Point.X, 6);
        Assert.Equal(0, c.Point.Y, 6);
        Assert.Empty(a.Problems);
    }

    [Fact]
    public void Conversao_que_sai_da_mesma_aproximacao_entra_no_no_e_converge_com_quem_chega_no_mesmo_lugar()
    {
        var x = BuildCross();
        var right = x.Net.AddConnector(x.SIn, 0, x.SIn.Axis().Length, x.EOut, 0, 0); // sul → leste
        var a = ConflictAnalysis.Of(x.Net);

        var j = Assert.Single(a.Junctions);
        Assert.Contains(right.Id, j.ConnectorIds);
        Assert.Contains(a.Conflicts, c => c.Kind == ConflictKind.Merge && c.A == x.WE.Id && c.B == right.Id);
    }

    [Fact]
    public void Conectores_que_saem_da_mesma_faixa_divergem_sem_formar_no()
    {
        // Principal de 2 faixas termina; da faixa da direita saem um conector para a continuação
        // (faixa da direita, mais acima) e outro para uma alça (mais abaixo). Os eixos começam juntos.
        // Geometria tirada de um desenho real no editor, onde aparecia um "nó" falso.
        var net = new Network();
        var main = net.AddLink([new(8.1, 156.8), new(178.6, 141.9)], laneCount: 2);
        var next = net.AddLink([new(227.3, 136.4), new(392.2, 120.1)], laneCount: 2);
        var ramp = net.AddLink([new(232.8, 145.1), new(259.7, 156.8), new(281.5, 178.6)]);
        net.AddConnector(main, 0, main.Axis().Length, next, 0, 0);
        net.AddConnector(main, 0, main.Axis().Length, ramp, 0, 0);
        var a = ConflictAnalysis.Of(net);
        Assert.Empty(a.Junctions);
        Assert.DoesNotContain(a.Conflicts, c => c.Kind == ConflictKind.Crossing);
    }

    [Fact]
    public void Niveis_diferentes_desfazem_o_no()
    {
        var x = BuildCross();
        x.Net.SetPriority(x.SN.Id, x.WE.Id, ConflictPriority.None);
        var a = ConflictAnalysis.Of(x.Net);
        Assert.Empty(a.Junctions);
        Assert.Single(a.Conflicts); // continua listado, para poder voltar atrás
    }

    [Fact]
    public void Conector_que_entra_no_meio_de_um_link_converge_com_o_fluxo_principal()
    {
        var net = new Network();
        var main = net.AddLink([new(0, 0), new(300, 0)], laneCount: 2);
        var ramp = net.AddLink([new(0, 60), new(100, 20)]);
        var merge = net.AddConnector(ramp, 0, ramp.Axis().Length, main, 0, 150);
        var c = Assert.Single(ConflictAnalysis.Of(net).Conflicts);
        Assert.Equal(ConflictKind.Merge, c.Kind);
        Assert.Equal((merge.Id, ConflictRule.MainLine), (c.A, c.B));
        Assert.Equal(main.Id, c.MainLinkId);
    }

    [Fact]
    public void Link_que_cruza_outro_avisa_ate_ser_marcado_como_viaduto()
    {
        var net = new Network();
        var a = net.AddLink([new(-100, 0), new(100, 0)]);
        var b = net.AddLink([new(0, 100), new(0, -100)]);
        var conflict = Assert.Single(ConflictAnalysis.Of(net).Conflicts);
        Assert.Equal(ConflictKind.LinkCrossing, conflict.Kind);
        Assert.Contains(Validation.Check(net), i => i.Severity == Severity.Warning && i.Message.Contains("níveis diferentes"));

        net.SetPriority(a.Id, b.Id, ConflictPriority.None);
        Assert.Empty(Validation.Check(net));
    }

    [Fact]
    public void Preferencia_e_simetrica_e_some_com_o_conector()
    {
        var x = BuildCross();
        x.Net.SetPriority(x.SN.Id, x.WE.Id, ConflictPriority.A); // SN tem preferência
        Assert.Equal(ConflictPriority.A, x.Net.PriorityOf(x.SN.Id, x.WE.Id));
        Assert.Equal(ConflictPriority.B, x.Net.PriorityOf(x.WE.Id, x.SN.Id));
        var rule = Assert.Single(x.Net.ConflictRules);
        Assert.Equal((x.WE.Id, x.SN.Id, ConflictPriority.B), (rule.A, rule.B, rule.Priority));

        x.Net.SetPriority(x.WE.Id, x.SN.Id, ConflictPriority.Auto);
        Assert.Empty(x.Net.ConflictRules);

        x.Net.SetPriority(x.WE.Id, x.SN.Id, ConflictPriority.A);
        x.Net.SetControl(x.WE.Id, JunctionControl.RightBeforeLeft);
        x.Net.RemoveConnector(x.WE.Id);
        Assert.Empty(x.Net.ConflictRules);
        Assert.Empty(x.Net.Junctions);
    }

    [Fact]
    public void Exportacao_poe_os_conectores_do_no_como_conexoes_com_forma_e_a_preferencia_como_prohibition()
    {
        var x = BuildCross();
        x.Net.SetPriority(x.SN.Id, x.WE.Id, ConflictPriority.A);
        x.Net.SetControl(x.WE.Id, JunctionControl.Priority);
        var e = SumoExport.From(x.Net);

        Assert.Empty(e.ConnectorEdges); // os dois são internos ao nó
        var node = e.Nodes.Root!.Elements("node").Single(n => (string?)n.Attribute("id") == $"J{x.WE.Id}");
        Assert.Equal("priority", (string?)node.Attribute("type"));
        Assert.Equal(5, e.Nodes.Root!.Elements("node").Count()); // J + as 4 pontas de fora

        var w = e.LinkPieces[x.WIn.Id][0].EdgeId;
        var east = e.LinkPieces[x.EOut.Id][0].EdgeId;
        var s = e.LinkPieces[x.SIn.Id][0].EdgeId;
        var n = e.LinkPieces[x.NOut.Id][0].EdgeId;
        var we = e.Connections.Root!.Elements("connection").Single(c => (string?)c.Attribute("from") == w);
        Assert.Equal(east, (string?)we.Attribute("to"));
        Assert.StartsWith("-10,0", (string?)we.Attribute("shape"));

        var p = Assert.Single(e.Connections.Root!.Elements("prohibition"));
        Assert.Equal($"{s}->{n}", (string?)p.Attribute("prohibitor"));
        Assert.Equal($"{w}->{east}", (string?)p.Attribute("prohibited"));
    }

    [Fact]
    public void No_com_preferencia_a_direita_vira_right_before_left()
    {
        var x = BuildCross();
        x.Net.SetControl(Math.Min(x.SN.Id, x.WE.Id), JunctionControl.RightBeforeLeft);
        var e = SumoExport.From(x.Net);
        Assert.Contains(e.Nodes.Root!.Elements("node"), n => (string?)n.Attribute("type") == "right_before_left");
    }

    [Fact]
    public void Rotas_atravessam_o_no_sem_edge_de_conector()
    {
        var x = BuildCross();
        x.Net.AddInput(x.WIn);
        var e = SumoExport.From(x.Net);
        var path = Assert.Single(e.PathsFrom(x.WIn.Id));
        Assert.Equal([e.LinkPieces[x.WIn.Id][0].EdgeId, e.LinkPieces[x.EOut.Id][0].EdgeId], path.Edges);
    }

    [Fact]
    public void No_no_SUMO_quem_nao_tem_preferencia_espera_mais()
    {
        if (SumoInstall.Home is null) return;
        var x = BuildCross();
        x.Net.AddInput(x.WIn, 700).TruckShare = 0;
        x.Net.AddInput(x.SIn, 700).TruckShare = 0;
        x.Net.SetPriority(x.SN.Id, x.WE.Id, ConflictPriority.A); // sul → norte tem preferência

        var dir = Directory.CreateTempSubdirectory("trafego-cross").FullName;
        try
        {
            var scenario = SumoScenario.Prepare(x.Net, dir, new SimulationSettings { StartTimeOfDay = 0, DurationMinutes = 15 });
            var net = XDocument.Load(scenario.NetPath).Root!;
            string State(Link from) => (string)net.Elements("connection")
                .Single(c => (string?)c.Attribute("from") == scenario.Export.LinkPieces[from.Id][0].EdgeId).Attribute("state")!;
            Assert.Equal("M", State(x.SIn));
            Assert.Equal("m", State(x.WIn));

            var run = SumoInstall.Run("sumo", scenario.SumoArgs("--tripinfo-output", "trips.xml", "--statistic-output", "stats.xml"), dir);
            Assert.True(run.ExitCode == 0, run.Output);
            var stats = XDocument.Load(Path.Combine(dir, "stats.xml")).Root!;
            Assert.Equal(0, (int)stats.Element("safety")!.Attribute("collisions")!);
            Assert.Equal(0, (int)stats.Element("teleports")!.Attribute("total")!);

            var trips = XDocument.Load(Path.Combine(dir, "trips.xml")).Root!.Elements("tripinfo").ToList();
            double MeanWait(VehicleInput i) => trips.Where(t => ((string)t.Attribute("id")!).StartsWith($"in{i.Id}_"))
                .Average(t => (double)t.Attribute("timeLoss")!);
            var major = MeanWait(x.Net.Inputs[1]);
            var minor = MeanWait(x.Net.Inputs[0]);
            Assert.True(minor > major + 1, $"perda de tempo: secundária {minor:0.0} s, preferencial {major:0.0} s");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
