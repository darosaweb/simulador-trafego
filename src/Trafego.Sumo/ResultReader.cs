using System.Globalization;
using System.Xml.Linq;
using Trafego.Core.Model;
using Trafego.Core.Results;

namespace Trafego.Sumo;

/// <summary>Lê as saídas de uma rodada do SUMO (com o prefixo da rodada) e monta o resultado.</summary>
public static class ResultReader
{
    public static RunResult Read(SumoScenario scenario, string prefix, int seed)
    {
        var run = new RunResult { Seed = seed };
        ReadKpis(scenario, prefix, run.Kpis);
        var intervals = Intervals(scenario);
        foreach (var m in scenario.Network.Measurements)
        {
            var result = new MeasurementResult { Id = m.Id, Name = m.Name, Kind = m.Kind };
            switch (m.Kind)
            {
                case MeasurementKind.Count: ReadCount(scenario, prefix, m, intervals, result); break;
                case MeasurementKind.Queue: ReadQueue(scenario, prefix, m, intervals, result); break;
                default: ReadTravelTime(scenario, prefix, m, intervals, result); break;
            }
            run.Measurements.Add(result);
        }
        run.Links = ReadLinks(scenario, prefix);
        return run;
    }

    private static XElement? Load(SumoScenario s, string prefix, string file)
    {
        var path = s.OutputPath(prefix, file);
        if (!File.Exists(path)) return null;
        try { return XDocument.Load(path).Root; }
        catch (System.Xml.XmlException) { return null; } // saída incompleta (simulação interrompida)
    }

