namespace Trafego.Core.Model;

/// <summary>Passo de um caminho: um trecho de link (entre dois cortes) ou um conector.</summary>
public abstract record PathStep;

/// <param name="Piece">Trecho i vai do corte i ao corte i+1 do link (<see cref="Topology.Cuts"/>).</param>
public sealed record LinkStep(int LinkId, int Piece) : PathStep;

public sealed record ConnectorStep(int ConnectorId) : PathStep;

/// <summary>Caminho pela rede, do início de um link até sair dela.</summary>
/// <param name="Probability">Chance do caminho quando, em cada escolha, as opções têm a mesma chance.</param>
/// <param name="Length">Comprimento total, m.</param>
/// <param name="FreeFlowTime">Tempo de percurso na velocidade máxima de cada trecho, s.</param>
public sealed record NetPath(IReadOnlyList<PathStep> Steps, double Probability, double Length, double FreeFlowTime)
{
    /// <summary>Link em que o caminho termina (a saída da rede).</summary>
    public int EndLinkId => Steps.OfType<LinkStep>().LastOrDefault()?.LinkId ?? 0;

    /// <summary>Conectores usados, em ordem: identificam o caminho de forma estável para as escolhas de rota.</summary>
    public List<int> ConnectorIds => [.. Steps.OfType<ConnectorStep>().Select(s => s.ConnectorId)];
}

/// <summary>
/// Caminhos pela rede de links e conectores. Em cada corte, o veículo pode seguir no link
/// ou pegar um dos conectores que saem dali; nenhum trecho se repete num caminho (o que corta
/// voltas infinitas em rotatórias).
/// </summary>
public static class RouteFinder
{
    public const int MaxPaths = 500;

    /// <summary>
    /// Todos os caminhos a partir do início do link (no máximo <see cref="MaxPaths"/>), com a chance de cada
    /// um para a classe de veículo (os percentuais dos conectores podem ser diferentes para os pesados).
    /// </summary>
    public static List<NetPath> PathsFrom(ConflictAnalysis analysis, int linkId, VehicleClass cls = VehicleClass.Car)
    {
        var topo = analysis.Topology;
        var net = topo.Network;
        var result = new List<NetPath>();
        if (!topo.Has(linkId)) return result;

        // Conectores utilizáveis, agrupados pelo corte de onde saem.
        var outgoing = net.Connectors
            .Where(c => topo.Covers(c) && net.GeometryOf(c) is not null)
            .Where(c => analysis.JunctionOfConnector(c.Id) is null || topo.ToNode(c).Cut < topo.PieceCount(c.ToLinkId))
            .GroupBy(topo.FromNode)
            .ToDictionary(g => g.Key, g => g.ToList());

        var steps = new List<PathStep>();
        var used = new HashSet<PathStep>();

        void Emit(double prob)
        {
            double length = 0, time = 0;
            foreach (var s in steps)
            {
                var (l, v) = Measure(net, topo, s);
                length += l;
                time += l / Math.Max(v, 0.1);
            }
            result.Add(new NetPath([.. steps], prob, length, time));
        }

        void Walk(int link, int piece, double prob)
        {
            if (result.Count >= MaxPaths) return;
            var step = new LinkStep(link, piece);
            if (!used.Add(step))
            {
                if (steps.Count > 0) Emit(prob);
                return;
            }
            steps.Add(step);

            // Opções neste ponto e o percentual definido em cada uma (null = parte igual do que sobra).
            var options = new List<(Action<double> Go, double? Percent)>();
            if (piece + 1 < topo.PieceCount(link)) options.Add((p => Walk(link, piece + 1, p), null));
            if (outgoing.TryGetValue(new NodeKey(link, piece + 1), out var conns))
            {
                foreach (var c in conns)
                {
                    var to = topo.ToNode(c);
                    options.Add((p =>
                    {
                        var cs = new ConnectorStep(c.Id);
                        if (!used.Add(cs)) { Emit(p); return; }
                        steps.Add(cs);
                        if (to.Cut < topo.PieceCount(c.ToLinkId)) Walk(c.ToLinkId, to.Cut, p);
                        else Emit(p);
                        steps.RemoveAt(steps.Count - 1);
                        used.Remove(cs);
                    }, c.SplitFor(cls)));
                }
            }

            if (options.Count == 0) Emit(prob);
            else
            {
                var shares = Shares([.. options.Select(o => o.Percent)]);
                for (var i = 0; i < options.Count; i++)
                    if (shares[i] > 0) options[i].Go(prob * shares[i]);
            }

            steps.RemoveAt(steps.Count - 1);
            used.Remove(step);
        }

        Walk(linkId, 0, 1);
        return result;
    }

