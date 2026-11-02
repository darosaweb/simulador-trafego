using System.Globalization;
using Trafego.Core.Model;
using Trafego.Core.Results;
using Trafego.Sumo;
using Xunit.Abstractions;

namespace Trafego.Core.Tests;

/// <summary>Balsa: modelo e validação, e a simulação de verdade no SUMO (quando instalado).</summary>
[Collection(Libsumo.Collection)]
public class FerryTests(ITestOutputHelper output)
{
    /// <summary>
    /// Margem A (estrada até a rampa) → travessia de 1 km → margem B; com <paramref name="twoWay"/>, também a
    /// estrada da B até a rampa de volta → travessia de volta → margem A.
    /// </summary>
    internal static (Network Net, Ferry Ferry) River(bool twoWay, double vph = 80, double vphBack = 60)
    {
        var net = new Network();
        var roadA = net.AddLink([new(0, 0), new(200, 0)]);
        roadA.Name = "Estrada A";
        var crossing = net.AddLink([new(210, 0), new(1210, 0)]);
        crossing.Name = "Travessia";
        var roadB = net.AddLink([new(1220, 0), new(1420, 0)]);
        roadB.Name = "Estrada B";
        net.AddConnector(roadA, 0, roadA.Axis().Length, crossing, 0, 0);
        net.AddConnector(crossing, 0, crossing.Axis().Length, roadB, 0, 0);
        net.AddInput(roadA, vph).TruckShare = 0.1;
        var ferry = net.AddFerry(crossing);
        ferry.DeckLength = 40;
        ferry.CrossingMinutes = 2;
        ferry.MaxWaitMinutes = 3;
        if (twoWay)
        {
            var backB = net.AddLink([new(1420, 30), new(1220, 30)]);
            backB.Name = "Volta B";
            var back = net.AddLink([new(1210, 30), new(210, 30)]);
            back.Name = "Travessia volta";
            var backA = net.AddLink([new(200, 30), new(0, 30)]);
            backA.Name = "Volta A";
            net.AddConnector(backB, 0, backB.Axis().Length, back, 0, 0);
            net.AddConnector(back, 0, back.Axis().Length, backA, 0, 0);
            net.AddInput(backB, vphBack).TruckShare = 0.1;
            ferry.ReturnLinkId = back.Id;
        }
        return (net, ferry);
    }

    [Fact]
    public void Validacao_aceita_o_rio_e_aponta_travessias_erradas()
    {
        var (net, ferry) = River(twoWay: true);
        Assert.DoesNotContain(Validation.Check(net), i => i.Severity == Severity.Error);

        var crossing = net.FindLink(ferry.LinkId)!;
        crossing.Lanes.Add(new Lane());
        net.AddInput(crossing, 100);
        ferry.DeckLength = 2000;
        var errors = Validation.Check(net).Where(i => i.Severity == Severity.Error).Select(i => i.Message).ToList();
        Assert.Contains(errors, m => m.Contains("1 faixa"));
        Assert.Contains(errors, m => m.Contains("entrada de veículos na travessia"));
        Assert.Contains(errors, m => m.Contains("mais longa que o convés"));
    }

    /// <summary>
    /// Estrada de 1 faixa → pátio de 4 faixas → rampa de 1 faixa → travessia → margem B. Os conectores desenhados
    /// ligam só a faixa 0 do pátio; as outras são completadas na exportação.
    /// </summary>
    internal static (Network Net, Ferry Ferry, Link Yard) YardRiver(double vph = 300, int roadLanes = 1)
    {
        var net = new Network();
        var road = net.AddLink([new(roadLanes > 1 ? -1500 : -400, 0), new(-80, 0)]);
        road.Name = "Estrada A";
        for (var i = 1; i < roadLanes; i++) road.Lanes.Add(new Lane());
        var yard = net.AddLink([new(-70, 0), new(-10, 0)]);
        yard.Name = "Pátio A";
        for (var i = 0; i < 3; i++) yard.Lanes.Add(new Lane());
        var ramp = net.AddLink([new(0, 0), new(20, 0)]);
        ramp.Name = "Rampa A";
        var crossing = net.AddLink([new(30, 0), new(530, 0)]);
        crossing.Name = "Travessia";
        var roadB = net.AddLink([new(540, 0), new(740, 0)]);
        roadB.Name = "Estrada B";
        net.AddConnector(road, 0, road.Axis().Length, yard, 0, 0, roadLanes);
        net.AddConnector(yard, 0, yard.Axis().Length, ramp, 0, 0);
        net.AddConnector(ramp, 0, ramp.Axis().Length, crossing, 0, 0);
        net.AddConnector(crossing, 0, crossing.Axis().Length, roadB, 0, 0);
        net.AddInput(road, vph).TruckShare = 0.1;
        var ferry = net.AddFerry(crossing);
        ferry.DeckLength = 25;
        ferry.DeckLanes = 4;
        ferry.CrossingMinutes = 2;
        ferry.MaxWaitMinutes = 3;
        ferry.YardLinkId = yard.Id;
        return (net, ferry, yard);
    }