    private static double D(XElement e, string attr, double fallback = double.NaN) =>
        double.TryParse((string?)e.Attribute(attr), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static List<(double Begin, double End)> Intervals(SumoScenario s)
    {
        var period = Math.Max(60, s.Settings.ResultIntervalMinutes * 60);
        var list = new List<(double, double)>();
        for (var t = s.BeginTime; t < s.EndTime - 1e-6; t += period) list.Add((t, Math.Min(t + period, s.EndTime)));
        return list;
    }

    private static void ReadKpis(SumoScenario s, string prefix, NetworkKpis k)
    {
        var trips = Load(s, prefix, SumoScenario.TripInfoOutput)?.Elements("tripinfo").ToList() ?? [];
        k.Completed = trips.Count;
        if (trips.Count > 0)
        {
            k.MeanTravelTime = trips.Average(t => D(t, "duration", 0));
            k.MeanDelay = trips.Average(t => D(t, "timeLoss", 0));
            k.MeanStoppedTime = trips.Average(t => D(t, "waitingTime", 0));
            k.MeanStops = trips.Average(t => D(t, "waitingCount", 0));
            var dur = trips.Sum(t => D(t, "duration", 0));
            k.MeanSpeedKmh = dur > 0 ? trips.Sum(t => D(t, "routeLength", 0)) / dur * 3.6 : 0;
            k.TotalDelayHours = trips.Sum(t => D(t, "timeLoss", 0)) / 3600;
        }
        else
        {
            k.MeanTravelTime = k.MeanDelay = k.MeanStoppedTime = k.MeanStops = k.MeanSpeedKmh = double.NaN;
        }

        if (Load(s, prefix, SumoScenario.StatisticOutput) is { } stats)
        {
            if (stats.Element("vehicles") is { } v)
            {
                k.NotInserted = D(v, "waiting", 0);
                k.Running = D(v, "running", 0);
            }
            k.Teleports = stats.Element("teleports") is { } tp ? D(tp, "total", 0) : 0;
            k.Collisions = stats.Element("safety") is { } sf ? D(sf, "collisions", 0) : 0;
        }
    }

    /// <summary>Linhas de saída de um detector (por faixa, se laneCount &gt; 0), agrupadas pelo início do intervalo.</summary>
    private static Dictionary<double, List<XElement>> ByInterval(XElement? root, int measurementId, bool perLane)
    {
        var result = new Dictionary<double, List<XElement>>();
        if (root is null) return result;
        var prefix = SumoExport.DetectorId(measurementId);
        foreach (var e in root.Elements("interval"))
        {
            var id = (string?)e.Attribute("id") ?? "";
            if (perLane ? !id.StartsWith(prefix + "_l", StringComparison.Ordinal) : id != prefix) continue;
            var begin = D(e, "begin");
            if (!result.TryGetValue(begin, out var list)) result[begin] = list = [];
            list.Add(e);
        }
        return result;
    }

    private static void ReadCount(SumoScenario s, string prefix, Measurement m, List<(double, double)> intervals, MeasurementResult r)
    {
        var rows = ByInterval(Load(s, prefix, SumoExport.CountOutput), m.Id, perLane: true);
        double totalVeh = 0, speedSum = 0, speedVeh = 0, totalTime = 0;
        foreach (var (b, e) in intervals)
        {
            var lanes = rows.GetValueOrDefault(b, []);
            var veh = lanes.Sum(x => D(x, "nVehContrib", 0));
            var withSpeed = lanes.Where(x => D(x, "speed", -1) >= 0).ToList();
            var sv = withSpeed.Sum(x => D(x, "nVehContrib", 0));
            var ss = withSpeed.Sum(x => D(x, "speed", 0) * D(x, "nVehContrib", 0));
            if (lanes.Count == 0) continue; // a simulação não chegou neste intervalo
            r.Intervals.Add(new IntervalValues
            {
                Begin = b, End = e,
                Values = { ["vehicles"] = veh, ["volume"] = veh / (e - b) * 3600, ["speed"] = sv > 0 ? ss / sv * 3.6 : double.NaN },
            });
            totalVeh += veh; speedSum += ss; speedVeh += sv; totalTime += e - b;
        }
        r.Total["vehicles"] = totalVeh;
        r.Total["volume"] = totalTime > 0 ? totalVeh / totalTime * 3600 : double.NaN;
        r.Total["speed"] = speedVeh > 0 ? speedSum / speedVeh * 3.6 : double.NaN;
    }

    private static void ReadQueue(SumoScenario s, string prefix, Measurement m, List<(double, double)> intervals, MeasurementResult r)
    {
        var rows = ByInterval(Load(s, prefix, SumoExport.QueueOutput), m.Id, perLane: true);
        var means = new List<double>();
        double max = 0, stops = 0;
        foreach (var (b, e) in intervals)
        {
            if (!rows.TryGetValue(b, out var lanes)) continue;
            // A fila do ponto é a da faixa mais carregada.
            var mean = lanes.Max(x => D(x, "meanMaxJamLengthInMeters", 0));
            var mx = lanes.Max(x => D(x, "maxJamLengthInMeters", 0));
            var st = lanes.Sum(x => D(x, "startedHalts", 0));
            r.Intervals.Add(new IntervalValues { Begin = b, End = e, Values = { ["meanQueue"] = mean, ["maxQueue"] = mx, ["stops"] = st } });
            means.Add(mean);
            max = Math.Max(max, mx);
            stops += st;
        }
        r.Total["meanQueue"] = means.Count > 0 ? means.Average() : double.NaN;
        r.Total["maxQueue"] = means.Count > 0 ? max : double.NaN;
        r.Total["stops"] = stops;
    }

    private static void ReadTravelTime(SumoScenario s, string prefix, Measurement m, List<(double, double)> intervals, MeasurementResult r)
    {
        var rows = ByInterval(Load(s, prefix, SumoExport.TravelTimeOutput), m.Id, perLane: false);
        double veh = 0, tt = 0, delay = 0;
        foreach (var (b, e) in intervals)
        {
            if (!rows.TryGetValue(b, out var list) || list.Count == 0) continue;
            var x = list[0];
            var n = D(x, "vehicleSum", 0);
            var t = D(x, "meanTravelTime", -1);
            var d = D(x, "meanTimeLoss", -1);
            r.Intervals.Add(new IntervalValues
            {
                Begin = b, End = e,
                Values = { ["vehicles"] = n, ["travelTime"] = n > 0 && t >= 0 ? t : double.NaN, ["delay"] = n > 0 && d >= 0 ? d : double.NaN },
            });
            if (n > 0 && t >= 0) { veh += n; tt += t * n; delay += Math.Max(d, 0) * n; }
        }
        r.Total["vehicles"] = veh;
        r.Total["travelTime"] = veh > 0 ? tt / veh : double.NaN;
        r.Total["delay"] = veh > 0 ? delay / veh : double.NaN;
    }

    /// <summary>Junta os trechos (edges) de cada link: volume do trecho mais carregado, velocidade ponderada pelo tempo.</summary>
    private static List<LinkResult> ReadLinks(SumoScenario s, string prefix)
    {
        var result = new List<LinkResult>();
        var root = Load(s, prefix, SumoExport.EdgeDataOutput);
        if (root is null) return result;

        var edges = root.Elements("interval").SelectMany(i => i.Elements("edge").Select(e => (Interval: i, Edge: e)))
            .GroupBy(x => (string)x.Edge.Attribute("id")!)
            .ToDictionary(g => g.Key, g => g.ToList());
        var duration = root.Elements("interval").Sum(i => D(i, "end", 0) - D(i, "begin", 0));
        if (duration <= 0) return result;

        foreach (var (linkId, pieces) in s.Export.LinkPieces)
        {
            if (s.Network.FindLink(linkId) is not { } link) continue;
            double maxEntered = 0, sampled = 0, speedTime = 0, timeLoss = 0, densitySum = 0, densityN = 0;
            foreach (var piece in pieces)
            {
                if (!edges.TryGetValue(piece.EdgeId, out var rows)) continue;
                // "entered" não conta quem foi inserido no próprio trecho (esses estão em "departed").
                maxEntered = Math.Max(maxEntered, rows.Sum(r => D(r.Edge, "entered", 0) + D(r.Edge, "departed", 0)));
                foreach (var (_, e) in rows)
                {
                    var ss = D(e, "sampledSeconds", 0);
                    sampled += ss;
                    speedTime += D(e, "speed", 0) * ss;
                    timeLoss += D(e, "timeLoss", 0);
                    densitySum += D(e, "laneDensity", 0);
                    densityN++;
                }
            }
            var speed = sampled > 0 ? speedTime / sampled * 3.6 : double.NaN;
            result.Add(new LinkResult
            {
                LinkId = linkId,
                Name = link.Name,
                VolumeVph = maxEntered / duration * 3600,
                MeanSpeedKmh = speed,
                SpeedRatio = double.IsFinite(speed) ? Math.Clamp(speed / link.SpeedKmh, 0, 1) : double.NaN,
                DelayPerVehicle = maxEntered > 0 ? timeLoss / maxEntered : double.NaN,
                Density = densityN > 0 ? densitySum / densityN : double.NaN,
            });
        }
        return result;
    }
}
