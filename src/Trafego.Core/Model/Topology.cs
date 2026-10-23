namespace Trafego.Core.Model;

/// <summary>Ponto de corte de um link: índice do corte em <see cref="Topology.Cuts"/>.</summary>
public readonly record struct NodeKey(int LinkId, int Cut);

/// <summary>
/// Onde cada link é cortado: nas pontas e onde algum conector sai ou chega. Entre dois cortes
/// seguidos há um trecho contínuo do link; cada corte é um ponto onde os movimentos se encontram.
/// </summary>
public sealed class Topology
{
    /// <summary>Cortes mais próximos que isso de outro corte ou da ponta do link se juntam, m.</summary>
    public const double MergeDistance = 1.0;

    private readonly Dictionary<int, double[]> _cuts = [];
    public Network Network { get; }

    public Topology(Network net)
    {
        Network = net;
        foreach (var link in net.Links.Where(IsUsable)) _cuts[link.Id] = ComputeCuts(net, link);
    }

    /// <summary>Links curtos demais ou sem geometria ficam de fora da simulação.</summary>
    public static bool IsUsable(Link l) => l.Points.Count >= 2 && l.Axis().Length >= MergeDistance;

    public bool Has(int linkId) => _cuts.ContainsKey(linkId);

    public IReadOnlyList<double> Cuts(int linkId) => _cuts[linkId];

    /// <summary>Quantidade de trechos do link (cortes − 1).</summary>
    public int PieceCount(int linkId) => _cuts[linkId].Length - 1;

    public int CutIndex(int linkId, double s)
    {
        var cuts = _cuts[linkId];
        var best = 0;
        for (var i = 1; i < cuts.Length; i++)
            if (Math.Abs(cuts[i] - s) < Math.Abs(cuts[best] - s)) best = i;
        return best;
    }

    public bool Covers(Connector c) => _cuts.ContainsKey(c.FromLinkId) && _cuts.ContainsKey(c.ToLinkId);
    public NodeKey FromNode(Connector c) => new(c.FromLinkId, CutIndex(c.FromLinkId, c.FromS));
    public NodeKey ToNode(Connector c) => new(c.ToLinkId, CutIndex(c.ToLinkId, c.ToS));

    public bool IsLinkStart(NodeKey n) => n.Cut == 0;
    public bool IsLinkEnd(NodeKey n) => n.Cut == _cuts[n.LinkId].Length - 1;

    private static double[] ComputeCuts(Network net, Link link)
    {
        var len = link.Axis().Length;
        var wanted = net.Connectors.Where(c => c.FromLinkId == link.Id).Select(c => c.FromS)
            .Concat(net.Connectors.Where(c => c.ToLinkId == link.Id).Select(c => c.ToS))
            .Where(s => s > MergeDistance && s < len - MergeDistance)
            .Order();
        var cuts = new List<double> { 0 };
        foreach (var s in wanted)
            if (s - cuts[^1] >= MergeDistance) cuts.Add(s);
        if (len - cuts[^1] < MergeDistance) cuts.RemoveAt(cuts.Count - 1);
        cuts.Add(len);
        return [.. cuts];
    }
}
