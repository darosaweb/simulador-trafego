using System.Text.Json;
#if LIBSUMO
using Eclipse.Sumo.Libsumo;
#endif

namespace Trafego.Sumo;

/// <summary>Uma balsa traduzida para o SUMO: as edges da travessia e os parâmetros em segundos.</summary>
public sealed record FerrySetup(
    int FerryId, string Name,
    int LinkId, string Edge,
    int? ReturnLinkId, string? ReturnEdge,
    int Vessels, double DeckLength, double CrossingSeconds, double MaxWaitSeconds)
{
    /// <summary>Faixas do convés (<see cref="DeckLength"/> é o comprimento de cada uma).</summary>
    public int DeckLanes { get; init; } = 1;
    /// <summary>Edge do pátio de acúmulo da margem A (ida) e da B (volta), se houver.</summary>
    public string? YardEdge { get; init; }
    public string? ReturnYardEdge { get; init; }

    /// <summary>Na simulação as faixas do convés ficam uma atrás da outra, numa fila só: o comprimento dela.</summary>
    public double LinearDeck => DeckLanes * DeckLength + (DeckLanes - 1) * Trafego.Core.Model.Ferry.DeckSpacing;
}

/// <summary>
/// Uma embarcação num instante, para desenhar: o trecho do link que o casco ocupa (frações 0–1), as faixas do
/// convés e onde desenhar quem está a bordo.
/// </summary>
public sealed record FerryView(int FerryId, int Vessel, int LinkId, double From, double To, int Aboard, bool Moving)
{
    public int Lanes { get; init; } = 1;
    public IReadOnlyList<FerryRider> Riders { get; init; } = [];
}

/// <summary>
/// Veículo a bordo, no desenho: o centro dele ao longo do link (fração 0–1) e o deslocamento lateral até a faixa do
/// convés (m, positivo à esquerda). Na simulação o convés é uma fila só; o desenho põe as faixas lado a lado.
/// </summary>
public sealed record FerryRider(string Id, double At, double Lateral);

/// <summary>Uma viagem: sentido (0 ida, 1 volta, -1 volta vazia sem link), horários e carga.</summary>
public sealed class FerryTripLog
{
    public int Vessel { get; set; }
    public int Direction { get; set; }
    public double DockedAt { get; set; }
    public double DepartedAt { get; set; }
    public double ArrivedAt { get; set; } = double.NaN;
    public int Vehicles { get; set; }
    public int Trucks { get; set; }
    public double UsedLength { get; set; }
    public bool Full { get; set; }
    public int LeftBehind { get; set; }
    /// <summary>Maior distância entre o primeiro e o último a bordo durante a travessia (controle de qualidade).</summary>
    public double MaxSpread { get; set; }
}

/// <summary>O que aconteceu com uma balsa numa rodada; gravado em "{prefixo}balsas.json" e lido nos resultados.</summary>
public sealed class FerryLog
{
    public int FerryId { get; set; }
    public string Name { get; set; } = "";
    public double DeckLength { get; set; }
    public List<FerryTripLog> Trips { get; set; } = [];
    /// <summary>Embarques: instante, sentido e quanto o veículo esperou na fila (s).</summary>
    public List<double[]> Boardings { get; set; } = [];
    /// <summary>Fila a cada 10 s: instante, sentido, veículos na fila (rampa, pátio e antes dele) e, com pátio, no pátio.</summary>
    public List<double[]> Queue { get; set; } = [];
    /// <summary>Veículos ainda esperando na rampa no fim, por sentido.</summary>
    public int[] WaitingAtEnd { get; set; } = [0, 0];

    public const string File = "balsas.json";
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };
    public static void Save(string path, List<FerryLog> logs) => System.IO.File.WriteAllText(path, JsonSerializer.Serialize(logs, Options));
    public static List<FerryLog> Load(string path) => JsonSerializer.Deserialize<List<FerryLog>>(System.IO.File.ReadAllText(path), Options) ?? [];
}

/// <summary>
/// Balsas sobre a simulação do SUMO, controladas passo a passo pelo libsumo. O SUMO cuida de como os veículos
/// andam e formam fila; aqui se decide quem espera, quem embarca e como a embarcação se move:
/// <list type="bullet">
/// <item>quem vai atravessar recebe, ao entrar na rede, uma parada no fim da rampa (a "cancela");</item>
/// <item>com uma embarcação atracada, o primeiro da fila ganha uma vaga no convés (uma parada no começo da
/// travessia, logo atrás do último embarcado), se couber;</item>
/// <item>cheia ou na espera máxima, ela sai: todos a bordo recebem, a cada passo, a mesma velocidade (acelera,
/// navega, freia), e se movem como um bloco só até a outra margem;</item>
/// <item>lá desembarcam dirigindo normalmente, a embarcação embarca quem espera para voltar e sai de novo.</item>
/// </list>
/// Cada margem atende uma embarcação de cada vez: quem chega com a margem ocupada espera ao largo.
/// </summary>
public sealed class FerrySystem
{
    private readonly List<Service> _services;
    public const double Gap = 1.0;          // distância entre veículos no convés, m
    public const double Slack = 0.25;       // folga para cada um alcançar a vaga exata
    public const double Acceleration = 0.5; // da embarcação, m/s²
    public const double HoldBack = 30;      // ao largo, esperando a margem: distância antes da atracação, m
    private const double Hold = 1e6;        // parada "até segunda ordem"

