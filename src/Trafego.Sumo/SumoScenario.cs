using System.Text.Json;
using Trafego.Core.Model;

namespace Trafego.Sumo;

/// <summary>
/// Arquivos do SUMO de um cenário, gerados numa pasta. A pasta fica para consulta:
/// dá para abrir a rede no netedit ou rodar no sumo-gui para comparar.
/// </summary>
public sealed class SumoScenario
{
    public const string NetFile = "rede.net.xml";
    public const string RouteFile = "rotas.rou.xml";
    public const string AdditionalFile = "semaforos.add.xml";
    public const string ConfigFile = "cenario.sumocfg";

    public required string Directory { get; init; }
    public required SumoExport Export { get; init; }
    /// <summary>Início e fim no relógio (s desde 00:00 do primeiro dia): a simulação roda nesse intervalo.</summary>
    public required double BeginTime { get; init; }
    public required double EndTime { get; init; }
    public double StepLength { get; init; } = 0.1;
    public int Seed { get; init; } = 1;
    public required SimulationSettings Settings { get; init; }
    /// <summary>Rede exportada (para dar nome às medições e aos links nos resultados).</summary>
    public required Network Network { get; init; }
    /// <summary>Balsas da rede, já nas edges do SUMO.</summary>
    public List<FerrySetup> Ferries { get; init; } = [];

    /// <summary>Rede e configuração do cenário, para abrir a pasta de novo sem rodar o netconvert (replicações em outro processo).</summary>
    public const string StateFile = "cenario.trafego.json";

    public string NetPath => Path.Combine(Directory, NetFile);
    public string RoutePath => Path.Combine(Directory, RouteFile);
    public string ConfigPath => Path.Combine(Directory, ConfigFile);

    /// <summary>Exporta a rede, roda o netconvert e grava rotas, semáforos e configuração.</summary>
    public static SumoScenario Prepare(Network net, string directory, SimulationSettings? settings = null, int? seed = null)
    {
        settings ??= new SimulationSettings();
        if (net.Links.Count == 0) throw new InvalidOperationException("A rede não tem links.");
        if (!(settings.DurationMinutes > 0)) throw new InvalidOperationException("A duração da simulação deve ser maior que zero.");
        var errors = Validation.Check(net).Where(i => i.Severity == Severity.Error).ToList();
        if (errors.Count > 0)
            throw new InvalidOperationException("Corrija a rede antes de simular:\n" + string.Join("\n", errors.Select(e => e.Message)));

        System.IO.Directory.CreateDirectory(directory);
        foreach (var old in new[] { "rede.tll.xml", AdditionalFile })
            File.Delete(Path.Combine(directory, old));

        var export = SumoExport.From(net);
        export.Nodes.Save(Path.Combine(directory, "rede.nod.xml"));
        export.Edges.Save(Path.Combine(directory, "rede.edg.xml"));
        export.Connections.Save(Path.Combine(directory, "rede.con.xml"));

        List<string> args =
        [
            "--node-files", "rede.nod.xml",
            "--edge-files", "rede.edg.xml",
            "--connection-files", "rede.con.xml",
            "--output-file", NetFile,
            "--offset.disable-normalization", "true",
            "--no-turnarounds", "true",
            "--junctions.corner-detail", "5",
            "--no-warnings", "false",
        ];
        if (export.HasTrafficLights)
        {
            export.TrafficLights.Save(Path.Combine(directory, "rede.tll.xml"));
            args.AddRange(["--tllogic-files", "rede.tll.xml"]);
        }

        var result = SumoInstall.Run("netconvert", args, directory);
        if (result.ExitCode != 0 || !File.Exists(Path.Combine(directory, NetFile)))
            throw new InvalidOperationException("O netconvert falhou:\n" + result.Output);
        export.Warnings.AddRange(result.Output.Split('\n')
            .Where(l => l.StartsWith("Warning", StringComparison.OrdinalIgnoreCase)).Select(l => l.Trim()));

        if (net.Inputs.Count == 0) export.Warnings.Add("A rede não tem entradas de veículos.");
        var scenario = new SumoScenario
        {
            Directory = directory,
            Export = export,
            BeginTime = settings.Begin,
            EndTime = settings.End,
            Seed = seed ?? settings.Seed,
            Settings = settings,
            Network = net,
            Ferries = FerriesOf(net, export),
        };
        File.WriteAllText(Path.Combine(directory, StateFile), JsonSerializer.Serialize(new State(net, settings, scenario.Seed), StateOptions));
        export.Routes(scenario.BeginTime, scenario.EndTime).Save(scenario.RoutePath);

        var additionalFiles = new List<string>();
        var additional = export.Additional(scenario.BeginTime, scenario.EndTime);
        if (additional.Root!.HasElements)
        {
            additional.Save(Path.Combine(directory, AdditionalFile));
            additionalFiles.Add(AdditionalFile);
        }
        var period = Math.Max(60, settings.ResultIntervalMinutes * 60);
        export.Detectors(LaneLengths(scenario.NetPath), scenario.BeginTime, scenario.EndTime, period)
            .Save(Path.Combine(directory, DetectorFile));
        additionalFiles.Add(DetectorFile);

        File.WriteAllText(scenario.ConfigPath, $"""
            <configuration>
              <input>
                <net-file value="{NetFile}"/>
                <route-files value="{RouteFile}"/>
                <additional-files value="{string.Join(",", additionalFiles)}"/>
              </input>
              <output>
                <tripinfo-output value="{TripInfoOutput}"/>
                <statistic-output value="{StatisticOutput}"/>
              </output>
              <time>
                <begin value="{SumoExport.F(scenario.BeginTime)}"/>
                <end value="{SumoExport.F(scenario.EndTime)}"/>
                <step-length value="{SumoExport.F(scenario.StepLength)}"/>
              </time>
              <random_number>
                <seed value="{scenario.Seed}"/>
              </random_number>
              {(scenario.Ferries.Count > 0 ? NoTeleport : "")}
            </configuration>
            """);
        return scenario;
    }

