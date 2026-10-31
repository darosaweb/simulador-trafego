using System.Text.Json;
using Trafego.Core.Model;

namespace Trafego.Core.Results;

/// <summary>Valor médio entre replicações, com o desvio-padrão (0 com uma replicação só).</summary>
public sealed record Stat(double Mean, double StdDev, int N)
{
    public static Stat Of(IEnumerable<double> values)
    {
        var v = values.Where(double.IsFinite).ToList();
        if (v.Count == 0) return new Stat(double.NaN, 0, 0);
        var mean = v.Average();
        var sd = v.Count > 1 ? Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / (v.Count - 1)) : 0;
        return new Stat(mean, sd, v.Count);
    }
}

/// <summary>Indicadores de desempenho da rede numa replicação.</summary>
public sealed class NetworkKpis
{
    /// <summary>Veículos que completaram a viagem dentro do período.</summary>
    public double Completed { get; set; }
    /// <summary>Veículos que não conseguiram entrar na rede (fila na entrada) ao fim do período.</summary>
    public double NotInserted { get; set; }
    /// <summary>Veículos ainda na rede ao fim do período.</summary>
    public double Running { get; set; }
    public double MeanTravelTime { get; set; }
    /// <summary>Atraso médio: tempo perdido em relação a andar na velocidade desejada, s/veíc.</summary>
    public double MeanDelay { get; set; }
    /// <summary>Tempo médio parado, s/veíc.</summary>
    public double MeanStoppedTime { get; set; }
    public double MeanStops { get; set; }
    public double MeanSpeedKmh { get; set; }
    /// <summary>Atraso total, veíc·h.</summary>
    public double TotalDelayHours { get; set; }
    public double Teleports { get; set; }
    public double Collisions { get; set; }

    /// <summary>Nome legível de cada indicador, na ordem de exibição.</summary>
    public static readonly (string Key, string Label, string Unit)[] Fields =
    [
        (nameof(Completed), "Veículos atendidos", "veíc"),
        (nameof(NotInserted), "Veículos sem conseguir entrar", "veíc"),
        (nameof(Running), "Veículos ainda na rede no fim", "veíc"),
        (nameof(MeanTravelTime), "Tempo de viagem médio", "s"),
        (nameof(MeanDelay), "Atraso médio", "s/veíc"),
        (nameof(MeanStoppedTime), "Tempo parado médio", "s/veíc"),
        (nameof(MeanStops), "Paradas por veículo", ""),
        (nameof(MeanSpeedKmh), "Velocidade média", "km/h"),
        (nameof(TotalDelayHours), "Atraso total", "veíc·h"),
        (nameof(Teleports), "Teleportes (travamentos)", ""),
        (nameof(Collisions), "Colisões", ""),
    ];

    public double Get(string key) => (double)typeof(NetworkKpis).GetProperty(key)!.GetValue(this)!;
    public void Set(string key, double v) => typeof(NetworkKpis).GetProperty(key)!.SetValue(this, v);
}

/// <summary>Valores de uma medição num intervalo de tempo (chaves dependem do tipo de medição).</summary>
public sealed class IntervalValues
{
    public double Begin { get; set; }
    public double End { get; set; }
    public Dictionary<string, double> Values { get; set; } = [];
}

/// <summary>Resultado de uma medição: por intervalo e no período todo.</summary>
public sealed class MeasurementResult
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public MeasurementKind Kind { get; set; }
    public List<IntervalValues> Intervals { get; set; } = [];
    public Dictionary<string, double> Total { get; set; } = [];

    /// <summary>Rótulos das chaves de cada tipo de medição, na ordem de exibição.</summary>
    public static (string Key, string Label)[] FieldsOf(MeasurementKind k) => k switch
    {
        MeasurementKind.Count => [("volume", "Volume (veíc/h)"), ("speed", "Velocidade (km/h)"), ("vehicles", "Veículos")],
        MeasurementKind.Queue => [("meanQueue", "Fila média (m)"), ("maxQueue", "Fila máxima (m)"), ("stops", "Paradas")],
        _ => [("travelTime", "Tempo de viagem (s)"), ("delay", "Atraso (s)"), ("vehicles", "Veículos")],
    };
}