    public FerrySystem(IEnumerable<FerrySetup> setups) => _services = [.. setups.Select(s => new Service(s))];

    public int Stowaways => _services.Sum(s => s.Stowaways);
    public List<string> Errors { get; } = [];

    public List<FerryView> Views() => [.. _services.SelectMany(s => s.Views())];

    public List<FerryLog> Logs() => [.. _services.Select(s => s.Log)];

    /// <summary>Chamar depois de cada passo da simulação.</summary>
    public void Step(double time)
    {
#if LIBSUMO
        var dt = Simulation.getDeltaT();
        foreach (var s in _services) s.Begin(time);
        foreach (var id in Simulation.getDepartedIDList())
        {
            var route = Try(() => Vehicle.getRoute(id).ToList()) ?? [];
            foreach (var s in _services) s.Gate(id, route, this);
        }
        foreach (var id in Simulation.getArrivedIDList())
            foreach (var s in _services) s.Forget(id);
        foreach (var s in _services) s.Step(time, dt, this);
#endif
    }

    /// <summary>Fim da rodada: quem ainda espera na rampa.</summary>
    public void Finish()
    {
        foreach (var s in _services) s.Finish();
    }

    internal T? Try<T>(Func<T> f)
    {
        try { return f(); }
        catch (Exception ex) { if (Errors.Count < 50) Errors.Add(ex.Message); return default; }
    }

    internal void Try(Action a)
    {
        try { a(); }
        catch (Exception ex) { if (Errors.Count < 50) Errors.Add(ex.Message); }
    }

    private enum VesselState { Boarding, Closing, Crossing, Unloading }

    private sealed class Gated
    {
        public int Dir;
        public string Ramp = "";
        /// <summary>Quando entrou na fila (parou na rampa, ou parou atrás de quem já está na fila).</summary>
        public double? QueuedAt;
        /// <summary>Parado na cancela: é o primeiro da fila desta rampa (ou desta faixa do pátio).</summary>
        public bool AtGate;
        /// <summary>Pátio por onde passa (null = espera na rampa) e a faixa que recebeu (-1 = ainda sem faixa).</summary>
        public Yard? Yard;
        public int YardLane = -1;
        /// <summary>Comprimento, e o espaço que ocupa parado no pátio (comprimento mais a folga do pátio), m.</summary>
        public double Length, Need;
        /// <summary>Distância mínima normal do veículo (no pátio e no convés ele encosta mais; devolvida ao desembarcar).</summary>
        public double MinGap = double.NaN;
        /// <summary>Já encostou: entrou no pátio com a folga do convés.</summary>
        public bool Packed;
        /// <summary>
        /// A edge logo antes do pátio: a parada no fim dela segura quem ainda não tem faixa. A faixa é dada na chegada
        /// ali, na ordem em que os veículos estão na fila (não na ordem em que saíram: antes pode haver ultrapassagem).
        /// </summary>
        public string? Hold;
    }

    /// <summary>
    /// Pátio de acúmulo: junta uma carga de balsa (o que cabe no convés, se o pátio comportar) e a entrega inteira à
    /// próxima balsa; só volta a receber quando ela parte (só entra na balsa quem estava no pátio). Cada faixa enche em fila até o fim; quem chega vai para a faixa que
    /// está enchendo e, cheia ela, para a próxima vazia. Embarca uma faixa de cada vez, a mais antiga primeiro.
    /// </summary>
    private sealed class Yard
    {
        public string Edge = "";
        public double[] StopPos = [];
        public double[] Capacity = [];
        public List<string>[] Lanes = [];
        public double[] Used = [];
        public bool[] Closed = [];
        /// <summary>Faixas com veículos, da que começou a encher primeiro.</summary>
        public readonly List<int> Order = [];
        public int Fill = -1;
        /// <summary>Entregando a carga à balsa atracada: não recebe ninguém até ela partir.</summary>
        public bool Delivering;
        /// <summary>A carga está completa: o próximo da fila já não caberia no convés, ou o pátio está cheio.</summary>
        public bool Complete;
    }

    /// <summary>Vaga no convés: faixa, distância da frente da faixa até a frente do veículo, e na fila da simulação.</summary>
    private readonly record struct Seat(int Lane, double Offset, double Linear);

    private sealed class Vessel
    {
        public int Index;
        public VesselState State;
        /// <summary>Margem onde está atracada, ou para onde vai (0 = A, 1 = B).</summary>
        public int Side;
        /// <summary>Sentido da travessia em curso: 0 ida, 1 volta, -1 volta vazia pela água (sem link de volta).</summary>
        public int Dir;
        /// <summary>Deslocamento desde a atracação de partida, m, e velocidade, m/s.</summary>
        public double S, V;
        public bool Claimed;
        /// <summary>Começou a simulação no meio do caminho: essa primeira chegada não é uma viagem.</summary>
        public bool Positioning;
        public double Since;
        /// <summary>Faixa do convés que está enchendo, o quanto dela já está ocupado e o das faixas já cheias, m.</summary>
        public int DeckLane;
        public double LaneUsed, UsedBefore;
        public readonly List<string> Aboard = [];
        public readonly Dictionary<string, Seat> Seats = [];
        /// <summary>O último que saiu do pátio para embarcar (o próximo só sai quando ele passar da junção).</summary>
        public string? LastFromYard;
        /// <summary>Já começou a levar a carga do pátio nesta atracação.</summary>
        public bool TookYard;
        public readonly Dictionary<string, double> MinGap = [];
        public FerryTripLog Trip = new();
    }

