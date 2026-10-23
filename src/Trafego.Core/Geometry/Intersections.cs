namespace Trafego.Core.Geometry;

/// <summary>Cruzamento de duas polilinhas: posições ao longo de cada uma e o ponto.</summary>
public readonly record struct Crossing(double SA, double SB, Vec2 Point);

public static class Intersections
{
    /// <summary>Todos os pontos onde as duas polilinhas se cruzam.</summary>
    public static List<Crossing> Between(Polyline a, Polyline b)
    {
        var result = new List<Crossing>();
        if (a.Points.Count < 2 || b.Points.Count < 2 || !Overlap(Bounds(a.Points), Bounds(b.Points))) return result;

        for (var i = 0; i < a.Points.Count - 1; i++)
        {
            var p = a.Points[i];
            var r = a.Points[i + 1] - p;
            for (var j = 0; j < b.Points.Count - 1; j++)
            {
                var q = b.Points[j];
                var s = b.Points[j + 1] - q;
                var denom = Cross(r, s);
                if (Math.Abs(denom) < 1e-12) continue; // paralelos: não contam como cruzamento
                var qp = q - p;
                var t = Cross(qp, s) / denom;
                var u = Cross(qp, r) / denom;
                if (t is < 0 or > 1 || u is < 0 or > 1) continue;
                result.Add(new Crossing(
                    a.CumulativeAt(i) + r.Length * t,
                    b.CumulativeAt(j) + s.Length * u,
                    p + r * t));
            }
        }
        return result;
    }

    private static double Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;

    public static (Vec2 Min, Vec2 Max) Bounds(IEnumerable<Vec2> pts)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var p in pts)
        {
            x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y);
            x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y);
        }
        return (new Vec2(x0, y0), new Vec2(x1, y1));
    }

    private static bool Overlap((Vec2 Min, Vec2 Max) a, (Vec2 Min, Vec2 Max) b) =>
        a.Min.X <= b.Max.X && b.Min.X <= a.Max.X && a.Min.Y <= b.Max.Y && b.Min.Y <= a.Max.Y;

    /// <summary>Envoltória convexa (cadeia monótona de Andrew), em sentido anti-horário.</summary>
    public static List<Vec2> ConvexHull(IEnumerable<Vec2> points)
    {
        var pts = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (pts.Count < 3) return pts;
        var hull = new List<Vec2>(2 * pts.Count);
        foreach (var pass in new[] { pts, Enumerable.Reverse(pts).ToList() })
        {
            var start = hull.Count;
            foreach (var p in pass)
            {
                while (hull.Count >= start + 2 && Cross(hull[^1] - hull[^2], p - hull[^2]) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        return hull;
    }
}
