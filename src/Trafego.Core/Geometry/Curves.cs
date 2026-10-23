namespace Trafego.Core.Geometry;

public static class Curves
{
    /// <summary>Espaçamento alvo da amostragem das curvas, m.</summary>
    public const double SampleStep = 1.0;

    /// <summary>
    /// Curva suave (Catmull-Rom centripetal) que passa por todos os pontos de controle.
    /// A versão centripetal evita laços e overshoot com pontos de espaçamento irregular,
    /// o que é o caso normal num traçado feito à mão.
    /// </summary>
    public static List<Vec2> CatmullRom(IReadOnlyList<Vec2> points, double step = SampleStep)
    {
        var n = points.Count;
        if (n <= 2) return [.. points];

        // Pontos-fantasma nas pontas por reflexão (duplicar o ponto daria divisão por zero).
        var ext = new List<Vec2>(n + 2) { 2 * points[0] - points[1] };
        ext.AddRange(points);
        ext.Add(2 * points[n - 1] - points[n - 2]);

        var result = new List<Vec2>();
        for (var i = 0; i < n - 1; i++)
        {
            var spanLen = Vec2.Distance(points[i], points[i + 1]);
            var steps = Math.Clamp((int)Math.Ceiling(spanLen / step), 2, 200);
            for (var j = 0; j < steps; j++)
                result.Add(Centripetal(ext[i], ext[i + 1], ext[i + 2], ext[i + 3], (double)j / steps));
        }
        result.Add(points[n - 1]);
        return result;
    }

    private static Vec2 Centripetal(Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3, double u)
    {
        const double eps = 1e-6;
        var t0 = 0.0;
        var t1 = t0 + Math.Sqrt(Math.Max(Vec2.Distance(p0, p1), eps));
        var t2 = t1 + Math.Sqrt(Math.Max(Vec2.Distance(p1, p2), eps));
        var t3 = t2 + Math.Sqrt(Math.Max(Vec2.Distance(p2, p3), eps));
        var t = t1 + (t2 - t1) * u;

        var a1 = Vec2.Lerp(p0, p1, (t - t0) / (t1 - t0));
        var a2 = Vec2.Lerp(p1, p2, (t - t1) / (t2 - t1));
        var a3 = Vec2.Lerp(p2, p3, (t - t2) / (t3 - t2));
        var b1 = Vec2.Lerp(a1, a2, (t - t0) / (t2 - t0));
        var b2 = Vec2.Lerp(a2, a3, (t - t1) / (t3 - t1));
        return Vec2.Lerp(b1, b2, (t - t1) / (t2 - t1));
    }

    /// <summary>
    /// Bézier cúbica de p0 a p3 que sai na direção t0 e chega na direção t3
    /// (tangentes unitárias). É a forma padrão de um conector.
    /// </summary>
    public static List<Vec2> TangentBezier(Vec2 p0, Vec2 t0, Vec2 p3, Vec2 t3, double step = SampleStep)
    {
        var d = Vec2.Distance(p0, p3);
        var k = d / 3;
        var p1 = p0 + t0 * k;
        var p2 = p3 - t3 * k;
        var steps = Math.Clamp((int)Math.Ceiling(d * 1.3 / step), 4, 200);
        var result = new List<Vec2>(steps + 1);
        for (var i = 0; i <= steps; i++)
        {
            var u = (double)i / steps;
            var v = 1 - u;
            result.Add(v * v * v * p0 + 3 * v * v * u * p1 + 3 * v * u * u * p2 + u * u * u * p3);
        }
        return result;
    }
}
