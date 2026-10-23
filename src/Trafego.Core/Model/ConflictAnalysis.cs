using Trafego.Core.Geometry;

namespace Trafego.Core.Model;

public enum ConflictKind
{
    /// <summary>Dois conectores se cruzam: viram um nó (interseção) no SUMO.</summary>
    Crossing,
    /// <summary>Dois movimentos chegam às mesmas faixas (conector entrando no fluxo principal ou em outro conector).</summary>
    Merge,
    /// <summary>Um link cruza outro link ou um conector no meio do caminho: ainda não vira nó no SUMO.</summary>
    LinkCrossing,
}

/// <summary>
/// Conflito entre dois movimentos. A é um conector (ou um link, em <see cref="ConflictKind.LinkCrossing"/>);
/// B é outro conector, um link, ou <see cref="ConflictRule.MainLine"/> = seguir no link de destino de A.
/// </summary>
/// <param name="SA">Posição do ponto de conflito ao longo do movimento A, m.</param>
/// <param name="SB">Posição ao longo do movimento B (no link de destino, se B é o fluxo principal), m.</param>
/// <param name="JunctionKey">Nó a que o conflito pertence, se houver.</param>
public sealed record Conflict(ConflictKind Kind, int A, int B, Vec2 Point, double SA, double SB, int? JunctionKey)
{
    /// <summary>Para B = fluxo principal: o link de destino de A.</summary>
    public int? MainLinkId { get; init; }
}

/// <summary>Nó (interseção) formado por conectores que se cruzam.</summary>
/// <param name="Key">Menor id de conector dentro do nó; identifica o nó nas configurações.</param>
/// <param name="ConnectorIds">Conectores internos ao nó (viram conexões no SUMO, não edges).</param>
/// <param name="Nodes">Cortes de link agrupados no nó.</param>
/// <param name="Outline">Contorno (envoltória convexa) para desenhar.</param>
public sealed record Junction(int Key, IReadOnlyList<int> ConnectorIds, IReadOnlyList<NodeKey> Nodes, IReadOnlyList<Vec2> Outline, Vec2 Center);

/// <summary>
/// Encontra conflitos e nós da rede pela geometria. Conectores que se cruzam (e não estão marcados
/// como "níveis diferentes") juntam seus pontos de saída e chegada num nó; conectores que saem ou
/// chegam nesses pontos entram no mesmo nó.
/// </summary>
public sealed class ConflictAnalysis
{
    /// <summary>Cruzamentos a menos disso das pontas de um conector não contam (conectores que saem ou chegam juntos), m.</summary>
    public const double EndTolerance = 1.5;

    public Topology Topology { get; }
    public List<Junction> Junctions { get; } = [];
    public List<Conflict> Conflicts { get; } = [];
    /// <summary>Problemas que impedem exportar a rede para o SUMO.</summary>
    public List<string> Problems { get; } = [];

    private readonly Dictionary<NodeKey, int> _junctionOfNode = [];
    private readonly Dictionary<int, int> _junctionOfConnector = [];

    private ConflictAnalysis(Topology topology) => Topology = topology;

    public int? JunctionOf(NodeKey n) => _junctionOfNode.TryGetValue(n, out var k) ? k : null;

    /// <summary>Nó em que o conector é interno (as duas pontas no mesmo nó), ou null.</summary>
    public int? JunctionOfConnector(int connectorId) => _junctionOfConnector.TryGetValue(connectorId, out var k) ? k : null;

    public Junction? FindJunction(int key) => Junctions.Find(j => j.Key == key);

    public static ConflictAnalysis Of(Network net)
    {
        var a = new ConflictAnalysis(new Topology(net));
        a.Run();
        return a;
    }

