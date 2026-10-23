using System.Globalization;
using System.Xml.Linq;
using Trafego.Core.Geometry;
using Trafego.Core.Model;

namespace Trafego.Sumo;

/// <summary>Trecho de um link que virou uma edge do SUMO.</summary>
/// <param name="EdgeId">Id da edge no SUMO.</param>
/// <param name="S0">Início do trecho ao longo do link, m.</param>
/// <param name="S1">Fim do trecho ao longo do link, m.</param>
public sealed record LinkPiece(string EdgeId, double S0, double S1);

/// <summary>
/// Rede no formato "plain XML" do SUMO (nós, edges e conexões), pronta para o netconvert,
/// e o mapa de volta de cada edge para o objeto do editor.
/// </summary>
/// <remarks>
/// Tradução do modelo link/conector do Vissim para o modelo nó/edge do SUMO:
/// <list type="bullet">
/// <item>Cada link é cortado onde algum conector sai ou chega (<see cref="Topology"/>); cada pedaço
/// vira uma edge e cada corte vira um nó. Pedaços seguidos do mesmo link se ligam faixa a faixa.</item>
/// <item>Conectores que se cruzam formam um nó só (<see cref="ConflictAnalysis"/>): dentro dele cada
/// conector vira uma conexão do SUMO com a geometria desenhada, e o SUMO calcula quem cruza com quem.</item>
/// <item>Os demais conectores viram edges próprias, entre o nó de saída e o de chegada.</item>
/// <item>As preferências definidas pelo usuário viram "prohibitions" (quem espera por quem).</item>
/// </list>
/// </remarks>
public sealed class SumoExport
{
    public const string LinkEdgePrefix = "L";
    public const string ConnectorEdgePrefix = "C";

    public XDocument Nodes { get; } = new(new XElement("nodes"));
    public XDocument Edges { get; } = new(new XElement("edges"));
    public XDocument Connections { get; } = new(new XElement("connections"));

    public ConflictAnalysis Analysis { get; }
    public Topology Topology => Analysis.Topology;
    public IReadOnlyDictionary<int, IReadOnlyList<LinkPiece>> LinkPieces => _pieces;
    /// <summary>Conectores que viraram edges (os internos a um nó não estão aqui).</summary>
    public IReadOnlyDictionary<int, string> ConnectorEdges => _connectorEdges;
    public List<string> Warnings { get; } = [];

    /// <summary>Programas semafóricos e quais conexões cada grupo controla (entrada -i do netconvert).</summary>
    public XDocument TrafficLights { get; } = new(new XElement("tlLogics"));
    public bool HasTrafficLights => _tlOfNode.Count > 0;
    /// <summary>Controlador do editor → id do semáforo (tlLogic) no SUMO.</summary>
    public IReadOnlyDictionary<int, string> TrafficLightIds => _tlIds;

    private readonly Dictionary<int, IReadOnlyList<LinkPiece>> _pieces = [];
    private readonly Dictionary<int, string> _connectorEdges = [];
    private readonly HashSet<string> _junctionNodes = [];
    private readonly Dictionary<string, string> _tlOfNode = [];
    private readonly Dictionary<int, string> _tlIds = [];
    private readonly HashSet<int> _heads = [];
    private readonly Dictionary<string, string> _edgeEnd = [];
    private readonly List<XElement> _tlConnections = [];
    private readonly Network _net;

    private SumoExport(Network net)
    {
        _net = net;
        Analysis = ConflictAnalysis.Of(net);
    }

    public static SumoExport From(Network net)
    {
        var e = new SumoExport(net);
        e.Build();
        return e;
    }

    /// <summary>Converte do mundo do editor (Y para baixo) para o SUMO (Y para cima).</summary>
    public static Vec2 ToSumo(Vec2 p) => new(p.X, -p.Y);
    public static Vec2 FromSumo(double x, double y) => new(x, -y);

