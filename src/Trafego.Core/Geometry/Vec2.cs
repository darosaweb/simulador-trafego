namespace Trafego.Core.Geometry;

/// <summary>
/// Ponto ou vetor 2D. No mundo, as coordenadas são em metros com o eixo Y
/// para baixo (como a imagem); a inversão para Y para cima fica na exportação.
/// </summary>
public readonly record struct Vec2(double X, double Y)
{
    public static readonly Vec2 Zero = new(0, 0);

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);
    public static Vec2 operator *(Vec2 a, double k) => new(a.X * k, a.Y * k);
    public static Vec2 operator *(double k, Vec2 a) => new(a.X * k, a.Y * k);
    public static Vec2 operator /(Vec2 a, double k) => new(a.X / k, a.Y / k);

    public double Length => Math.Sqrt(X * X + Y * Y);

    public double Dot(Vec2 o) => X * o.X + Y * o.Y;

    public Vec2 Normalized()
    {
        var len = Length;
        return len > 0 ? this / len : Zero;
    }

    /// <summary>
    /// Normal à esquerda de quem anda no sentido do vetor. Com Y para baixo,
    /// andando para leste (1,0) a esquerda é o norte (0,-1).
    /// </summary>
    public Vec2 LeftNormal() => new Vec2(Y, -X).Normalized();

    public static double Distance(Vec2 a, Vec2 b) => (a - b).Length;

    public static Vec2 Lerp(Vec2 a, Vec2 b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
}