    private void Run()
    {
        var net = Topology.Network;
        var conns = net.Connectors
            .Where(Topology.Covers)
            .Select(c => (C: c, G: net.GeometryOf(c)))
            .Where(x => x.G is not null)
            .Select(x => (x.C, G: x.G!))
            .ToList();

        var parent = new Dictionary<NodeKey, NodeKey>();
        NodeKey Find(NodeKey n)
        {
            if (!parent.TryGetValue(n, out var p)) return parent[n] = n;
            return p.Equals(n) ? n : parent[n] = Find(p);
        }
        void Union(NodeKey x, NodeKey y)
        {
            var (rx, ry) = (Find(x), Find(y));
            if (!rx.Equals(ry)) parent[rx] = ry;
        }
        var grouped = new HashSet<NodeKey>();

        // 1) Cruzamentos entre conectores.
        for (var i = 0; i < conns.Count; i++)
        for (var j = i + 1; j < conns.Count; j++)
        {
            var (ca, ga) = conns[i];
            var (cb, gb) = conns[j];
            // Saindo das mesmas faixas (divergência) ou chegando nas mesmas (convergência, tratada à parte),
            // os eixos andam quase juntos perto da ponta e podem se "cruzar" à toa.
            if (Topology.FromNode(ca).Equals(Topology.FromNode(cb)) && Overlap(ca.FromLane, ca.LaneCount, cb.FromLane, cb.LaneCount)) continue;
            if (Topology.ToNode(ca).Equals(Topology.ToNode(cb)) && Overlap(ca.ToLane, ca.LaneCount, cb.ToLane, cb.LaneCount)) continue;
            if (FirstCrossing(ga.Axis, gb.Axis) is not { } x) continue;
            var (a, b) = ca.Id < cb.Id ? (ca, cb) : (cb, ca);
            var (sa, sb) = ca.Id < cb.Id ? (x.SA, x.SB) : (x.SB, x.SA);
            Conflicts.Add(new Conflict(ConflictKind.Crossing, a.Id, b.Id, x.Point, sa, sb, null));
            if (net.PriorityOf(a.Id, b.Id) == ConflictPriority.None) continue;

            NodeKey[] nodes = [Topology.FromNode(ca), Topology.ToNode(ca), Topology.FromNode(cb), Topology.ToNode(cb)];
            foreach (var n in nodes.Skip(1)) Union(nodes[0], n);
            grouped.UnionWith(nodes);
        }

        // 2) Conectores que saem ou chegam num ponto já agrupado entram no mesmo nó.
        bool changed;
        do
        {
            changed = false;
            foreach (var (c, _) in conns)
            {
                var (f, t) = (Topology.FromNode(c), Topology.ToNode(c));
                var fin = grouped.Contains(f);
                var tin = grouped.Contains(t);
                if (fin == tin) continue;
                Union(f, t);
                grouped.Add(f);
                grouped.Add(t);
                changed = true;
            }
        } while (changed);

        // 3) Monta os nós.
        foreach (var group in grouped.GroupBy(Find))
        {
            var nodes = group.ToList();
            var internals = conns.Where(x => nodes.Contains(Topology.FromNode(x.C)) && nodes.Contains(Topology.ToNode(x.C))).ToList();
            if (internals.Count == 0) continue;
            var key = internals.Min(x => x.C.Id);
            foreach (var n in nodes) _junctionOfNode[n] = key;
            foreach (var x in internals) _junctionOfConnector[x.C.Id] = key;

            var outlinePts = new List<Vec2>();
            foreach (var (_, g) in internals)
            {
                outlinePts.AddRange(Polyline.Offset(g.Axis.Points, g.TotalWidth / 2));
                outlinePts.AddRange(Polyline.Offset(g.Axis.Points, -g.TotalWidth / 2));
            }
            var hull = Intersections.ConvexHull(outlinePts);
            var center = hull.Count > 0 ? hull.Aggregate(Vec2.Zero, (s, p) => s + p) / hull.Count : Vec2.Zero;
            Junctions.Add(new Junction(key, [.. internals.Select(x => x.C.Id).Order()], nodes, hull, center));

            // Um trecho de link com as duas pontas no mesmo nó viraria uma edge do nó para ele mesmo.
            foreach (var n in nodes)
                if (!Topology.IsLinkEnd(n) && nodes.Contains(n with { Cut = n.Cut + 1 }))
                    Problems.Add($"{net.FindLink(n.LinkId)?.Name}: um trecho do link fica inteiro dentro do nó {key}. " +
                                 "Termine o link antes da interseção e ligue os movimentos por conectores.");
        }
        Junctions.Sort((x, y) => x.Key.CompareTo(y.Key));

        for (var i = 0; i < Conflicts.Count; i++)
            if (Conflicts[i].Kind == ConflictKind.Crossing)
                Conflicts[i] = Conflicts[i] with { JunctionKey = JunctionOfConnector(Conflicts[i].A) };

        // 4) Convergências: conector entrando no meio de um link (com o fluxo principal) ou dois
        //    conectores chegando nas mesmas faixas no mesmo ponto.
        foreach (var (c, g) in conns)
        {
            var to = Topology.ToNode(c);
            if (!Topology.IsLinkStart(to))
                Conflicts.Add(new Conflict(ConflictKind.Merge, c.Id, ConflictRule.MainLine, g.Axis.Points[^1],
                    g.Axis.Length, c.ToS, JunctionOf(to)) { MainLinkId = c.ToLinkId });
        }
        for (var i = 0; i < conns.Count; i++)
        for (var j = i + 1; j < conns.Count; j++)
        {
            var (ca, ga) = conns[i];
            var (cb, gb) = conns[j];
            if (!Topology.ToNode(ca).Equals(Topology.ToNode(cb))) continue;
            var overlap = ca.ToLane < cb.ToLane + cb.LaneCount && cb.ToLane < ca.ToLane + ca.LaneCount;
            if (!overlap) continue;
            var (a, b, gA, gB) = ca.Id < cb.Id ? (ca, cb, ga, gb) : (cb, ca, gb, ga);
            Conflicts.Add(new Conflict(ConflictKind.Merge, a.Id, b.Id, gA.Axis.Points[^1], gA.Axis.Length, gB.Axis.Length,
                JunctionOf(Topology.ToNode(a))));
        }

        // 5) Links que cruzam conectores ou outros links no meio do caminho.
        var links = net.Links.Where(l => Topology.Has(l.Id)).ToList();
        for (var i = 0; i < links.Count; i++)
        {
            for (var j = i + 1; j < links.Count; j++)
                if (FirstCrossing(links[i].Axis(), links[j].Axis()) is { } x)
                    Conflicts.Add(new Conflict(ConflictKind.LinkCrossing, links[i].Id, links[j].Id, x.Point, x.SA, x.SB, null));
            foreach (var (c, g) in conns)
            {
                if (c.FromLinkId == links[i].Id || c.ToLinkId == links[i].Id) continue;
                if (FirstCrossing(g.Axis, links[i].Axis()) is { } x)
                    Conflicts.Add(new Conflict(ConflictKind.LinkCrossing, c.Id, links[i].Id, x.Point, x.SA, x.SB, null));
            }
        }
    }

    private static bool Overlap(int a, int na, int b, int nb) => a < b + nb && b < a + na;

    /// <summary>Primeiro cruzamento longe das pontas das duas curvas.</summary>
    private static Crossing? FirstCrossing(Polyline a, Polyline b)
    {
        foreach (var x in Intersections.Between(a, b).OrderBy(x => x.SA))
        {
            if (x.SA < EndTolerance || x.SA > a.Length - EndTolerance) continue;
            if (x.SB < EndTolerance || x.SB > b.Length - EndTolerance) continue;
            return x;
        }
        return null;
    }

    /// <summary>Conflitos de cruzamento de link que o usuário ainda não marcou como níveis diferentes.</summary>
    public IEnumerable<Conflict> UnresolvedLinkCrossings =>
        Conflicts.Where(c => c.Kind == ConflictKind.LinkCrossing && Topology.Network.PriorityOf(c.A, c.B) != ConflictPriority.None);
}