    internal static string F(double v)
    {
        var r = Math.Round(v, 3);
        return (r == 0 ? 0 : r).ToString("0.###", CultureInfo.InvariantCulture); // sem "-0"
    }
    private static string Shape(IEnumerable<Vec2> pts) =>
        string.Join(" ", pts.Select(ToSumo).Select(p => $"{F(p.X)},{F(p.Y)}"));

    public string NodeId(NodeKey n) =>
        Analysis.JunctionOf(n) is { } k ? $"J{k}" : $"n{n.LinkId}_{n.Cut}";

    public int CutIndex(int linkId, double s) => Topology.CutIndex(linkId, s);

    /// <summary>Id do semáforo (tlLogic) no SUMO para um controlador do editor.</summary>
    public static string TrafficLightId(int controllerId) => $"TL{controllerId}";

    private void Build()
    {
        FindSignalizedNodes();
        foreach (var j in Analysis.Junctions) AddJunctionNode(j);
        foreach (var link in _net.Links.Where(l => Topology.Has(l.Id))) BuildLink(link);
        foreach (var c in _net.Connectors) BuildConnector(c);
        BuildProhibitions();
        BuildTrafficLights();
    }

    private void AddJunctionNode(Junction j)
    {
        var id = $"J{j.Key}";
        _junctionNodes.Add(id);
        var type = _net.ControlOf(j.Key) switch
        {
            JunctionControl.RightBeforeLeft => "right_before_left",
            JunctionControl.AllWayStop => "allway_stop",
            _ => "priority",
        };
        AddNode(id, j.Center, type);
    }

    /// <summary>Nó do SUMO; se algum conector com semáforo sai dele, vira traffic_light do controlador.</summary>
    private void AddNode(string id, Vec2 world, string type)
    {
        var p = ToSumo(world);
        var node = new XElement("node", new XAttribute("id", id), new XAttribute("x", F(p.X)), new XAttribute("y", F(p.Y)));
        if (_tlOfNode.TryGetValue(id, out var tl))
            node.Add(new XAttribute("type", "traffic_light"), new XAttribute("tl", tl));
        else
            node.Add(new XAttribute("type", type));
        Nodes.Root!.Add(node);
    }

    // ------------------------------------------------------------------ semáforos

    /// <summary>
    /// O semáforo de um conector fica no nó onde ele começa: o nó do cruzamento, se o conector é interno,
    /// ou o ponto de saída dele no link de origem. Um nó só pode ter um controlador.
    /// </summary>
    private void FindSignalizedNodes()
    {
        foreach (var c in _net.Connectors)
        {
            if (c.Signal is not { } s || !Topology.Covers(c)) continue;
            var sc = _net.FindController(s.ControllerId);
            if (sc?.FindGroup(s.GroupId) is null || sc.Plans.Count == 0) continue;
            var fromNode = Topology.FromNode(c);
            if (Topology.IsLinkStart(fromNode))
            {
                Warnings.Add($"{c.Name}: semáforo ignorado (o conector sai do início do link, não há onde parar).");
                continue;
            }
            var nodeId = Analysis.JunctionOfConnector(c.Id) is { } k ? $"J{k}" : NodeId(fromNode);
            var tl = TrafficLightId(sc.Id);
            if (_tlOfNode.TryGetValue(nodeId, out var other) && other != tl)
            {
                Warnings.Add($"{c.Name}: o nó já é controlado por outro semáforo; a cabeça semafórica foi ignorada.");
                continue;
            }
            _tlOfNode[nodeId] = tl;
            _tlIds[sc.Id] = tl;
            _heads.Add(c.Id);
        }
    }