    private sealed class Service
    {
        private readonly FerrySetup _f;
        private readonly string?[] _edges;
        private readonly string?[] _yardEdges;
        private readonly Yard?[] _yards = [null, null];
        private readonly double[] _edgeLength = [0, 0];
        private readonly Dictionary<string, Gated> _gated = [];
        private readonly HashSet<string> _unloading = [];
        private readonly Dictionary<string, double> _restoreGap = [];
        private readonly Dictionary<string, double> _rampLength = [];
        private readonly int[] _berth = [-1, -1];
        private readonly List<Vessel> _vessels = [];
        private double _nextQueueSample = double.NegativeInfinity;
        private bool _started;
        public int Stowaways;
        public readonly FerryLog Log;

        public Service(FerrySetup f)
        {
            _f = f;
            _edges = [f.Edge, f.ReturnEdge];
            _yardEdges = [f.YardEdge, f.ReturnYardEdge];
            Log = new FerryLog { FerryId = f.FerryId, Name = f.Name, DeckLength = f.DeckLength * f.DeckLanes };
        }

        private bool TwoWay => _edges[1] is not null;
        private double DeckFront => _f.LinearDeck + 1;
        private int Lanes => Math.Max(1, _f.DeckLanes);
        /// <summary>Quanto a embarcação anda numa travessia: do convés até a frente encostar perto do fim.</summary>
        private double Distance(int dir) => _edgeLength[dir < 0 ? 0 : dir] - 2 - DeckFront;

        /// <summary>Velocidade de cruzeiro: acelerando e freando, cumpre a distância no tempo de travessia (t = d/v + v/a).</summary>
        private double Cruise(int dir)
        {
            double d = Distance(dir), a = Acceleration, t = _f.CrossingSeconds;
            var disc = t * t - 4 * d / a;
            return disc <= 0 ? Math.Sqrt(a * d) : Math.Max(0.5, (t - Math.Sqrt(disc)) * a / 2);
        }

#if LIBSUMO
        public void Begin(double time)
        {
            if (!_started) Start(time);
        }

        private void Start(double time)
        {
            _started = true;
            for (var d = 0; d < 2; d++)
            {
                if (_edges[d] is { } e) _edgeLength[d] = Lane.getLength(e + "_0");
                if (_edges[d] is null || _yardEdges[d] is not { } ye) continue;
                var n = Edge.getLaneNumber(ye);
                var y = new Yard
                {
                    Edge = ye,
                    StopPos = new double[n],
                    Capacity = new double[n],
                    Lanes = [.. Enumerable.Range(0, n).Select(_ => new List<string>())],
                    Used = new double[n],
                    Closed = new bool[n],
                };
                for (var k = 0; k < n; k++)
                {
                    var len = Lane.getLength($"{ye}_{k}");
                    y.StopPos[k] = Math.Max(0.1, len - 0.5);
                    y.Capacity[k] = Math.Max(1, len - 1);
                }
                _yards[d] = y;
            }
            // As embarcações começam espalhadas por igual no ciclo (atracada na A → ida → atracada na B → volta):
            // a i-ésima de n está a i/n do ciclo. As que começam navegando vão vazias até a primeira margem.
            var count = Math.Max(1, _f.Vessels);
            for (var i = 0; i < count; i++)
            {
                var v = new Vessel { Index = i, Since = time };
                if (i == 0 || 2 * i == count)
                {
                    v.Side = i == 0 ? 0 : 1;
                    v.State = VesselState.Boarding;
                    _berth[v.Side] = i;
                    v.Trip = new FerryTripLog { Vessel = i, Direction = v.Side, DockedAt = time };
                }
                else
                {
                    var phase = 2.0 * i / count; // 0–1 na ida, 1–2 na volta
                    v.Side = phase < 1 ? 1 : 0;
                    v.Dir = phase < 1 ? 0 : TwoWay ? 1 : -1;
                    v.S = (phase < 1 ? phase : phase - 1) * Math.Max(0, Distance(v.Dir));
                    v.State = VesselState.Crossing;
                    v.Positioning = true;
                    v.Trip = new FerryTripLog { Vessel = i, Direction = -1, DockedAt = time, DepartedAt = time };
                }
                _vessels.Add(v);
            }
        }

