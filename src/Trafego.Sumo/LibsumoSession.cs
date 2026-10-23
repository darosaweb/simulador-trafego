#if LIBSUMO
using Eclipse.Sumo.Libsumo;
#endif

namespace Trafego.Sumo;

/// <summary>Veículo num instante, em coordenadas do editor (m, Y para baixo).</summary>
/// <param name="X">Centro do veículo.</param>
/// <param name="Y">Centro do veículo.</param>
/// <param name="Heading">Direção em radianos no sistema do editor (0 = leste, sentido horário na tela).</param>
public sealed record VehicleState(string Id, double X, double Y, double Heading, double Length, double Width, double Speed, bool Truck);

public sealed record SimSnapshot(
    double Time,
    IReadOnlyList<VehicleState> Vehicles,
    int Departed,
    int Arrived,
    int Stopped,
    double MeanSpeed,
    bool Finished,
    IReadOnlyDictionary<string, string>? Signals = null)
{
    public static readonly SimSnapshot Empty = new(0, [], 0, 0, 0, 0, false);
}

/// <summary>
/// Simulação rodando no próprio processo pelo libsumo. O libsumo só aceita uma simulação
/// por processo e não é thread-safe: use sempre da mesma thread e feche antes de abrir outra.
/// </summary>
public sealed class LibsumoSession : IDisposable
{
    /// <summary>Prefixo das saídas da simulação animada.</summary>
    public const string AnimationPrefix = "anim_";

    private static LibsumoSession? _current;

    private readonly SumoScenario _scenario;
    private readonly Dictionary<string, (double Length, double Width)> _typeSize = [];
    private int _departed;
    private int _arrived;
    private bool _closed;

    public static bool Available =>
#if LIBSUMO
        SumoInstall.Home is not null;
#else
        false;
#endif

    /// <param name="outputPrefix">Prefixo dos arquivos de saída desta rodada (viagens, detectores…).</param>
    public LibsumoSession(SumoScenario scenario, string outputPrefix = AnimationPrefix)
    {
        _scenario = scenario;
#if LIBSUMO
        if (SumoInstall.Home is null) throw SumoInstall.NotFound();
        if (_current is not null) throw new InvalidOperationException("Já existe uma simulação aberta.");
        AddSumoToPath();
        var args = new StringVector { "sumo" };
        foreach (var a in scenario.SumoArgs("--output-prefix", outputPrefix)) args.Add(a);
        Simulation.start(args);
        Time = Simulation.getTime();
        _current = this;
#else
        throw new InvalidOperationException("Este build foi compilado sem o libsumo (SUMO_HOME não estava definido).");
#endif
    }

    /// <summary>A DLL do libsumo depende de outras DLLs do SUMO; o Windows as procura no PATH.</summary>
    private static void AddSumoToPath()
    {
        var bin = SumoInstall.BinDir;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!path.Split(Path.PathSeparator).Contains(bin, StringComparer.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + path);
    }

    public double Time { get; private set; }
    public double StepLength => _scenario.StepLength;
    public double BeginTime => _scenario.BeginTime;
    public double EndTime => _scenario.EndTime;

    /// <summary>Avança n passos e devolve o estado no fim.</summary>
    public SimSnapshot Step(int steps)
    {
#if LIBSUMO
        if (_closed) throw new ObjectDisposedException(nameof(LibsumoSession));
        var finished = false;
        for (var i = 0; i < steps; i++)
        {
            if (Time >= _scenario.EndTime - 1e-9) { finished = true; break; }
            Simulation.step();
            Time = Simulation.getTime();
            _departed += Simulation.getDepartedNumber();
            _arrived += Simulation.getArrivedNumber();
        }
        return Snapshot(finished);
#else
        throw new InvalidOperationException();
#endif
    }

#if LIBSUMO
    private SimSnapshot Snapshot(bool finished)
    {
        var ids = Vehicle.getIDList();
        var list = new List<VehicleState>(ids.Count);
        double speedSum = 0;
        var stopped = 0;
        foreach (var id in ids)
        {
            var type = Vehicle.getTypeID(id);
            if (!_typeSize.TryGetValue(type, out var size))
                _typeSize[type] = size = (VehicleType.getLength(type), VehicleType.getWidth(type));

            // Posição do SUMO = meio da frente do veículo; ângulo em graus, 0 = norte, horário.
            var pos = Vehicle.getPosition(id);
            var a = Vehicle.getAngle(id) * Math.PI / 180;
            var fx = Math.Sin(a);
            var fy = -Math.Cos(a); // no editor Y é para baixo
            var front = SumoExport.FromSumo(pos.x, pos.y);
            var speed = Vehicle.getSpeed(id);
            speedSum += speed;
            if (speed < 0.1) stopped++;
            list.Add(new VehicleState(id,
                front.X - fx * size.Length / 2,
                front.Y - fy * size.Length / 2,
                Math.Atan2(fy, fx),
                size.Length, size.Width, speed,
                type == "truck"));
        }
        return new SimSnapshot(Time, list, _departed, _arrived, stopped,
            list.Count > 0 ? speedSum / list.Count : 0,
            finished || (Time >= _scenario.EndTime - 1e-9),
            _scenario.Export.TrafficLightIds.Values.ToDictionary(id => id, id => TrafficLight.getRedYellowGreenState(id)));
    }
#endif

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
#if LIBSUMO
        try { Simulation.close(); }
        finally { _current = null; }
#endif
    }
}