    /// <summary>Um tlLogic por plano; o estado tem um caractere por grupo (linkIndex = posição do grupo).</summary>
    private void BuildTrafficLights()
    {
        var root = TrafficLights.Root!;
        foreach (var (controllerId, tl) in _tlIds)
        {
            var sc = _net.FindController(controllerId)!;
            foreach (var plan in sc.Plans.Where(p => p.Cycle > 0))
            {
                var logic = new XElement("tlLogic", new XAttribute("id", tl), new XAttribute("programID", $"P{plan.Id}"),
                    new XAttribute("type", "static"), new XAttribute("offset", F(plan.Offset)));
                foreach (var (duration, state) in plan.Phases(sc.Groups))
                    logic.Add(new XElement("phase", new XAttribute("duration", F(duration)), new XAttribute("state", state)));
                root.Add(logic);
            }
        }
        foreach (var c in _tlConnections) root.Add(c);
    }

    /// <summary>
    /// Arquivo adicional com a troca de planos por horário (WAUT). O plano inicial é o que vale no horário
    /// de início; as trocas seguem a tabela de horários de cada controlador, dia após dia.
    /// </summary>
    public XDocument Additional(double begin, double end)
    {
        var root = new XElement("additional");
        foreach (var (controllerId, tl) in _tlIds)
        {
            var sc = _net.FindController(controllerId)!;
            if (sc.Plans.Count(p => p.Cycle > 0) < 2 || sc.PlanAt(begin) is not { } start) continue;
            var waut = new XElement("WAUT", new XAttribute("id", $"w{tl}"), new XAttribute("refTime", "0"),
                new XAttribute("startProg", $"P{start.Id}"));
            var current = start.Id;
            foreach (var (time, plan) in sc.SwitchesBetween(begin, end))
            {
                if (plan.Id == current || plan.Cycle <= 0) continue;
                waut.Add(new XElement("wautSwitch", new XAttribute("time", F(time)), new XAttribute("to", $"P{plan.Id}")));
                current = plan.Id;
            }
            root.Add(waut);
            root.Add(new XElement("wautJunction", new XAttribute("wautID", $"w{tl}"), new XAttribute("junctionID", tl)));
        }
        return new XDocument(root);
    }

    private void BuildLink(Link link)
    {
        var axis = link.Axis();
        var cuts = Topology.Cuts(link.Id);

        for (var i = 0; i < cuts.Count; i++)
        {
            var key = new NodeKey(link.Id, i);
            if (Analysis.JunctionOf(key) is not null) continue;
            AddNode(NodeId(key), axis.At(cuts[i]).Point, "priority");
        }

        var pieces = new List<LinkPiece>();
        for (var i = 0; i < cuts.Count - 1; i++)
        {
            var id = $"{LinkEdgePrefix}{link.Id}_{i}";
            pieces.Add(new LinkPiece(id, cuts[i], cuts[i + 1]));
            var (from, to) = (NodeId(new NodeKey(link.Id, i)), NodeId(new NodeKey(link.Id, i + 1)));
            if (from == to)
            {
                Warnings.Add($"{link.Name}: trecho inteiro dentro do nó {from}; ficou de fora.");
                continue;
            }
            var edge = new XElement("edge",
                new XAttribute("id", id),
                new XAttribute("from", from),
                new XAttribute("to", to),
                new XAttribute("numLanes", link.Lanes.Count),
                new XAttribute("speed", F(link.SpeedKmh / 3.6)),
                new XAttribute("priority", 2),
                new XAttribute("spreadType", "center"),
                new XAttribute("shape", Shape(axis.Slice(cuts[i], cuts[i + 1]))));
            for (var l = 0; l < link.Lanes.Count; l++)
                edge.Add(new XElement("lane", new XAttribute("index", l), new XAttribute("width", F(link.Lanes[l].Width))));
            Edges.Root!.Add(edge);
            _edgeEnd[id] = to;

            if (i > 0)
            {
                // Continuação do mesmo link: faixa i segue na faixa i.
                for (var l = 0; l < link.Lanes.Count; l++)
                    AddConnection(pieces[i - 1].EdgeId, l, id, l);
            }
        }
        _pieces[link.Id] = pieces;
    }