    [Fact]
    public void Validacao_do_patio()
    {
        var (net, ferry, yard) = YardRiver();
        Assert.DoesNotContain(Validation.Check(net), i => i.Severity == Severity.Error);
        // Travessia curta para 4 faixas de 25 m em fila (103,75 m + 20).
        Assert.Equal(103.75, ferry.LinearDeck, 6);
        // Pátio que não leva à travessia.
        var other = net.AddLink([new(0, 100), new(50, 100)]);
        ferry.YardLinkId = other.Id;
        Assert.Contains(Validation.Check(net), i => i.Severity == Severity.Error && i.Message.Contains("não se chega à travessia"));
        ferry.YardLinkId = yard.Id;
        net.RemoveLink(yard.Id);
        Assert.Null(ferry.YardLinkId);
    }

    [Fact]
    public void Patio_enche_faixa_por_faixa_e_embarca_no_conves_de_quatro_faixas() => YardScenario(roadLanes: 1);

    /// <summary>
    /// Estrada de 2 faixas e longa: quem saiu depois ultrapassa e chega antes ao pátio. A faixa do pátio embarca na
    /// ordem em que os veículos estão nela, não na ordem em que saíram.
    /// </summary>
    [Fact]
    public void Patio_com_ultrapassagem_antes_dele() => YardScenario(roadLanes: 2);