        /// <summary>Quem vai atravessar ganha a parada no fim da rampa, ao entrar na rede.</summary>
        public void Gate(string id, List<string> route, FerrySystem sys)
        {
            for (var dir = 0; dir < 2; dir++)
            {
                if (_edges[dir] is not { } edge) continue;
                var i = route.IndexOf(edge);
                if (i < 1) continue;
                if (_yards[dir] is { } y && route.IndexOf(y.Edge) is var yi and >= 0 && yi < i)
                {
                    // Passa pelo pátio: para antes dele e recebe uma faixa ao chegar (veja Admit).
                    var g = new Gated
                    {
                        Dir = dir, Ramp = y.Edge, Yard = y,
                        Length = sys.Try(() => Vehicle.getLength(id)),
                    };
                    // No pátio os veículos param encostados como no convés: o mesmo comprimento leva a mesma carga.
                    g.MinGap = sys.Try(() => Vehicle.getMinGap(id));
                    g.Need = g.Length + Gap + Slack;
                    _gated[id] = g;
                    if (yi == 0)
                    {
                        Assign(y, id, g, sys, force: true); // entrou já no pátio
                        return;
                    }
                    g.Hold = route[yi - 1];
                    sys.Try(() => Vehicle.setStop(id, g.Hold, Math.Max(0.1, RampLength(g.Hold, sys) - 0.5), 0, Hold));
                    return;
                }
                var ramp = route[i - 1];
                sys.Try(() => Vehicle.setStop(id, ramp, Math.Max(0.1, RampLength(ramp, sys) - 0.5), 0, Hold));
                _gated[id] = new Gated { Dir = dir, Ramp = ramp };
                return;
            }
        }

        private double RampLength(string edge, FerrySystem sys)
        {
            if (!_rampLength.TryGetValue(edge, out var len))
                _rampLength[edge] = len = sys.Try(() => Lane.getLength(edge + "_0"));
            return len;
        }

        /// <summary>
        /// Quem chega ao fim da edge antes do pátio (nos últimos metros, ou já parado ali) recebe faixa, do primeiro
        /// da fila para trás; sem lugar no pátio, o primeiro fica parado ali e segura os demais.
        /// </summary>
        private void Admit(Yard y, FerrySystem sys)
        {
            var arriving = new List<(string Id, Gated G, double Left)>();
            foreach (var (id, g) in _gated)
            {
                if (g.Yard != y || g.YardLane >= 0 || g.Hold is null || sys.Try(() => Vehicle.getRoadID(id)) != g.Hold) continue;
                var left = RampLength(g.Hold, sys) - sys.Try(() => Vehicle.getLanePosition(id));
                if (left <= 40) arriving.Add((id, g, left));
            }
            foreach (var (id, g, _) in arriving.OrderBy(a => a.Left))
                if (!Assign(y, id, g, sys)) break;
        }

        /// <summary>
        /// Dá ao veículo uma faixa do pátio: a que está enchendo, se couber; senão a próxima vazia. Devolve false (ele
        /// espera antes do pátio) se não há faixa, se o pátio está entregando a carga à balsa ou se ele já não caberia
        /// no convés junto com quem está no pátio; a não ser com <paramref name="force"/> (entrou já no pátio).
        /// </summary>
        private bool Assign(Yard y, string id, Gated g, FerrySystem sys, bool force = false)
        {
            if (!force && y.Delivering) return false;
            if (!force && !DeckFits(y, g.Length))
            {
                y.Complete = true;
                return false;
            }
            var n = y.Lanes.Length;
            var lane = -1;
            // Cabe na faixa: o que já está nela (cada um com a folga até o de trás) mais o comprimento dele.
            if (y.Fill >= 0 && !y.Closed[y.Fill] && (y.Used[y.Fill] + g.Length <= y.Capacity[y.Fill] || y.Lanes[y.Fill].Count == 0))
                lane = y.Fill;
            else
            {
                if (y.Fill >= 0) y.Closed[y.Fill] = true;
                for (var k = 1; k <= n && lane < 0; k++)
                {
                    var c = ((y.Fill + k) % n + n) % n;
                    if (y.Lanes[c].Count == 0) lane = c;
                }
                if (lane < 0 && force) lane = Enumerable.Range(0, n).MinBy(k => y.Used[k]);
                if (lane < 0)
                {
                    y.Complete = true; // pátio cheio: a carga é o que coube nele
                    return false;
                }
                y.Fill = lane;
                y.Closed[lane] = false;
            }
            if (!y.Order.Contains(lane)) y.Order.Add(lane);
            y.Lanes[lane].Add(id);
            y.Used[lane] += g.Need;
            g.YardLane = lane;
            if (g.Hold is null)
                sys.Try(() => Vehicle.setStop(id, y.Edge, y.StopPos[lane], lane, Hold));
            else if (sys.Try(() => Vehicle.getRoadID(id)) == g.Hold && (sys.Try(() => Vehicle.getStopState(id)) & 1) != 0)
            {
                // Parado na espera antes do pátio: segue para a faixa.
                sys.Try(() => Vehicle.resume(id));
                sys.Try(() => Vehicle.setStop(id, y.Edge, y.StopPos[lane], lane, Hold));
            }
            else
                sys.Try(() => Vehicle.replaceStop(id, 0, y.Edge, y.StopPos[lane], lane, Hold)); // ainda chegando: troca a parada
            return true;
        }