    /// <summary>Edge do trecho que termina no corte (null se o corte é o início do link).</summary>
    private string? PieceEndingAt(NodeKey n) => n.Cut > 0 ? _pieces[n.LinkId][n.Cut - 1].EdgeId : null;

    /// <summary>Edge do trecho que começa no corte (null se o corte é o fim do link).</summary>
    private string? PieceStartingAt(NodeKey n) => n.Cut < _pieces[n.LinkId].Count ? _pieces[n.LinkId][n.Cut].EdgeId : null;

    private void BuildConnector(Connector c)
    {
        var from = _net.FindLink(c.FromLinkId);
        var to = _net.FindLink(c.ToLinkId);
        var geometry = _net.GeometryOf(c);
        if (from is null || to is null || geometry is null || !Topology.Covers(c))
        {
            Warnings.Add($"{c.Name}: ignorado (links ou faixas inválidos).");
            return;
        }

        var fromNode = Topology.FromNode(c);
        var toNode = Topology.ToNode(c);
        var inEdge = PieceEndingAt(fromNode);
        var outEdge = PieceStartingAt(toNode);
        if (inEdge is null) Warnings.Add($"{c.Name}: sai do início de {from.Name}; nenhum veículo chega até ele por esse link.");
        if (outEdge is null) Warnings.Add($"{c.Name}: chega no fim de {to.Name}; os veículos saem da rede ali.");

        if (Analysis.JunctionOfConnector(c.Id) is not null)
        {
            // Interno a um nó: vira conexão faixa a faixa, com a forma desenhada.
            if (inEdge is null || outEdge is null) return;
            for (var l = 0; l < c.LaneCount; l++)
                AddConnection(inEdge, c.FromLane + l, outEdge, c.ToLane + l,
                    Shape(Polyline.Offset(geometry.Axis.Points, geometry.LaneOffsets[l])), HeadOf(c));
            return;
        }

        var id = $"{ConnectorEdgePrefix}{c.Id}";
        _connectorEdges[c.Id] = id;
        var edge = new XElement("edge",
            new XAttribute("id", id),
            new XAttribute("from", NodeId(fromNode)),
            new XAttribute("to", NodeId(toNode)),
            new XAttribute("numLanes", c.LaneCount),
            new XAttribute("speed", F(Math.Min(from.SpeedKmh, to.SpeedKmh) / 3.6)),
            new XAttribute("priority", 1),
            new XAttribute("spreadType", "center"),
            new XAttribute("shape", Shape(geometry.Axis.Points)));
        for (var l = 0; l < c.LaneCount; l++)
            edge.Add(new XElement("lane", new XAttribute("index", l), new XAttribute("width", F(geometry.LaneWidths[l]))));
        Edges.Root!.Add(edge);
        _edgeEnd[id] = NodeId(toNode);

        if (inEdge is not null)
            for (var l = 0; l < c.LaneCount; l++)
                AddConnection(inEdge, c.FromLane + l, id, l, head: HeadOf(c));
        if (outEdge is not null)
            for (var l = 0; l < c.LaneCount; l++)
                AddConnection(id, l, outEdge, c.ToLane + l);
    }

    /// <summary>(semáforo, índice do grupo) se o conector tem uma cabeça semafórica válida.</summary>
    private (string Tl, int Index)? HeadOf(Connector c)
    {
        if (!_heads.Contains(c.Id) || c.Signal is not { } s || _net.FindController(s.ControllerId) is not { } sc) return null;
        return (_tlIds[sc.Id], sc.Groups.FindIndex(g => g.Id == s.GroupId));
    }