    private void YardScenario(int roadLanes)
    {
        if (!LibsumoSession.Available) return;
        var (net, ferry, _) = YardRiver(roadLanes: roadLanes);
        var dir = Directory.CreateTempSubdirectory("trafego-balsa").FullName;
        try
        {
            var scenario = Prepare(net, dir, minutes: 40);
            using var session = new LibsumoSession(scenario, "anim_");
            SimSnapshot last;
            var maxRiders = 0;
            var lateral = new HashSet<double>();
            do
            {
                last = session.Step(50);
                foreach (var v in last.Ferries!)
                {
                    Assert.Equal(4, v.Lanes);
                    maxRiders = Math.Max(maxRiders, v.Riders.Count);
                    if (v.Moving) foreach (var r in v.Riders) lateral.Add(Math.Round(r.Lateral));
                }
            }
            while (!last.Finished);
            var ferries = session.Ferries!;
            Print(ferries);
            AssertSound(ferries, ferry, last);

            var log = ferries.Logs()[0];
            var loaded = log.Trips.Where(t => t.Direction == 0).ToList();
            Assert.True(loaded.Count >= 5);
            // Um convés de 4 × 25 m leva bem mais que uma fila de 25 m (4 carros).
            Assert.True(loaded.Max(t => t.Vehicles) >= 12, $"no máximo {loaded.Max(t => t.Vehicles)} a bordo");
            // Com fila no pátio, toda viagem depois da primeira sai lotada (nenhuma faixa trava o embarque).
            Assert.All(loaded.Skip(1), t => Assert.True(t.Full, $"saiu às {T(t.DepartedAt)} com {t.Vehicles} sem lotar"));
            // O pátio (60 m por faixa, uns 8 carros) passou de uma faixa.
            var yardPeak = log.Queue.Where(q => q.Length > 3).Max(q => q[3]);
            output.WriteLine($"máximo no pátio: {yardPeak}");
            Assert.True(yardPeak > 12, $"máximo no pátio {yardPeak}");
            // Na travessia, quem está a bordo aparece nas 4 faixas do casco.
            Assert.True(maxRiders >= 12);
            Assert.Equal(4, lateral.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Avaliar_com_patio_sem_colisoes()
    {
        if (!LibsumoSession.Available) return;
        var (net, _, _) = YardRiver();
        var dir = Directory.CreateTempSubdirectory("trafego-balsa").FullName;
        try
        {
            var set = await SumoBatch.RunAsync(Prepare(net, dir, minutes: 30), "teste");
            var result = set.MeanMeasurements().Single(m => m.Kind == MeasurementKind.Ferry);
            output.WriteLine($"no pátio: {result.Total["maxYard"]:0.0}, fila: {result.Total["maxQueue"]:0.0}, ocupação {result.Total["occupancy"]:0}%");
            Assert.True(result.Total["maxYard"] > 12);
            Assert.True(result.Total["maxQueue"] >= result.Total["maxYard"]);
            Assert.All(set.Runs, r => Assert.Equal(0, r.Kpis.Collisions));
            Assert.All(set.Runs, r => Assert.Equal(0, r.Kpis.Teleports));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Apagar_o_link_apaga_a_balsa_ou_a_volta()
    {
        var (net, ferry) = River(twoWay: true);
        net.RemoveLink(ferry.ReturnLinkId!.Value);
        Assert.Null(ferry.ReturnLinkId);
        net.RemoveLink(ferry.LinkId);
        Assert.Empty(net.Ferries);
    }

    private SumoScenario Prepare(Network net, string dir, double minutes = 60) =>
        SumoScenario.Prepare(net, dir, new SimulationSettings { StartTimeOfDay = 0, DurationMinutes = minutes, Replications = 2 });

    private void Print(FerrySystem ferries)
    {
        output.WriteLine("emb | sentido | saiu  | chegou | carros | cam. | convés | lotou | ficaram");
        foreach (var t in ferries.Logs().SelectMany(l => l.Trips).OrderBy(t => t.DepartedAt))
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{t.Vessel,3} | {(t.Direction switch { 0 => "ida", 1 => "volta", _ => "vazia" }),7} | {T(t.DepartedAt)} | {T(t.ArrivedAt)} | {t.Vehicles - t.Trucks,6} | {t.Trucks,4} | {t.UsedLength,4:0.0} m | {(t.Full ? "sim" : "não"),5} | {t.LeftBehind,7}"));
        foreach (var e in ferries.Errors.Distinct().Take(5)) output.WriteLine("erro: " + e);
    }

    private static string T(double s) => double.IsNaN(s) ? "  —  " : $"{(int)(s / 60):00}:{(int)(s % 60):00}";

    /// <summary>Confere as regras que valem para qualquer balsa.</summary>
    private static void AssertSound(FerrySystem ferries, Ferry ferry, SimSnapshot last)
    {
        Assert.Empty(ferries.Errors);
        Assert.Equal(0, ferries.Stowaways);
        var trips = ferries.Logs().SelectMany(l => l.Trips).ToList();
        foreach (var t in trips)
        {
            Assert.True(t.UsedLength <= ferry.DeckLength * ferry.DeckLanes + 1e-6, $"usou {t.UsedLength:0.0} m do convés");
            // A travessia leva o tempo definido (ou mais, se esperou a margem desocupar).
            Assert.True(t.ArrivedAt - t.DepartedAt >= ferry.CrossingMinutes * 60 - 1, $"travessia de {t.ArrivedAt - t.DepartedAt:0} s");
            // Todos a bordo atravessam juntos, na formação do convés.
            Assert.True(t.MaxSpread <= ferry.LinearDeck, $"espalhou {t.MaxSpread:0.0} m");
        }
        Assert.True(last.Finished);
    }

    [Fact]
    public void So_ida_leva_em_lotes_e_volta_vazia()
    {
        if (!LibsumoSession.Available) return;
        var (net, ferry) = River(twoWay: false);
        var dir = Directory.CreateTempSubdirectory("trafego-balsa").FullName;
        try
        {
            var scenario = Prepare(net, dir);
            using var session = new LibsumoSession(scenario, "anim_");
            SimSnapshot last;
            var sawMoving = false;
            do
            {
                last = session.Step(100);
                sawMoving |= last.Ferries!.Any(f => f.Moving && f.Aboard > 0);
            }
            while (!last.Finished);
            var ferries = session.Ferries!;
            Print(ferries);
            AssertSound(ferries, ferry, last);

            var trips = ferries.Logs()[0].Trips;
            Assert.True(trips.Count(t => t.Direction == 0) >= 5);
            Assert.Contains(trips, t => t.Direction == -1);         // volta vazia
            Assert.DoesNotContain(trips, t => t.Direction == 1);
            Assert.Contains(trips, t => t.Full && t.LeftBehind > 0); // lota e deixa gente para a próxima
            Assert.True(sawMoving, "a animação deve mostrar a balsa navegando com carga");
            Assert.All(trips.Where(t => t.Direction == 0), t => Assert.InRange(t.ArrivedAt - t.DepartedAt, 119, 121));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Ida_e_volta_com_duas_embarcacoes()
    {
        if (!LibsumoSession.Available) return;
        var (net, ferry) = River(twoWay: true);
        ferry.Vessels = 2;
        var dir = Directory.CreateTempSubdirectory("trafego-balsa").FullName;
        try
        {
            var scenario = Prepare(net, dir);
            using var session = new LibsumoSession(scenario, "anim_");
            SimSnapshot last;
            do last = session.Step(600);
            while (!last.Finished);
            var ferries = session.Ferries!;
            Print(ferries);
            AssertSound(ferries, ferry, last);

            var trips = ferries.Logs()[0].Trips;
            Assert.True(trips.Count(t => t.Direction == 0 && t.Vehicles > 0) >= 4, "ida com carga");
            Assert.True(trips.Count(t => t.Direction == 1 && t.Vehicles > 0) >= 4, "volta com carga");
            Assert.Equal([0, 1], trips.Select(t => t.Vessel).Distinct().Order());
            // As duas embarcações não ficam na mesma margem ao mesmo tempo: as viagens de cada sentido se alternam
            // entre elas e nunca saem juntas.
            foreach (var d in new[] { 0, 1 })
            {
                var deps = trips.Where(t => t.Direction == d).OrderBy(t => t.DepartedAt).ToList();
                for (var i = 1; i < deps.Count; i++) Assert.True(deps[i].DepartedAt - deps[i - 1].DepartedAt > 5);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Seis embarcações começam espalhadas pelo ciclo: chegam às margens uma de cada vez, não em bloco.</summary>
    [Fact]
    public void Seis_embarcacoes_espalhadas_pelo_ciclo()
    {
        if (!LibsumoSession.Available) return;
        var (net, ferry) = River(twoWay: true, vph: 240, vphBack: 200);
        ferry.Vessels = 6;
        ferry.CrossingMinutes = 3;
        var dir = Directory.CreateTempSubdirectory("trafego-balsa").FullName;
        try
        {
            using var session = new LibsumoSession(Prepare(net, dir), "anim_");
            SimSnapshot last;
            do last = session.Step(600);
            while (!last.Finished);
            var ferries = session.Ferries!;
            Print(ferries);
            AssertSound(ferries, ferry, last);
            var trips = ferries.Logs()[0].Trips;
            Assert.Equal(6, trips.Select(t => t.Vessel).Distinct().Count());
            // Chegadas a cada margem: nunca duas quase juntas.
            foreach (var d in new[] { 0, 1 })
            {
                var arrivals = trips.Where(t => t.Direction == d).Select(t => t.ArrivedAt).Order().ToList();
                for (var i = 1; i < arrivals.Count; i++)
                    Assert.True(arrivals[i] - arrivals[i - 1] > 20, $"chegadas em {T(arrivals[i - 1])} e {T(arrivals[i])}");
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Exemplo_da_balsa_roda_nos_dois_cenarios()
    {
        if (!LibsumoSession.Available) return;
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "samples", "balsa.trafego.json"))) d = d.Parent;
        Assert.NotNull(d);
        var project = Serialization.ProjectFile.Load(Path.Combine(d.FullName, "samples", "balsa.trafego.json"));
        Assert.Equal(3, project.Scenarios.Count);
        foreach (var s in project.Scenarios)
        {
            Assert.DoesNotContain(Validation.Check(s.Network), i => i.Severity == Severity.Error);
            var dir = Directory.CreateTempSubdirectory("trafego-balsa").FullName;
            try
            {
                var settings = new SimulationSettings { StartTimeOfDay = 7 * 3600, DurationMinutes = 40 };
                using var session = new LibsumoSession(SumoScenario.Prepare(s.Network, dir, settings), "anim_");
                SimSnapshot last;
                do last = session.Step(600);
                while (!last.Finished);
                var ferries = session.Ferries!;
                Print(ferries);
                AssertSound(ferries, s.Network.Ferries[0], last);
                var trips = ferries.Logs()[0].Trips;
                Assert.Contains(trips, t => t.Direction == 0 && t.Vehicles > 0);
                Assert.Contains(trips, t => t.Direction == 1 && t.Vehicles > 0);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Avaliar_com_balsa_gera_os_resultados_dela()
    {
        if (!LibsumoSession.Available) return;
        var (net, ferry) = River(twoWay: true);
        var dir = Directory.CreateTempSubdirectory("trafego-balsa").FullName;
        try
        {
            var scenario = Prepare(net, dir, minutes: 45);
            // Reabrir a pasta (como o processo auxiliar faz) dá as mesmas balsas.
            Assert.Equal(scenario.Ferries, SumoScenario.Open(dir).Ferries);

            var set = await SumoBatch.RunAsync(scenario, "teste");
            Assert.Equal(2, set.Runs.Count);
            var result = set.MeanMeasurements().Single(m => m.Kind == MeasurementKind.Ferry);
            foreach (var (key, label) in MeasurementResult.FieldsOf(MeasurementKind.Ferry))
                output.WriteLine($"{label}: {result.Total.GetValueOrDefault(key, double.NaN):0.0}");
            Assert.Equal(ferry.Name, result.Name);
            Assert.True(result.Total["trips"] >= 8);
            Assert.True(result.Total["vehicles"] > 40);
            Assert.InRange(result.Total["occupancy"], 1, 100);
            Assert.True(result.Total["meanWait"] > 0);
            Assert.True(result.Total["maxWait"] >= result.Total["meanWait"]);
            Assert.Equal(3, result.Intervals.Count); // 45 min em intervalos de 15
            Assert.All(set.Runs, r => Assert.Equal(0, r.Kpis.Teleports));
            Assert.All(set.Runs, r => Assert.Equal(0, r.Kpis.Collisions));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