    /// <summary>
    /// Como os veículos se dividem num corte de link: seguir no link (ConnectorId null) e cada conector
    /// que sai dali, com a fração de cada um (somam 1).
    /// </summary>
    public static List<(int? ConnectorId, double Share)> SplitAt(Topology topo, NodeKey node, VehicleClass cls = VehicleClass.Car)
    {
        var net = topo.Network;
        var options = new List<(int? Id, double? Percent)>();
        if (topo.Has(node.LinkId) && !topo.IsLinkEnd(node)) options.Add((null, null));
        foreach (var c in net.Connectors.Where(c => topo.Covers(c) && net.GeometryOf(c) is not null && topo.FromNode(c).Equals(node)))
            options.Add((c.Id, c.SplitFor(cls)));
        var shares = Shares([.. options.Select(o => o.Percent)]);
        return [.. options.Select((o, i) => (o.Id, shares[i]))];
    }

    /// <summary>
    /// Divide os veículos entre as opções de um ponto de escolha: as que têm percentual ficam com ele;
    /// o que sobra vai em partes iguais para as sem percentual. Se não sobra nada ou não há opção sem
    /// percentual, os percentuais são normalizados para somar 100%.
    /// </summary>
    public static double[] Shares(IReadOnlyList<double?> percents)
    {
        var n = percents.Count;
        var result = new double[n];
        if (n == 0) return result;
        var set = percents.Where(p => p is not null).Sum(p => Math.Max(0, p!.Value)) / 100;
        var free = percents.Count(p => p is null);
        var rest = 1 - set;

        if (free > 0 && rest > 1e-9)
        {
            for (var i = 0; i < n; i++)
                result[i] = percents[i] is { } p ? Math.Max(0, p) / 100 : rest / free;
            return result;
        }
        if (set > 0)
        {
            for (var i = 0; i < n; i++)
                result[i] = percents[i] is { } p ? Math.Max(0, p) / 100 / set : 0;
            return result;
        }
        for (var i = 0; i < n; i++) result[i] = 1.0 / n; // tudo zerado: volta à divisão igual
        return result;
    }

    /// <summary>Até k caminhos diferentes do início de um link até sair da rede por outro, do mais rápido ao mais lento.</summary>
    public static List<NetPath> Alternatives(ConflictAnalysis analysis, int originLinkId, int destinationLinkId, int k = 3) =>
        [.. PathsFrom(analysis, originLinkId)
            .Where(p => p.EndLinkId == destinationLinkId && IsExitStep(analysis.Topology, p))
            .OrderBy(p => p.FreeFlowTime)
            .Take(k)];

    /// <summary>Saídas da rede alcançáveis a partir do link (links onde algum caminho termina no fim).</summary>
    public static List<int> ExitsFrom(ConflictAnalysis analysis, int linkId) =>
        [.. PathsFrom(analysis, linkId).Where(p => IsExitStep(analysis.Topology, p)).Select(p => p.EndLinkId).Distinct()];

    /// <summary>
    /// Saídas da rede: links que terminam sem nenhum conector saindo do fim. É por elas que os
    /// veículos deixam a rede, e são os destinos da matriz O/D.
    /// </summary>
    public static List<Link> Exits(Topology topo)
    {
        var net = topo.Network;
        return [.. net.Links.Where(l => topo.Has(l.Id)).Where(l =>
        {
            var end = new NodeKey(l.Id, topo.PieceCount(l.Id));
            return !net.Connectors.Any(c => topo.Covers(c) && topo.FromNode(c).Equals(end));
        })];
    }

    /// <summary>O caminho termina de fato no fim do link (e não cortado por repetir um trecho).</summary>
    private static bool IsExitStep(Topology topo, NetPath p) =>
        p.Steps.Count > 0 && p.Steps[^1] is LinkStep last && last.Piece == topo.PieceCount(last.LinkId) - 1;

    /// <summary>Comprimento (m) e velocidade máxima (m/s) de um passo.</summary>
    public static (double Length, double Speed) Measure(Network net, Topology topo, PathStep s)
    {
        switch (s)
        {
            case LinkStep ls:
                var cuts = topo.Cuts(ls.LinkId);
                var link = net.FindLink(ls.LinkId)!;
                return (cuts[ls.Piece + 1] - cuts[ls.Piece], link.SpeedKmh / 3.6);
            case ConnectorStep cs:
                var c = net.FindConnector(cs.ConnectorId)!;
                var len = net.GeometryOf(c)?.Axis.Length ?? 0;
                var speed = Math.Min(net.FindLink(c.FromLinkId)?.SpeedKmh ?? 50, net.FindLink(c.ToLinkId)?.SpeedKmh ?? 50) / 3.6;
                return (len, speed);
            default:
                return (0, 1);
        }
    }
}