    /// <summary>
    /// Conexão faixa a faixa. Num nó com semáforo, quem não tem cabeça semafórica fica "uncontrolled":
    /// passa pelas regras de preferência, sem cor.
    /// </summary>
    private void AddConnection(string from, int fromLane, string to, int toLane, string? shape = null, (string Tl, int Index)? head = null)
    {
        var e = new XElement("connection",
            new XAttribute("from", from), new XAttribute("to", to),
            new XAttribute("fromLane", fromLane), new XAttribute("toLane", toLane));
        if (shape is not null) e.Add(new XAttribute("shape", shape));
        if (head is { } h)
            _tlConnections.Add(new XElement("connection",
                new XAttribute("from", from), new XAttribute("to", to),
                new XAttribute("fromLane", fromLane), new XAttribute("toLane", toLane),
                new XAttribute("tl", h.Tl), new XAttribute("linkIndex", h.Index)));
        else if (_edgeEnd.TryGetValue(from, out var node) && _tlOfNode.ContainsKey(node))
            e.Add(new XAttribute("uncontrolled", "true"));
        Connections.Root!.Add(e);
    }

    // ------------------------------------------------------------------ preferências

    /// <summary>
    /// A ligação (edge de entrada → edge de saída) que o movimento usa no nó do conflito.
    /// Para um conector interno, atravessa o nó; para um conector-edge, é a chegada dele.
    /// </summary>
    private (string From, string To)? MovementAt(Conflict conflict, int movement)
    {
        if (movement == ConflictRule.MainLine)
        {
            var owner = _net.FindConnector(conflict.A);
            if (owner is null) return null;
            var node = Topology.ToNode(owner);
            return PieceEndingAt(node) is { } a && PieceStartingAt(node) is { } b ? (a, b) : null;
        }
        if (_net.FindConnector(movement) is not { } c || !Topology.Covers(c)) return null;
        var outEdge = PieceStartingAt(Topology.ToNode(c));
        if (outEdge is null) return null;
        if (_connectorEdges.TryGetValue(c.Id, out var edge)) return (edge, outEdge);
        return PieceEndingAt(Topology.FromNode(c)) is { } inEdge ? (inEdge, outEdge) : null;
    }

    private void BuildProhibitions()
    {
        foreach (var conflict in Analysis.Conflicts.Where(x => x.Kind != ConflictKind.LinkCrossing))
        {
            var priority = _net.PriorityOf(conflict.A, conflict.B);
            if (priority is not (ConflictPriority.A or ConflictPriority.B)) continue;
            var (winner, loser) = priority == ConflictPriority.A ? (conflict.A, conflict.B) : (conflict.B, conflict.A);
            if (MovementAt(conflict, winner) is not { } w || MovementAt(conflict, loser) is not { } l)
            {
                Warnings.Add($"Preferência entre {Validation.NameOf(_net, conflict.A)} e {Validation.NameOf(_net, conflict.B)} " +
                             "não pôde ser aplicada (um dos movimentos não tem por onde chegar).");
                continue;
            }
            if (w == l) continue;
            Connections.Root!.Add(new XElement("prohibition",
                new XAttribute("prohibitor", $"{w.From}->{w.To}"),
                new XAttribute("prohibited", $"{l.From}->{l.To}")));
        }
    }

    // ------------------------------------------------------------------ medições

    public const string CountOutput = "medicoes_contagem.xml";
    public const string QueueOutput = "medicoes_fila.xml";
    public const string TravelTimeOutput = "medicoes_tempo.xml";
    public const string EdgeDataOutput = "links.xml";

    /// <summary>Id do detector no SUMO: "m{medição}" ou "m{medição}_l{faixa}" para os que são por faixa.</summary>
    public static string DetectorId(int measurementId, int? lane = null) =>
        lane is { } l ? $"m{measurementId}_l{l}" : $"m{measurementId}";

