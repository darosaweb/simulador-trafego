using Trafego.Core.Geometry;

namespace Trafego.Core.Model;

/// <summary>Ponto sobre um link: posição ao longo do eixo e faixa.</summary>
public readonly record struct LinkHit(Link Link, Projection Projection, int Lane);

public readonly record struct ConnectorHit(Connector Connector, Projection Projection);

public static class HitTest
{
    /// <summary>
    /// Link sob o ponto, com a faixa. Com sobreposição (viaduto, conectores), ganha o
    /// desenhado por último, que também é o que aparece por cima.
    /// </summary>
    public static LinkHit? Link(Network net, Vec2 p, double tolerance)
    {
        for (var i = net.Links.Count - 1; i >= 0; i--)
        {
            var link = net.Links[i];
            if (link.Points.Count < 2) continue;
            if (link.Axis().Project(p) is not { } proj) continue;
            var half = link.TotalWidth / 2;
            // Distance, e não só o lateral: fora das pontas do eixo o lateral pode ser pequeno e o ponto estar longe.
            if (proj.Distance > half + tolerance) continue;
            var lane = link.LaneAt(Math.Clamp(proj.Lateral, -half, half)) ?? 0;
            return new LinkHit(link, proj, lane);
        }
        return null;
    }

    public static ConnectorHit? Connector(Network net, Vec2 p, double tolerance)
    {
        for (var i = net.Connectors.Count - 1; i >= 0; i--)
        {
            var c = net.Connectors[i];
            if (net.GeometryOf(c) is not { } g) continue;
            if (g.Axis.Project(p) is not { } proj) continue;
            if (proj.Distance <= g.TotalWidth / 2 + tolerance) return new ConnectorHit(c, proj);
        }
        return null;
    }
}
