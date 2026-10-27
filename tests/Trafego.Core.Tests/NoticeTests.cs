using Trafego.Core.Model;
using Trafego.Sumo;

namespace Trafego.Core.Tests;

public class NoticeTests
{
    [Fact]
    public void Nomes_citados_viram_alvos_sem_confundir_prefixos()
    {
        var net = new Network();
        for (var i = 0; i < 12; i++) net.AddLink([new(0, i * 10), new(100, i * 10)]);
        var l1 = net.Links.Single(l => l.Name == "Link 1");
        var l12 = net.Links.Single(l => l.Name == "Link 12");
        var targets = MapNotice.FindByName(net, "Link 12 cruza Link 1 fora de um nó.");
        Assert.Equal([new MapTarget(MapTargetKind.Link, l12.Id), new MapTarget(MapTargetKind.Link, l1.Id)], targets);
    }

    [Fact]
    public void Aviso_da_verificacao_traz_primeiro_o_proprio_objeto()
    {
        var (net, main, _, exit) = SumoExportTests.Diverge();
        var notice = MapNotice.Of(net, new ValidationIssue(Severity.Warning, exit.Id, $"Os percentuais de {main.Name} ({exit.Name}) somam 120%."));
        Assert.Equal([new MapTarget(MapTargetKind.Connector, exit.Id), new MapTarget(MapTargetKind.Link, main.Id)], notice.Targets);
    }

    [Fact]
    public void Ids_do_SUMO_viram_nomes_e_alvos()
    {
        var (net, main, ramp, exit) = SumoExportTests.Diverge();
        var e = SumoExport.From(net);

        var lane = SumoMessages.Explain(e, net,
            $"Warning: Lane 'L{main.Id}_1_2' is not connected from any incoming edge at junction 'n{main.Id}_1'.");
        Assert.Contains($"faixa 3 de {main.Name} (trecho 2 de 2)", lane.Text);
        Assert.Contains("não recebe nenhum conector", lane.Text);
        Assert.Equal(new MapTarget(MapTargetKind.Link, main.Id, 1, 2), lane.Targets[0]);

        var speed = SumoMessages.Explain(e, net,
            $"Warning: Speed of straight connection 'C{exit.Id}_0->L{ramp.Id}_0_0' reduced by 6.75 due to turning radius of 17.90 (length=9.52, angle=47.03).");
        Assert.Contains($"faixa 1 de {exit.Name} → faixa 1 de {ramp.Name}", speed.Text);
        Assert.Contains("cai 24 km/h", speed.Text);
        Assert.Equal(2, speed.Targets.Count);

        var other = SumoMessages.Explain(e, net, $"Warning: Something odd with edge 'C{exit.Id}' and 'L{ramp.Id}_0' and 'xyz'.");
        Assert.Equal($"Something odd with edge '{exit.Name}' and '{ramp.Name}' and 'xyz'.", other.Text);
        Assert.Equal([new MapTarget(MapTargetKind.Connector, exit.Id), new MapTarget(MapTargetKind.Link, ramp.Id, 0)], other.Targets);
    }

    [Fact]
    public void Faixa_sem_conector_no_netconvert_aponta_a_faixa_certa()
    {
        if (SumoInstall.Home is null) return;
        var net = new Network();
        var a = net.AddLink([new(0, 0), new(100, 0)]);
        var b = net.AddLink([new(140, 0), new(300, 0)], laneCount: 2);
        net.AddConnector(a, 0, a.Axis().Length, b, 0, 0); // só a faixa 1 de b recebe tráfego
        // Um conector cruzando: os dois viram um nó de cruzamento, onde o netconvert confere as faixas.
        var d = net.AddLink([new(120, 100), new(120, 20)]);
        var f = net.AddLink([new(120, -20), new(120, -100)]);
        net.AddConnector(d, 0, d.Axis().Length, f, 0, 0);
        net.AddInput(a, 300);
        net.AddInput(d, 300);
        var dir = Directory.CreateTempSubdirectory("trafego-notice").FullName;
        try
        {
            var scenario = SumoScenario.Prepare(net, dir, new SimulationSettings { DurationMinutes = 5 });
            var notices = SumoMessages.ExplainAll(scenario.Export, net);
            var lanes = notices.Where(n => n.Text.Contains("não recebe nenhum conector")).ToList();
            Assert.True(lanes.Count == 1, string.Join(" | ", scenario.Export.Warnings));
            var notice = lanes[0];
            Assert.Contains($"faixa 2 de {b.Name}", notice.Text);
            Assert.Equal(new MapTarget(MapTargetKind.Link, b.Id, 0, 1), notice.Targets[0]);

            // Um conector chegando nessa faixa mais adiante (entrada no meio do link): o trecho antes
            // dele fica vazio, o que é o esperado — o aviso some.
            var g = net.AddLink([new(160, 60), new(200, 10)]);
            net.AddConnector(g, 0, g.Axis().Length, b, 1, 80);
            scenario = SumoScenario.Prepare(net, dir, new SimulationSettings { DurationMinutes = 5 });
            Assert.Contains(scenario.Export.Warnings, w => w.Contains($"L{b.Id}_0_1"));
            Assert.DoesNotContain(SumoMessages.ExplainAll(scenario.Export, net), n => n.Text.Contains("não recebe nenhum conector"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