    /// <summary>
    /// Trecho do link na posição s e a posição relativa (0 a 1) dentro dele. Num corte, preferEnd escolhe
    /// o trecho que termina ali (para a fila, que olha para trás) em vez do que começa.
    /// </summary>
    public (LinkPiece Piece, double Fraction)? PieceAt(int linkId, double s, bool preferEnd)
    {
        if (!_pieces.TryGetValue(linkId, out var pieces) || pieces.Count == 0) return null;
        var i = pieces.ToList().FindIndex(p => preferEnd ? s > p.S0 + 1e-6 && s <= p.S1 + 1e-6 : s >= p.S0 - 1e-6 && s < p.S1 - 1e-6);
        if (i < 0) i = s <= pieces[0].S0 ? 0 : pieces.Count - 1;
        var piece = pieces[i];
        var len = piece.S1 - piece.S0;
        return (piece, len > 0 ? Math.Clamp((s - piece.S0) / len, 0, 1) : 0);
    }

    /// <summary>
    /// Detectores das medições e a coleta por link (edgeData). As posições são convertidas para o
    /// comprimento real das faixas no SUMO (laneLengths, lido da rede gerada), que o netconvert encurta
    /// nas junções.
    /// </summary>
    public XDocument Detectors(IReadOnlyDictionary<string, double> laneLengths, double begin, double end, double period)
    {
        var root = new XElement("additional");
        var p = F(period);

        double LanePos(string lane, double fraction, double margin = 0.5)
        {
            var len = laneLengths.GetValueOrDefault(lane, 0);
            return Math.Clamp(fraction * len, Math.Min(margin, len / 2), Math.Max(len - margin, len / 2));
        }

        foreach (var m in _net.Measurements)
        {
            if (_net.FindLink(m.LinkId) is not { } link) continue;
            switch (m.Kind)
            {
                case MeasurementKind.Count when PieceAt(link.Id, m.S, preferEnd: false) is { } at:
                    for (var l = 0; l < link.Lanes.Count; l++)
                    {
                        var lane = $"{at.Piece.EdgeId}_{l}";
                        root.Add(new XElement("inductionLoop", new XAttribute("id", DetectorId(m.Id, l)),
                            new XAttribute("lane", lane), new XAttribute("pos", F(LanePos(lane, at.Fraction))),
                            new XAttribute("period", p), new XAttribute("file", CountOutput)));
                    }
                    break;

                case MeasurementKind.Queue when PieceAt(link.Id, m.S, preferEnd: true) is { } at:
                    // A fila é medida dentro do trecho do link que termina no ponto (até QueueLength m para trás).
                    var pieceLen = at.Piece.S1 - at.Piece.S0;
                    for (var l = 0; l < link.Lanes.Count; l++)
                    {
                        var lane = $"{at.Piece.EdgeId}_{l}";
                        var endPos = LanePos(lane, at.Fraction, 0.1);
                        var scale = pieceLen > 0 ? laneLengths.GetValueOrDefault(lane, pieceLen) / pieceLen : 1;
                        var startPos = Math.Max(0, endPos - m.QueueLength * scale);
                        if (endPos - startPos < 1) continue;
                        root.Add(new XElement("laneAreaDetector", new XAttribute("id", DetectorId(m.Id, l)),
                            new XAttribute("lane", lane), new XAttribute("pos", F(startPos)), new XAttribute("endPos", F(endPos)),
                            new XAttribute("period", p), new XAttribute("file", QueueOutput)));
                    }
                    break;

                case MeasurementKind.TravelTime when _net.FindLink(m.ToLinkId) is { } to &&
                                                     PieceAt(link.Id, m.S, false) is { } a && PieceAt(to.Id, m.ToS, true) is { } b:
                    var e3 = new XElement("entryExitDetector", new XAttribute("id", DetectorId(m.Id)),
                        new XAttribute("period", p), new XAttribute("file", TravelTimeOutput));
                    for (var l = 0; l < link.Lanes.Count; l++)
                    {
                        var lane = $"{a.Piece.EdgeId}_{l}";
                        e3.Add(new XElement("detEntry", new XAttribute("lane", lane), new XAttribute("pos", F(LanePos(lane, a.Fraction)))));
                    }
                    for (var l = 0; l < to.Lanes.Count; l++)
                    {
                        var lane = $"{b.Piece.EdgeId}_{l}";
                        e3.Add(new XElement("detExit", new XAttribute("lane", lane), new XAttribute("pos", F(LanePos(lane, b.Fraction)))));
                    }
                    root.Add(e3);
                    break;

                default:
                    Warnings.Add($"{m.Name}: não foi possível posicionar a medição.");
                    break;
            }
        }

        root.Add(new XElement("edgeData", new XAttribute("id", "links"), new XAttribute("file", EdgeDataOutput),
            new XAttribute("begin", F(begin)), new XAttribute("end", F(end)), new XAttribute("period", p)));
        return new XDocument(root);
    }

