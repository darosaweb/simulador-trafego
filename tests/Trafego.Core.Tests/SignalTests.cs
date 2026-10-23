using System.Xml.Linq;
using Trafego.Core.Model;
using Trafego.Sumo;

namespace Trafego.Core.Tests;

[Collection(Libsumo.Collection)]
public class SignalTests
{
    private static double H(double hours) => hours * 3600;

    [Fact]
    public void Fases_saem_das_mudancas_de_cor_de_cada_grupo()
    {
        var sc = SignalController.CreateDefault(1); // G1 verde 0–40, G2 verde 45–85, amarelo 3 s, ciclo 90
        var phases = sc.Plans[0].Phases(sc.Groups);
        Assert.Equal(
            [(40.0, "Gr"), (3.0, "yr"), (2.0, "rr"), (40.0, "rG"), (3.0, "ry"), (2.0, "rr")],
            phases.Select(p => (Math.Round(p.Duration, 6), p.State)));
        Assert.Equal(90, phases.Sum(p => p.Duration), 9);
    }

    [Fact]
    public void Verde_que_passa_do_fim_do_ciclo_continua_no_comeco_e_verde_permitido_vira_g()
    {
        var plan = new SignalPlan { Cycle = 90, Timings = [new GroupTiming { GroupId = 1, GreenStart = 80, GreenEnd = 20, Amber = 3 }] };
        Assert.Equal(SignalState.Green, plan.StateAt(1, 85));
        Assert.Equal(SignalState.Green, plan.StateAt(1, 10));
        Assert.Equal(SignalState.Amber, plan.StateAt(1, 21));
        Assert.Equal(SignalState.Red, plan.StateAt(1, 30));

        var groups = new List<SignalGroup> { new() { Id = 1, Permissive = true } };
        Assert.Contains(plan.Phases(groups), p => p.State == "g");
    }

    [Fact]
    public void Plano_em_vigor_segue_a_tabela_de_horarios_e_atravessa_a_meia_noite()
    {
        var sc = SignalController.CreateDefault(1);
        sc.Plans.Add(new SignalPlan { Id = 2, Name = "Pico", Cycle = 120 });
        sc.Schedule =
        [
            new PlanSwitch { TimeOfDay = H(6), PlanId = 1 },
            new PlanSwitch { TimeOfDay = H(7), PlanId = 2 },
            new PlanSwitch { TimeOfDay = H(19), PlanId = 1 },
        ];
        Assert.Equal(1, sc.PlanAt(H(5))!.Id); // antes das 6h vale o das 19h do dia anterior
        Assert.Equal(2, sc.PlanAt(H(7.5))!.Id);
        Assert.Equal(1, sc.PlanAt(H(23))!.Id);

        var switches = sc.SwitchesBetween(H(6.5), H(24 + 7.5));
        Assert.Equal([(H(7), 2), (H(19), 1), (H(30), 1), (H(31), 2)], switches.Select(s => (s.Time, s.Plan.Id)));
    }

    internal static (ConflictTests.Cross X, SignalController Sc, Connector Right) SignalizedCross()
    {
        var x = ConflictTests.BuildCross();
        var right = x.Net.AddConnector(x.SIn, 0, x.SIn.Axis().Length, x.EOut, 0, 0); // sul → leste, sem semáforo
        var sc = x.Net.AddController();
        x.WE.Signal = new SignalRef { ControllerId = sc.Id, GroupId = 1 };
        x.SN.Signal = new SignalRef { ControllerId = sc.Id, GroupId = 2 };
        return (x, sc, right);
    }

    [Fact]
    public void Exportacao_poe_o_no_como_semaforo_com_um_indice_por_grupo()
    {
        var (x, sc, right) = SignalizedCross();
        var e = SumoExport.From(x.Net);

        var tl = $"TL{sc.Id}";
        var j = e.Nodes.Root!.Elements("node").Single(n => ((string)n.Attribute("id")!).StartsWith('J'));
        Assert.Equal("traffic_light", (string?)j.Attribute("type"));
        Assert.Equal(tl, (string?)j.Attribute("tl"));

        var logic = Assert.Single(e.TrafficLights.Root!.Elements("tlLogic"));
        Assert.Equal("P1", (string?)logic.Attribute("programID"));
        Assert.Equal(6, logic.Elements("phase").Count());

        var tlConns = e.TrafficLights.Root!.Elements("connection")
            .ToDictionary(c => (string)c.Attribute("from")!, c => (int)c.Attribute("linkIndex")!);
        Assert.Equal(0, tlConns[e.LinkPieces[x.WIn.Id][0].EdgeId]);
        Assert.Equal(1, tlConns[e.LinkPieces[x.SIn.Id][0].EdgeId]);

        // A conversão à direita, sem cabeça semafórica, passa sem cor (pelas regras de preferência).
        var rightConn = e.Connections.Root!.Elements("connection").Single(c =>
            (string?)c.Attribute("from") == e.LinkPieces[x.SIn.Id][0].EdgeId &&
            (string?)c.Attribute("to") == e.LinkPieces[x.EOut.Id][0].EdgeId);
        Assert.Equal("true", (string?)rightConn.Attribute("uncontrolled"));
        Assert.Empty(e.Additional(H(7), H(8)).Root!.Elements()); // um plano só: sem troca por horário
    }

