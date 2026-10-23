namespace Trafego.Core.Geometry;

public readonly record struct PathSample(Vec2 Point, Vec2 Tangent);

/// <summary>Projeção de um ponto sobre a polilinha.</summary>
/// <param name="S">Distância ao longo da polilinha (comprimento de arco), m.</param>
/// <param name="Lateral">Deslocamento lateral com sinal: positivo à esquerda do sentido, m.</param>
/// <param name="Distance">Distância absoluta do ponto até a polilinha, m.</param>
/// <param name="Segment">Índice do segmento pts[i] → pts[i+1] mais próximo.</param>
/// <param name="Point">Ponto projetado sobre a polilinha.</param>
public readonly record struct Projection(double S, double Lateral, double Distance, int Segment, Vec2 Point);

/// <summary>Polilinha parametrizada por comprimento de arco.</summary>
public sealed class Polyline
{
    public IReadOnlyList<Vec2> Points { get; }
    private readonly double[] _cum;

    public Polyline(IReadOnlyList<Vec2> points)
    {
        Points = points;
        _cum = new double[Math.Max(points.Count, 1)];
        for (var i = 1; i < points.Count; i++)
            _cum[i] = _cum[i - 1] + Vec2.Distance(points[i - 1], points[i]);
    }

    public double Length => _cum[^1];

    /// <summary>Comprimento acumulado até o vértice i.</summary>
    public double CumulativeAt(int i) => _cum[i];

    public PathSample At(double s)
    {
        var n = Points.Count;
        if (n == 0) return new PathSample(Vec2.Zero, new Vec2(1, 0));
        if (n == 1) return new PathSample(Points[0], new Vec2(1, 0));

        var clamped = Math.Clamp(s, 0, Length);
        int lo = 0, hi = n - 2;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (_cum[mid] <= clamped) lo = mid;
            else hi = mid - 1;
        }

        // Segmentos de comprimento zero não têm direção: procura um vizinho que tenha.
        var seg = lo;
        while (seg < n - 2 && _cum[seg + 1] - _cum[seg] == 0) seg++;
        var a = Points[seg];
        var b = Points[seg + 1];
        var segLen = _cum[seg + 1] - _cum[seg];
        var t = segLen == 0 ? 0 : (clamped - _cum[seg]) / segLen;
        return new PathSample(Vec2.Lerp(a, b, Math.Clamp(t, 0, 1)), (b - a).Normalized());
    }

    /// <summary>Trecho da polilinha entre as posições s0 e s1 (s0 &lt; s1), com os vértices internos.</summary>
    public List<Vec2> Slice(double s0, double s1)
    {
        s0 = Math.Clamp(s0, 0, Length);
        s1 = Math.Clamp(s1, s0, Length);
        var result = new List<Vec2> { At(s0).Point };
        for (var i = 0; i < Points.Count; i++)
            if (_cum[i] > s0 + 1e-9 && _cum[i] < s1 - 1e-9) result.Add(Points[i]);
        result.Add(At(s1).Point);
        return result;
    }

    public Projection? Project(Vec2 p)
    {
        if (Points.Count < 2) return null;
        Projection? best = null;
        for (var i = 0; i < Points.Count - 1; i++)
        {
            var a = Points[i];
            var b = Points[i + 1];
            var d = b - a;
            var len2 = d.Dot(d);
            var t = len2 == 0 ? 0 : Math.Clamp((p - a).Dot(d) / len2, 0, 1);
            var q = a + d * t;
            var dist = Vec2.Distance(p, q);
            if (best is null || dist < best.Value.Distance)
            {
                var lateral = (p - q).Dot(d.LeftNormal());
                best = new Projection(_cum[i] + Math.Sqrt(len2) * t, lateral, dist, i, q);
            }
        }
        return best;
    }

    /// <summary>
    /// Curva paralela deslocada lateralmente (positivo = à esquerda).
    /// Junta em esquadro (miter) com limite, para não estourar em curvas fechadas.
    /// </summary>
    public static List<Vec2> Offset(IReadOnlyList<Vec2> pts, double offset, double miterLimit = 4)
    {
        var n = pts.Count;
        var result = new List<Vec2>(n);
        if (n < 2)
        {
            result.AddRange(pts);
            return result;
        }

        for (var i = 0; i < n; i++)
        {
            var nPrev = i > 0 ? (pts[i] - pts[i - 1]).LeftNormal() : Vec2.Zero;
            var nNext = i < n - 1 ? (pts[i + 1] - pts[i]).LeftNormal() : Vec2.Zero;
            if (nPrev == Vec2.Zero) nPrev = nNext;
            if (nNext == Vec2.Zero) nNext = nPrev;

            var bis = (nPrev + nNext).Normalized();
            if (bis == Vec2.Zero) bis = nNext; // retorno de 180°: sem bissetriz definida
            var cos = bis.Dot(nNext);
            var scale = cos > 1.0 / miterLimit ? 1 / cos : miterLimit;
            result.Add(pts[i] + bis * (offset * scale));
        }
        return result;
    }
}