        /// <summary>
        /// Quem está no pátio, mais um veículo deste comprimento, cabe no convés? Arruma na ordem em que embarcariam
        /// (faixa por faixa do pátio), com a mesma regra do embarque: enche uma faixa do convés e passa à seguinte.
        /// </summary>
        private bool DeckFits(Yard y, double length)
        {
            var lengths = y.Order.SelectMany(k => y.Lanes[k]).Select(id => _gated.TryGetValue(id, out var o) ? o.Length : 0).Append(length);
            int lane = 0;
            double used = 0;
            foreach (var len in lengths)
            {
                var offset = used == 0 ? 0 : used + Gap + Slack;
                if (offset + len > _f.DeckLength + 1e-6 && used > 0 && lane + 1 < Lanes)
                {
                    lane++;
                    offset = 0;
                }
                if (offset + len > _f.DeckLength + 1e-6) return false;
                used = offset + len;
            }
            return true;
        }

        /// <summary>
        /// Parado no pátio numa faixa que não é a sua (entrou na errada e a fila da vizinha não o deixou mudar): fica
        /// sendo dessa faixa. Senão ele esperaria ali para sempre, segurando quem está atrás dele.
        /// </summary>
        private static void Settle(Yard y, string id, Gated g, FerrySystem sys)
        {
            if (sys.Try(() => Vehicle.getRoadID(id)) != y.Edge || sys.Try(() => Vehicle.getSpeed(id)) > 0.1) return;
            var lane = sys.Try(() => Vehicle.getLaneIndex(id));
            if (lane == g.YardLane || lane < 0 || lane >= y.Lanes.Length) return;
            Leave(y, id, g);
            if (!y.Order.Contains(lane)) y.Order.Add(lane);
            y.Lanes[lane].Add(id);
            y.Used[lane] += g.Need;
            g.YardLane = lane;
            sys.Try(() => Vehicle.replaceStop(id, 0, y.Edge, y.StopPos[lane], lane, Hold));
        }

        /// <summary>Saiu do pátio (embarcou ou deixou a rede): a faixa vazia volta a receber.</summary>
        private static void Leave(Yard y, string id, Gated g)
        {
            if (g.YardLane < 0) return;
            var k = g.YardLane;
            if (!y.Lanes[k].Remove(id)) return;
            y.Used[k] = Math.Max(0, y.Used[k] - g.Need);
            if (y.Lanes[k].Count > 0) return;
            y.Used[k] = 0;
            y.Closed[k] = false;
            y.Order.Remove(k);

        }

        public void Forget(string id)
        {
            if (_gated.TryGetValue(id, out var g) && g.Yard is { } y) Leave(y, id, g);
            _gated.Remove(id);
            _unloading.Remove(id);
            _restoreGap.Remove(id);
        }

        public void Step(double time, double dt, FerrySystem sys)
        {
            if (!_started) Start(time);
            RestoreGaps(sys);

            // Pátio: quem chega recebe faixa; quem parou na faixa errada fica sendo dela.
            foreach (var y in _yards)
                if (y is not null) Admit(y, sys);
            foreach (var (id, g) in _gated)
            {
                if (g.Yard is not { } yard || g.YardLane < 0) continue;
                if (!g.Packed && sys.Try(() => Vehicle.getRoadID(id)) == yard.Edge)
                {
                    g.Packed = true;
                    sys.Try(() => Vehicle.setMinGap(id, Gap));
                }
                Settle(yard, id, g, sys);
            }

            // Fila: entra quem para na rampa, ou para logo atrás de quem já está na fila (fila que sai da rampa).
            foreach (var (id, g) in _gated)
            {
                if (g.QueuedAt is null && sys.Try(() => Vehicle.getSpeed(id)) < 1)
                {
                    var road = sys.Try(() => Vehicle.getRoadID(id));
                    if (road == g.Ramp || road == g.Hold) g.QueuedAt = time;
                    else if (sys.Try(() => Vehicle.getLeader(id, 30)) is { } leader && !string.IsNullOrEmpty(leader.first) &&
                             _gated.TryGetValue(leader.first, out var lg) && lg.QueuedAt is not null && lg.Dir == g.Dir)
                        g.QueuedAt = time;
                }
                if (!g.AtGate && g.QueuedAt is not null && sys.Try(() => Vehicle.getRoadID(id)) == g.Ramp &&
                    (sys.Try(() => Vehicle.getStopState(id)) & 1) != 0)
                    g.AtGate = true;
            }

            if (time >= _nextQueueSample)
            {
                _nextQueueSample = time + 10;
                for (var d = 0; d < 2; d++)
                {
                    if (_edges[d] is null) continue;
                    if (_yards[d] is { } y)
                        Log.Queue.Add([time, d, Waiting(d), y.Lanes.Sum(l => l.Count(id => sys.Try(() => Vehicle.getRoadID(id)) == y.Edge))]);
                    else Log.Queue.Add([time, d, Waiting(d)]);
                }
            }

            // Ninguém pode estar na travessia sem ter embarcado.
            for (var d = 0; d < 2; d++)
                if (_edges[d] is { } e)
                    foreach (var id in Edge.getLastStepVehicleIDs(e))
                        if (_gated.TryGetValue(id, out var g) && g.Dir == d && !_unloading.Contains(id) && !_vessels.Any(v => v.Aboard.Contains(id)))
                            Stowaways++;

            foreach (var v in _vessels) StepVessel(v, time, dt, sys);
        }