    [Fact]
    public void Validacao_avisa_quando_grupos_que_se_cruzam_abrem_juntos()
    {
        var (x, sc, _) = SignalizedCross();
        Assert.DoesNotContain(Validation.Check(x.Net), i => i.Message.Contains("abrem juntos"));
        sc.Plans[0].TimingOf(2)!.GreenStart = 30; // G2 passa a abrir enquanto G1 ainda está verde
        Assert.Contains(Validation.Check(x.Net), i => i.Message.Contains("abrem juntos"));
        sc.Groups[1].Permissive = true; // verde permitido cede a passagem: aceitável
        Assert.DoesNotContain(Validation.Check(x.Net), i => i.Message.Contains("abrem juntos"));
    }

    [Fact]
    public void Remover_grupo_tira_os_tempos_e_as_cabecas_dele()
    {
        var (x, sc, _) = SignalizedCross();
        x.Net.RemoveGroup(sc, 2);
        Assert.Null(x.SN.Signal);
        Assert.All(sc.Plans, p => Assert.Null(p.TimingOf(2)));
        x.Net.RemoveController(sc.Id);
        Assert.Null(x.WE.Signal);
    }

    [Fact]
    public void No_SUMO_o_semaforo_alterna_os_fluxos_sem_colisao()
    {
        if (SumoInstall.Home is null) return;
        var (x, _, _) = SignalizedCross();
        x.Net.AddInput(x.WIn, 500).TruckShare = 0;
        x.Net.AddInput(x.SIn, 500).TruckShare = 0;
        var dir = Directory.CreateTempSubdirectory("trafego-tl").FullName;
        try
        {
            var scenario = SumoScenario.Prepare(x.Net, dir, new SimulationSettings { StartTimeOfDay = H(7), DurationMinutes = 15 });
            var run = SumoInstall.Run("sumo", scenario.SumoArgs("--statistic-output", "stats.xml"), dir);
            Assert.True(run.ExitCode == 0, run.Output);
            var stats = XDocument.Load(Path.Combine(dir, "stats.xml")).Root!;
            Assert.Equal(0, (int)stats.Element("safety")!.Attribute("collisions")!);
            Assert.Equal(0, (int)stats.Element("teleports")!.Attribute("total")!);
            var v = stats.Element("vehicles")!;
            var inserted = (int)v.Attribute("inserted")!;
            Assert.True(inserted - (int)v.Attribute("running")! > inserted * 0.8);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void No_libsumo_o_plano_troca_no_horario_da_tabela()
    {
        if (!LibsumoSession.Available) return;
        var (x, sc, _) = SignalizedCross();
        // Plano 2: ciclo de 60 s com só 10 s de verde para o grupo 1.
        sc.Plans.Add(new SignalPlan
        {
            Id = 2, Name = "Pico", Cycle = 60,
            Timings =
            [
                new GroupTiming { GroupId = 1, GreenStart = 0, GreenEnd = 10, Amber = 3 },
                new GroupTiming { GroupId = 2, GreenStart = 15, GreenEnd = 55, Amber = 3 },
            ],
        });
        sc.Schedule = [new PlanSwitch { TimeOfDay = H(6), PlanId = 1 }, new PlanSwitch { TimeOfDay = H(7), PlanId = 2 }];

        var dir = Directory.CreateTempSubdirectory("trafego-waut").FullName;
        try
        {
            var scenario = SumoScenario.Prepare(x.Net, dir, new SimulationSettings { StartTimeOfDay = H(7) - 180, DurationMinutes = 10 });
            var tl = scenario.Export.TrafficLightIds[sc.Id];
            double GreenShare(LibsumoSession s, int seconds)
            {
                var green = 0;
                for (var i = 0; i < seconds * 10; i++)
                    if (s.Step(1).Signals![tl][0] == 'G') green++;
                return green / (seconds * 10.0);
            }

            using var session = new LibsumoSession(scenario);
            Assert.Equal(H(7) - 180, session.Time, 6);
            var before = GreenShare(session, 180); // 06:57–07:00: plano 1, 40 s de 90
            var after = GreenShare(session, 300); // 07:00–07:05: plano 2, 10 s de 60
            Assert.InRange(before, 0.36, 0.52);
            Assert.InRange(after, 0.10, 0.24);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
