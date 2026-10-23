using System.Xml.Linq;
using Trafego.Core.Model;
using Trafego.Core.Results;
using Trafego.Sumo;

namespace Trafego.Core.Tests;

[Collection(Libsumo.Collection)]
public class ResultTests
{
    private static double H(double hours) => hours * 3600;

    private static SimulationSettings Settings(double minutes, int replications = 1) => new()
    {
        StartTimeOfDay = H(7), DurationMinutes = minutes, ResultIntervalMinutes = 5, Replications = replications, Seed = 1,
    };

    [Fact]
    public void Detectores_ficam_nos_trechos_certos_e_dentro_das_faixas()
    {
        var (net, main, ramp, _) = SumoExportTests.Diverge();
        net.AddMeasurement(MeasurementKind.Count, main, 50);
        var q = net.AddMeasurement(MeasurementKind.Queue, main, 100); // no corte: olha o trecho que termina ali
        var tt = net.AddMeasurement(MeasurementKind.TravelTime, main, 10);
        tt.ToLinkId = ramp.Id;
        tt.ToS = ramp.Axis().Length - 5;
        var e = SumoExport.From(net);
        var lengths = e.LinkPieces.Values.SelectMany(p => p).SelectMany(p => Enumerable.Range(0, 3).Select(l => $"{p.EdgeId}_{l}"))
            .ToDictionary(id => id, _ => 95.0);

        var root = e.Detectors(lengths, H(7), H(8), 300).Root!;
        var loops = root.Elements("inductionLoop").ToList();
        Assert.Equal(3, loops.Count); // uma por faixa
        Assert.All(loops, l => Assert.StartsWith(e.LinkPieces[main.Id][0].EdgeId + "_", (string)l.Attribute("lane")!));
        Assert.Equal(47.5, (double)loops[0].Attribute("pos")!, 6); // 50 de 100 m → metade de 95 m

        var areas = root.Elements("laneAreaDetector").ToList();
        Assert.Equal(3, areas.Count);
        Assert.All(areas, a => Assert.True((double)a.Attribute("endPos")! <= 95 && (double)a.Attribute("pos")! >= 0));
        Assert.Equal(e.LinkPieces[main.Id][0].EdgeId + "_0", (string?)areas[0].Attribute("lane"));

        var e3 = Assert.Single(root.Elements("entryExitDetector"));
        Assert.Equal(3, e3.Elements("detEntry").Count());
        Assert.Single(e3.Elements("detExit"));
        Assert.NotNull(root.Element("edgeData"));
        Assert.Equal(SumoExport.DetectorId(q.Id, 0), (string?)areas[0].Attribute("id"));
    }

    [Fact]
    public void Avaliacao_com_replicacoes_mede_volume_fila_e_tempo_de_viagem()
    {
        if (SumoInstall.Home is null) return;
        var (x, _, _) = SignalTests.SignalizedCross();
        x.Net.AddInput(x.WIn, 600).TruckShare = 0;
        x.Net.AddInput(x.SIn, 600).TruckShare = 0;
        var count = x.Net.AddMeasurement(MeasurementKind.Count, x.WIn, 60);
        var queue = x.Net.AddMeasurement(MeasurementKind.Queue, x.WIn, x.WIn.Axis().Length);
        var tt = x.Net.AddMeasurement(MeasurementKind.TravelTime, x.WIn, 20);
        tt.ToLinkId = x.EOut.Id;
        tt.ToS = 100;

        var dir = Directory.CreateTempSubdirectory("trafego-res").FullName;
        try
        {
            var scenario = SumoScenario.Prepare(x.Net, dir, Settings(20, replications: 3));
            var set = SumoBatch.RunAsync(scenario, "teste").GetAwaiter().GetResult();

            Assert.Equal([1, 2, 3], set.Seeds);
            var completed = set.Kpi(nameof(NetworkKpis.Completed));
            Assert.Equal(3, completed.N);
            Assert.InRange(completed.Mean, 300, 450); // 1200 veíc/h por 20 min ≈ 400, menos os que ainda estão na rede
            Assert.True(set.Kpi(nameof(NetworkKpis.MeanDelay)).Mean > 5, "semáforo deve causar atraso");
            Assert.Equal(0, set.Kpi(nameof(NetworkKpis.Collisions)).Mean);

            var ms = set.MeanMeasurements();
            var c = ms.Single(m => m.Id == count.Id);
            Assert.Equal(4, c.Intervals.Count); // 20 min em intervalos de 5
            Assert.InRange(c.Total["volume"], 480, 720);
            var q = ms.Single(m => m.Id == queue.Id);
            Assert.True(q.Total["maxQueue"] > 10, $"fila máxima {q.Total["maxQueue"]}");
            Assert.True(q.Total["meanQueue"] < q.Total["maxQueue"]);
            var t = ms.Single(m => m.Id == tt.Id);
            var freeFlow = (x.WIn.Axis().Length - 20 + 20 + 100) / (60 / 3.6);
            Assert.True(t.Total["travelTime"] > freeFlow, $"tempo {t.Total["travelTime"]:0.0} s, livre {freeFlow:0.0} s");
            Assert.True(t.Total["vehicles"] > 100);

            var link = set.MeanLinks().Single(l => l.LinkId == x.WIn.Id);
            Assert.InRange(link.VolumeVph, 480, 720);
            Assert.InRange(link.SpeedRatio, 0.05, 1);

            // Ida e volta em JSON (inclusive NaN).
            var file = Path.Combine(dir, "r" + ResultSet.Extension);
            set.Save(file);
            var back = ResultSet.Load(file);
            Assert.Equal(set.Kpi(nameof(NetworkKpis.Completed)).Mean, back.Kpi(nameof(NetworkKpis.Completed)).Mean);
            Assert.Equal(3, back.Runs.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Simulacao_animada_tambem_gera_resultados_ao_fechar()
    {
        if (!LibsumoSession.Available) return;
        var (net, main, _, _) = SumoExportTests.Diverge();
        net.Inputs[0].TruckShare = 0;
        var count = net.AddMeasurement(MeasurementKind.Count, main, 50);
        var dir = Directory.CreateTempSubdirectory("trafego-anim").FullName;
        try
        {
            var scenario = SumoScenario.Prepare(net, dir, Settings(10));
            using (var session = new LibsumoSession(scenario))
                session.Step(3000); // 5 min
            var run = ResultReader.Read(scenario, LibsumoSession.AnimationPrefix, 1);
            Assert.True(run.Kpis.Completed > 30);
            var c = run.Measurements.Single(m => m.Id == count.Id);
            Assert.NotEmpty(c.Intervals);
            Assert.True(c.Total["vehicles"] > 30);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Estatistica_entre_replicacoes()
    {
        var s = Stat.Of([10, 12, 14, double.NaN]);
        Assert.Equal(3, s.N);
        Assert.Equal(12, s.Mean, 9);
        Assert.Equal(2, s.StdDev, 9);
        Assert.Equal(0, Stat.Of([5]).StdDev);
    }
}