    // ------------------------------------------------------------------ caminhos e rotas

    /// <summary>Um caminho em edges do SUMO, com a sua probabilidade.</summary>
    public sealed record RoutePath(IReadOnlyList<string> Edges, double Probability);

    public const int MaxPathsPerInput = RouteFinder.MaxPaths;

    /// <summary>Edges do SUMO de um caminho do editor (conectores internos a um nó não têm edge).</summary>
    public List<string> EdgesOf(NetPath path)
    {
        var edges = new List<string>();
        foreach (var s in path.Steps)
        {
            switch (s)
            {
                case LinkStep ls when _pieces.TryGetValue(ls.LinkId, out var pieces) && ls.Piece < pieces.Count:
                    edges.Add(pieces[ls.Piece].EdgeId);
                    break;
                case ConnectorStep cs when _connectorEdges.TryGetValue(cs.ConnectorId, out var edge):
                    edges.Add(edge);
                    break;
            }
        }
        return edges;
    }

    /// <summary>Caminhos a partir do início do link, com chance igual em cada escolha.</summary>
    public List<RoutePath> PathsFrom(int linkId) =>
        [.. RouteFinder.PathsFrom(Analysis, linkId).Select(p => new RoutePath(EdgesOf(p), p.Probability))];

    /// <summary>
    /// Arquivo de rotas, com um fluxo por janela de tempo (intervalos da demanda):
    /// <list type="bullet">
    /// <item>entrada sem matriz O/D: volume da entrada no intervalo, com chance igual em cada escolha;</item>
    /// <item>entrada com matriz: um fluxo por destino, pelas rotas escolhidas (ou pela mais rápida).</item>
    /// </list>
    /// Chegadas de Poisson; os fluxos ficam em ordem de início, como o SUMO exige.
    /// </summary>
    public XDocument Routes(double begin, double end)
    {
        var root = new XElement("routes",
            new XElement("vType", new XAttribute("id", "car"), new XAttribute("vClass", "passenger"),
                new XAttribute("length", "4.5"), new XAttribute("width", "1.8"), new XAttribute("minGap", "2"),
                new XAttribute("accel", "2.6"), new XAttribute("decel", "4.5"), new XAttribute("speedDev", "0.1")),
            new XElement("vType", new XAttribute("id", "truck"), new XAttribute("vClass", "truck"),
                new XAttribute("length", "12"), new XAttribute("width", "2.5"), new XAttribute("minGap", "3"),
                new XAttribute("accel", "1.0"), new XAttribute("decel", "4.0"), new XAttribute("maxSpeed", "25"),
                new XAttribute("speedDev", "0.05")));

        var demand = _net.Demand;
        var windows = demand.Windows(begin, end);
        var flows = new List<(double Begin, XElement Flow)>();

        void AddFlows(VehicleInput input, string id, string route, Func<double, double> vphAt)
        {
            for (var w = 0; w < windows.Count; w++)
            {
                var (b, e, slice) = windows[w];
                foreach (var (type, share) in new[] { ("car", 1 - input.TruckShare), ("truck", input.TruckShare) })
                {
                    var vph = vphAt(slice) * share;
                    if (vph <= 0) continue;
                    flows.Add((b, new XElement("flow",
                        new XAttribute("id", $"{id}_w{w}_{type}"),
                        new XAttribute("type", type),
                        new XAttribute("route", route),
                        new XAttribute("begin", F(b)),
                        new XAttribute("end", F(e)),
                        new XAttribute("period", $"exp({(vph / 3600).ToString("R", CultureInfo.InvariantCulture)})"),
                        new XAttribute("departLane", "best"),
                        new XAttribute("departSpeed", "max"))));
                }
            }
        }

        XElement Distribution(string id, IEnumerable<(List<string> Edges, double Weight)> routes)
        {
            var dist = new XElement("routeDistribution", new XAttribute("id", id));
            var i = 0;
            foreach (var (edges, weight) in routes)
                dist.Add(new XElement("route", new XAttribute("id", $"{id}_{i++}"),
                    new XAttribute("edges", string.Join(" ", edges)),
                    new XAttribute("probability", weight.ToString("R", CultureInfo.InvariantCulture))));
            return dist;
        }

        foreach (var input in _net.Inputs)
        {
            if (!demand.UsesMatrix(input.Id))
            {
                var paths = RouteFinder.PathsFrom(Analysis, input.LinkId);
                if (paths.Count == 0)
                {
                    Warnings.Add($"{input.Name}: o link não existe ou não foi exportado.");
                    continue;
                }
                if (paths.Count >= MaxPathsPerInput)
                    Warnings.Add($"{input.Name}: caminhos demais; só os {MaxPathsPerInput} primeiros foram usados.");
                root.Add(Distribution($"rd{input.Id}", paths.Select(p => (EdgesOf(p), p.Probability))));
                AddFlows(input, $"in{input.Id}", $"rd{input.Id}", input.VolumeAt);
                continue;
            }

            foreach (var dest in demand.Pairs().Where(p => p.Origin == input.Id).Select(p => p.Destination))
            {
                var routes = ChosenRoutes(input, dest);
                if (routes.Count == 0)
                {
                    Warnings.Add($"Matriz O/D: não há caminho de {input.Name} até {Validation.NameOf(_net, dest)}; volume ignorado.");
                    continue;
                }
                var distId = $"od{input.Id}_{dest}";
                root.Add(Distribution(distId, routes.Select(r => (EdgesOf(r.Path), r.Weight))));
                AddFlows(input, $"od{input.Id}_{dest}", distId, slice => demand.OdVolume(input.Id, dest, slice));
            }
        }

        foreach (var (_, flow) in flows.OrderBy(f => f.Begin)) root.Add(flow);
        return new XDocument(root);
    }

    /// <summary>
    /// Rotas de um par O/D com o peso de cada uma: as escolhidas pelo usuário (que ainda existem
    /// e têm peso) ou, sem escolha, só a mais rápida em fluxo livre.
    /// </summary>
    public List<(NetPath Path, double Weight)> ChosenRoutes(VehicleInput input, int destinationLinkId)
    {
        var alternatives = RouteFinder.PathsFrom(Analysis, input.LinkId)
            .Where(p => p.EndLinkId == destinationLinkId)
            .OrderBy(p => p.FreeFlowTime)
            .ToList();
        if (alternatives.Count == 0) return [];

        var chosen = new List<(NetPath, double)>();
        foreach (var choice in _net.Demand.ChoicesOf(input.Id, destinationLinkId).Where(c => c.Share > 0))
            if (alternatives.FirstOrDefault(p => p.ConnectorIds.SequenceEqual(choice.ConnectorIds)) is { } path)
                chosen.Add((path, choice.Share));
        return chosen.Count > 0 ? chosen : [(alternatives[0], 1)];
    }
}