/// <summary>Resultado agregado de um link (todos os trechos dele).</summary>
public sealed class LinkResult
{
    public int LinkId { get; set; }
    public string Name { get; set; } = "";
    public double VolumeVph { get; set; }
    public double MeanSpeedKmh { get; set; }
    /// <summary>Velocidade média dividida pela máxima do link (0 a 1), para colorir o mapa.</summary>
    public double SpeedRatio { get; set; }
    public double DelayPerVehicle { get; set; }
    /// <summary>Densidade média, veíc/km por faixa.</summary>
    public double Density { get; set; }
}

/// <summary>Resultado de uma replicação.</summary>
public sealed class RunResult
{
    public int Seed { get; set; }
    public NetworkKpis Kpis { get; set; } = new();
    public List<MeasurementResult> Measurements { get; set; } = [];
    public List<LinkResult> Links { get; set; } = [];
}

/// <summary>
/// Conjunto de resultados de uma avaliação (uma ou mais replicações), guardado em JSON ao lado do
/// projeto. É o que a janela de resultados mostra e compara.
/// </summary>
public sealed class ResultSet
{
    public string Name { get; set; } = "";
    public string ProjectName { get; set; } = "";
    /// <summary>Cenário do projeto que foi simulado.</summary>
    public string ScenarioName { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public SimulationSettings Settings { get; set; } = new();
    /// <summary>Verdadeiro quando a simulação foi interrompida antes do fim.</summary>
    public bool Partial { get; set; }
    public List<RunResult> Runs { get; set; } = [];

    public List<int> Seeds => [.. Runs.Select(r => r.Seed)];

    public Stat Kpi(string key) => Stat.Of(Runs.Select(r => r.Kpis.Get(key)));

    /// <summary>Medições com a média das replicações em cada intervalo e no total.</summary>
    public List<MeasurementResult> MeanMeasurements()
    {
        var result = new List<MeasurementResult>();
        if (Runs.Count == 0) return result;
        foreach (var first in Runs[0].Measurements)
        {
            var all = Runs.Select(r => r.Measurements.Find(m => m.Id == first.Id)).OfType<MeasurementResult>().ToList();
            var mean = new MeasurementResult { Id = first.Id, Name = first.Name, Kind = first.Kind };
            for (var i = 0; i < first.Intervals.Count; i++)
            {
                var iv = new IntervalValues { Begin = first.Intervals[i].Begin, End = first.Intervals[i].End };
                foreach (var key in first.Intervals[i].Values.Keys)
                    iv.Values[key] = Stat.Of(all.Where(m => i < m.Intervals.Count)
                        .Select(m => m.Intervals[i].Values.GetValueOrDefault(key, double.NaN))).Mean;
                mean.Intervals.Add(iv);
            }
            foreach (var key in first.Total.Keys)
                mean.Total[key] = Stat.Of(all.Select(m => m.Total.GetValueOrDefault(key, double.NaN))).Mean;
            result.Add(mean);
        }
        return result;
    }

    /// <summary>Links com a média das replicações.</summary>
    public List<LinkResult> MeanLinks()
    {
        if (Runs.Count == 0) return [];
        return [.. Runs[0].Links.Select(first =>
        {
            var all = Runs.Select(r => r.Links.Find(l => l.LinkId == first.LinkId)).OfType<LinkResult>().ToList();
            return new LinkResult
            {
                LinkId = first.LinkId,
                Name = first.Name,
                VolumeVph = Stat.Of(all.Select(l => l.VolumeVph)).Mean,
                MeanSpeedKmh = Stat.Of(all.Select(l => l.MeanSpeedKmh)).Mean,
                SpeedRatio = Stat.Of(all.Select(l => l.SpeedRatio)).Mean,
                DelayPerVehicle = Stat.Of(all.Select(l => l.DelayPerVehicle)).Mean,
                Density = Stat.Of(all.Select(l => l.Density)).Mean,
            };
        })];
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public const string Extension = ".resultado.json";

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Options));

    public static ResultSet Load(string path) =>
        JsonSerializer.Deserialize<ResultSet>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException("Arquivo de resultados vazio.");

    /// <summary>Pasta de resultados de um projeto: "cenario.resultados" ao lado de "cenario.trafego.json".</summary>
    public static string FolderFor(string projectPath)
    {
        var name = Path.GetFileName(projectPath);
        name = name.EndsWith(Serialization.ProjectFile.Extension, StringComparison.OrdinalIgnoreCase)
            ? name[..^Serialization.ProjectFile.Extension.Length]
            : Path.GetFileNameWithoutExtension(name);
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectPath))!, name + ".resultados");
    }
}