        private int Waiting(int dir) => _gated.Values.Count(g => g.Dir == dir && g.QueuedAt is not null);

        private void StepVessel(Vessel v, double time, double dt, FerrySystem sys)
        {
            switch (v.State)
            {
                case VesselState.Boarding:
                    Board(v, time, sys);
                    break;
                case VesselState.Closing:
                    // Espera os últimos embarcados pararem nas vagas.
                    if (v.Aboard.All(id => (sys.Try(() => Vehicle.getStopState(id)) & 1) != 0)) Depart(v, time, sys);
                    break;
                case VesselState.Crossing:
                    Sail(v, time, dt, sys);
                    break;
                case VesselState.Unloading:
                    if (v.Aboard.All(id => !_unloading.Contains(id) || sys.Try(() => Vehicle.getRoadID(id)) != _edges[v.Dir]))
                    {
                        foreach (var id in v.Aboard) _unloading.Remove(id);
                        v.Aboard.Clear();
                        v.Seats.Clear();
                        Dock(v, time);
                    }
                    break;
            }
        }

        /// <summary>Atracada e livre: passa a embarcar quem espera nesta margem.</summary>
        private void Dock(Vessel v, double time)
        {
            v.State = VesselState.Boarding;
            v.Since = time;
            v.DeckLane = 0;
            v.LaneUsed = v.UsedBefore = 0;
            v.LastFromYard = null;
            v.TookYard = false;
            v.S = v.V = 0;
            v.Trip = new FerryTripLog { Vessel = v.Index, Direction = v.Side, DockedAt = time };
        }

        private void Board(Vessel v, double time, FerrySystem sys)
        {
            var dir = v.Side;
            if (_edges[dir] is not { } edge)
            {
                // Margem B sem link de volta: volta vazia na hora.
                Depart(v, time, sys);
                return;
            }
            // O primeiro da fila desta margem: parado numa cancela (com várias rampas, quem entrou antes na fila); com
            // pátio, o primeiro da faixa que está embarcando.
            var front = _gated.Where(p => p.Value.Dir == dir && p.Value.AtGate && p.Value.Yard is null)
                .Select(p => (string?)p.Key).Append(YardFront(v, dir, sys)).OfType<string>()
                .OrderBy(id => _gated[id].QueuedAt).FirstOrDefault();
            if (front is not null)
            {
                var length = sys.Try(() => Vehicle.getLength(front));
                // Na faixa do convés que está enchendo; se não couber, na próxima (a sobra fica vazia).
                var lane = v.DeckLane;
                var offset = v.LaneUsed == 0 ? 0 : v.LaneUsed + Gap + Slack;
                if (offset + length > _f.DeckLength + 1e-6 && v.LaneUsed > 0 && lane + 1 < Lanes)
                {
                    lane++;
                    offset = 0;
                }
                if (offset + length <= _f.DeckLength + 1e-6)
                {
                    if (lane != v.DeckLane)
                    {
                        v.UsedBefore += v.LaneUsed;
                        v.DeckLane = lane;
                    }
                    var linear = lane * (_f.DeckLength + Gap + Slack) + offset;
                    var g = _gated[front];
                    if (g.Yard is { } y)
                    {
                        // A faixa que embarca não recebe mais ninguém (os próximos vão para outra).
                        if (y.Fill == g.YardLane) y.Closed[g.YardLane] = true;
                        if (!v.TookYard)
                        {
                            // Começa a entregar a carga. Completa, o pátio fecha até a balsa partir (só embarca quem
                            // estava nele); incompleta (pouca demanda, sem fila), segue recebendo e embarcando.
                            v.TookYard = true;
                            y.Delivering = y.Complete;
                        }
                        Leave(y, front, g);
                        v.LastFromYard = front;
                    }
                    var truck = sys.Try(() => Vehicle.getVehicleClass(front)) == "truck";
                    v.MinGap[front] = double.IsNaN(g.MinGap) ? sys.Try(() => Vehicle.getMinGap(front)) : g.MinGap;
                    // No convés os veículos encostam mais que na rua (o SUMO mantém minGap atrás do da frente).
                    sys.Try(() => Vehicle.setMinGap(front, Gap));
                    sys.Try(() => Vehicle.resume(front));
                    sys.Try(() => Vehicle.setStop(front, edge, DeckFront - linear, 0, Hold));
                    Log.Boardings.Add([time, dir, time - (g.QueuedAt ?? time)]);
                    _gated.Remove(front);
                    v.Aboard.Add(front);
                    v.Seats[front] = new Seat(lane, offset, linear);
                    if (truck) v.Trip.Trucks++;
                    v.LaneUsed = offset + length;
                    v.Trip.UsedLength = v.UsedBefore + v.LaneUsed;
                    return;
                }
                v.Trip.Full = true;
            }
            // Entregou a carga completa do pátio: está cheia, parte.
            if (front is null && v.TookYard && _yards[dir] is { Delivering: true, Order.Count: 0 }) v.Trip.Full = true;
            // A espera máxima não interrompe a entrega da carga do pátio.
            var delivering = _yards[dir] is { Delivering: true, Order.Count: > 0 };
            if (v.Trip.Full || time >= v.Since + _f.MaxWaitSeconds && !delivering) v.State = VesselState.Closing;
        }