    /// <summary>
    /// Com balsa, a fila da rampa fica parada muito tempo esperando a embarcação: sem isto o SUMO "teleporta"
    /// quem fica preso mais de 300 s.
    /// </summary>
    private const string NoTeleport = "<processing><time-to-teleport value=\"-1\"/></processing>";

    private sealed record State(Network Network, SimulationSettings Settings, int Seed);

    private static readonly JsonSerializerOptions StateOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Abre um cenário já preparado (sem rodar o netconvert de novo).</summary>
    public static SumoScenario Open(string directory)
    {
        var state = JsonSerializer.Deserialize<State>(File.ReadAllText(Path.Combine(directory, StateFile)), StateOptions)
                    ?? throw new InvalidDataException("Cenário do SUMO sem " + StateFile);
        var export = SumoExport.From(state.Network);
        return new SumoScenario
        {
            Directory = directory,
            Export = export,
            BeginTime = state.Settings.Begin,
            EndTime = state.Settings.End,
            Seed = state.Seed,
            Settings = state.Settings,
            Network = state.Network,
            Ferries = FerriesOf(state.Network, export),
        };
    }

    /// <summary>Cada travessia é um link inteiro, sem cortes: uma edge só.</summary>
    private static List<FerrySetup> FerriesOf(Network net, SumoExport export)
    {
        string EdgeOf(int linkId) => export.LinkPieces.TryGetValue(linkId, out var pieces) && pieces.Count == 1
            ? pieces[0].EdgeId
            : throw new InvalidOperationException($"A travessia {net.FindLink(linkId)?.Name} não pode ter conectores no meio.");
        // Pátio: o último trecho do link (o que termina onde se sai para a rampa).
        string? YardOf(int? linkId) => linkId is { } id && export.LinkPieces.TryGetValue(id, out var pieces) && pieces.Count > 0
            ? pieces[^1].EdgeId : null;
        return [.. net.Ferries.Select(f => new FerrySetup(f.Id, f.Name,
            f.LinkId, EdgeOf(f.LinkId),
            f.ReturnLinkId, f.ReturnLinkId is { } r ? EdgeOf(r) : null,
            f.Vessels, f.DeckLength, f.CrossingMinutes * 60, f.MaxWaitMinutes * 60)
        {
            DeckLanes = Math.Max(1, f.DeckLanes),
            YardEdge = YardOf(f.YardLinkId),
            ReturnYardEdge = f.ReturnLinkId is null ? null : YardOf(f.ReturnYardLinkId),
        })];
    }

    public const string DetectorFile = "medicoes.add.xml";
    public const string TripInfoOutput = "viagens.xml";
    public const string StatisticOutput = "estatisticas.xml";

    /// <summary>Comprimento de cada faixa na rede gerada pelo netconvert (id da faixa → m).</summary>
    public static Dictionary<string, double> LaneLengths(string netPath)
    {
        var result = new Dictionary<string, double>();
        foreach (var lane in System.Xml.Linq.XDocument.Load(netPath).Descendants("lane"))
            if ((string?)lane.Attribute("id") is { } id && (double?)lane.Attribute("length") is { } len)
                result[id] = len;
        return result;
    }

    /// <summary>Caminho de um arquivo de saída de uma rodada (as saídas levam o prefixo da rodada).</summary>
    public string OutputPath(string prefix, string file) => Path.Combine(Directory, prefix + file);

    /// <summary>Argumentos de linha de comando do sumo para este cenário.</summary>
    public List<string> SumoArgs(params string[] extra) =>
    [
        "-c", ConfigPath,
        "--no-step-log", "true",
        "--duration-log.disable", "true",
        "--collision.action", "warn",
        .. extra,
    ];
}