        /// <summary>
        /// Pátio: o da frente da faixa mais antiga que já tem alguém no pátio, se está parado no fim dela (senão,
        /// espera ele chegar lá: a faixa embarca inteira antes da seguinte).
        /// </summary>
        private string? YardFront(Vessel v, int dir, FerrySystem sys)
        {
            if (_yards[dir] is not { } y) return null;
            // Um de cada vez pela junção do fim do pátio: soltos juntos, um da faixa ao lado podia passar na frente
            // e ficar com a vaga de trás no convés (e o da frente sem conseguir chegar à dele).
            if (v.LastFromYard is { } last && sys.Try(() => Vehicle.getRoadID(last)) is { } road &&
                (road == y.Edge || road.StartsWith(':')))
                return null;
            foreach (var k in y.Order)
            {
                // O da frente é quem está parado no fim da faixa: não necessariamente o primeiro a receber a faixa
                // (antes do pátio um pode ter ultrapassado o outro).
                var inYard = y.Lanes[k].Where(id => sys.Try(() => Vehicle.getRoadID(id)) == y.Edge).ToList();
                if (inYard.Count == 0) continue;
                return inYard.FirstOrDefault(id => _gated.TryGetValue(id, out var g) && g.AtGate);
            }
            return null;
        }

        private void Depart(Vessel v, double time, FerrySystem sys)
        {
            var dir = _edges[v.Side] is null ? -1 : v.Side;
            v.Dir = dir;
            v.Trip.Direction = dir;
            v.Trip.DepartedAt = time;
            v.Trip.Vehicles = v.Aboard.Count;
            v.Trip.LeftBehind = dir >= 0 ? Waiting(dir) : 0;
            foreach (var id in v.Aboard)
            {
                // Sai da vaga e passa a seguir só a velocidade comandada (sem as regras de segurança do SUMO,
                // que fariam cada um manter distância do da frente).
                sys.Try(() => Vehicle.resume(id));
                sys.Try(() => Vehicle.setSpeedMode(id, 0));
                sys.Try(() => Vehicle.setSpeed(id, 0));
            }
            // Pátio desta margem: a balsa partiu, ele volta a receber para a próxima.
            if (_yards[v.Side] is { } y) y.Delivering = y.Complete = false;
            _berth[v.Side] = -1;
            v.Side = 1 - v.Side;
            v.S = v.V = 0;
            v.Claimed = false;
            v.State = VesselState.Crossing;
        }

        private void Sail(Vessel v, double time, double dt, FerrySystem sys)
        {
            var d = Distance(v.Dir);
            // A margem livre fica com quem está mais perto dela (as outras esperam ao largo, atrás, na ordem em que chegam).
            if (!v.Claimed && (_berth[v.Side] < 0 || _berth[v.Side] == v.Index) &&
                !_vessels.Any(o => o != v && o.State == VesselState.Crossing && !o.Claimed && o.Side == v.Side &&
                                   Distance(o.Dir) - o.S < d - v.S))
            {
                _berth[v.Side] = v.Index;
                v.Claimed = true;
            }
            var target = v.Claimed ? d : Math.Max(0, d - HoldBack);
            // Nunca encosta em quem está na frente na travessia (ex.: desembarque anterior ainda saindo).
            if (v.Aboard.Count > 0 && sys.Try(() => Vehicle.getLeader(v.Aboard[0], 300)) is { } leader &&
                !string.IsNullOrEmpty(leader.first) && !v.Aboard.Contains(leader.first))
                target = Math.Min(target, v.S + Math.Max(0, leader.second - 0.5));

            var remaining = Math.Max(0, target - v.S);
            var speed = Math.Min(Math.Min(v.V + Acceleration * dt, Cruise(v.Dir)),
                                 Math.Min(Math.Sqrt(2 * Acceleration * remaining), remaining / dt));
            v.V = Math.Max(0, speed);
            v.S += v.V * dt;
            foreach (var id in v.Aboard) sys.Try(() => Vehicle.setSpeed(id, v.V));
            if (v.Aboard.Count > 1 && v.Dir >= 0)
            {
                var pos = v.Aboard.Select(id => sys.Try(() => Vehicle.getLanePosition(id))).ToList();
                v.Trip.MaxSpread = Math.Max(v.Trip.MaxSpread, pos.Max() - pos.Min());
            }

            if (v.Claimed && d - v.S < 1e-3 && v.V < 1e-6)
            {
                // Atracou: devolve o controle ao SUMO e quem está a bordo desembarca dirigindo normalmente.
                v.Trip.ArrivedAt = time;
                if (!v.Positioning) Log.Trips.Add(v.Trip);
                v.Positioning = false;
                foreach (var id in v.Aboard)
                {
                    sys.Try(() => Vehicle.setSpeedMode(id, 31));
                    sys.Try(() => Vehicle.setSpeed(id, -1));
                    if (v.MinGap.Remove(id, out var gap)) _restoreGap[id] = gap;
                    _unloading.Add(id);
                }
                v.State = VesselState.Unloading;
            }
        }

        /// <summary>
        /// Devolve a distância mínima normal só quando o veículo já se afastou do da frente: devolver na hora,
        /// com todos encostados, o SUMO conta como colisão (distância menor que a mínima).
        /// </summary>
        private void RestoreGaps(FerrySystem sys)
        {
            foreach (var (id, gap) in _restoreGap.ToList())
            {
                try
                {
                    var leader = Vehicle.getLeader(id, 50);
                    if (string.IsNullOrEmpty(leader.first) || leader.second + Vehicle.getMinGap(id) >= gap)
                    {
                        Vehicle.setMinGap(id, gap);
                        _restoreGap.Remove(id);
                    }
                }
                catch (Exception)
                {
                    _restoreGap.Remove(id); // já saiu da rede
                }
            }
        }
#endif

        public void Finish()
        {
            for (var d = 0; d < 2; d++) Log.WaitingAtEnd[d] = Waiting(d);
        }

        /// <summary>
        /// Casco no desenho: do tamanho de uma faixa do convés (as faixas ficam lado a lado), atracado no começo da
        /// travessia e navegando na mesma proporção da embarcação da simulação, cujo convés é uma fila só.
        /// </summary>
        public IEnumerable<FerryView> Views()
        {
            var hull = _f.DeckLength + 1.5;
            foreach (var v in _vessels)
            {
                int dir;
                double progress;
                var reversed = false;
                switch (v.State)
                {
                    case VesselState.Crossing:
                        dir = Math.Max(0, v.Dir);
                        reversed = v.Dir < 0; // volta vazia pela água, sobre a ida ao contrário
                        progress = Distance(v.Dir) > 0 ? v.S / Distance(v.Dir) : 1;
                        break;
                    case VesselState.Unloading when v.Dir >= 0:
                        dir = v.Dir;
                        progress = 1;
                        break;
                    case VesselState.Unloading:
                        dir = 0; // voltou vazia: já está no começo da ida
                        progress = 0;
                        break;
                    default:
                        dir = v.Side;
                        progress = 0;
                        if (_edges[dir] is null)
                        {
                            // Atracada na B sem link de volta: no fim da ida.
                            dir = 0;
                            progress = 1;
                        }
                        break;
                }
                var len = _edgeLength[dir];
                if (len <= 0) continue;
                // O casco anda do começo (0,5 m) até a frente encostar perto do fim da travessia.
                var rear = 0.5 + progress * Math.Max(0, len - 1.5 - hull);
                double a = rear / len, b = (rear + hull) / len;
                if (reversed) (a, b) = (1 - b, 1 - a);
                yield return new FerryView(_f.FerryId, v.Index, dir == 1 ? _f.ReturnLinkId!.Value : _f.LinkId,
                    Math.Clamp(a, 0, 1), Math.Clamp(b, 0, 1), v.Aboard.Count, v.State == VesselState.Crossing && v.V > 0.01)
                {
                    Lanes = Lanes,
                    // Com uma faixa, a fila da simulação já é o desenho.
                    Riders = reversed || Lanes == 1 || v.Seats.Count == 0 ? [] : Riders(v, dir, rear, progress),
                };
            }
        }

        /// <summary>
        /// Onde desenhar quem está a bordo. A vaga de cada um na fila da simulação vira a vaga na faixa dele no casco;
        /// quem está entrando (ou saindo) desliza da popa até a vaga (ou da vaga até a proa), saindo do centro e
        /// indo para a faixa nos últimos metros.
        /// </summary>
        private List<FerryRider> Riders(Vessel v, int dir, double rear, double progress)
        {
            var list = new List<FerryRider>();
#if LIBSUMO
            var edge = _edges[dir];
            var e = _edgeLength[dir];
            var deck = _f.DeckLength;
            var deckFront = rear + deck + 0.5;           // frente das faixas no casco
            var shift = progress * Distance(dir);        // quanto a fila da simulação já andou
            foreach (var (id, seat) in v.Seats)
            {
                try
                {
                    if (Vehicle.getRoadID(id) != edge) continue;
                    var x = Vehicle.getLanePosition(id);
                    var length = Vehicle.getLength(id);
                    var target = DeckFront - seat.Linear + shift;
                    double d, side;
                    if (x <= target)
                    {
                        d = seat.Offset + (deck + 0.5 - seat.Offset) * Math.Clamp((target - x) / Math.Max(target, 0.1), 0, 1);
                        side = Math.Clamp(x / Math.Max(0.5, Math.Min(8, target)), 0, 1);
                    }
                    else
                    {
                        d = seat.Offset * Math.Clamp((e - x) / Math.Max(0.1, e - target), 0, 1);
                        side = Math.Clamp((e - x) / Math.Max(0.5, Math.Min(8, e - target)), 0, 1);
                    }
                    var lateral = (seat.Lane - (Lanes - 1) / 2.0) * Trafego.Core.Model.Ferry.DeckLaneWidth * side;
                    list.Add(new FerryRider(id, Math.Clamp((deckFront - d - length / 2) / e, 0, 1), lateral));
                }
                catch (Exception)
                {
                    // saiu da rede
                }
            }
#endif
            return list;
        }
    }
}
