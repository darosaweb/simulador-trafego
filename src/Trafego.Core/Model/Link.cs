using System.Text.Json.Serialization;
using Trafego.Core.Geometry;

namespace Trafego.Core.Model;

public sealed class Lane
{
    public const double DefaultWidth = 3.5;

    /// <summary>Largura da faixa, m.</summary>
    public double Width { get; set; } = DefaultWidth;
}

/// <summary>
/// Trecho de via com uma ou mais faixas no mesmo sentido, como o "link" do Vissim.
/// A geometria é o eixo central; as faixas se distribuem para os dois lados dele.
/// Faixa 0 é a da direita (convenção do SUMO e do Vissim).
/// </summary>
public sealed class Link
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Pontos de controle do eixo, em metros no mundo.</summary>
    public List<Vec2> Points { get; set; } = [];

    /// <summary>Se verdadeiro, o eixo é uma curva suave pelos pontos; senão, uma polilinha.</summary>
    public bool Smooth { get; set; }

    public List<Lane> Lanes { get; set; } = [new()];

    /// <summary>Velocidade máxima permitida no trecho, km/h.</summary>
    public double SpeedKmh { get; set; } = 60;

    [JsonIgnore] public double TotalWidth => Lanes.Sum(l => l.Width);

    /// <summary>Borda direita da faixa (deslocamento lateral, positivo = à esquerda do eixo).</summary>
    public double LaneRightEdge(int lane)
    {
        var edge = -TotalWidth / 2;
        for (var i = 0; i < lane; i++) edge += Lanes[i].Width;
        return edge;
    }

    public double LaneLeftEdge(int lane) => LaneRightEdge(lane) + Lanes[lane].Width;

    public double LaneCenter(int lane) => LaneRightEdge(lane) + Lanes[lane].Width / 2;

    /// <summary>Faixa que contém o deslocamento lateral dado, ou null se estiver fora da via.</summary>
    public int? LaneAt(double lateral)
    {
        var edge = -TotalWidth / 2;
        if (lateral < edge) return null;
        for (var i = 0; i < Lanes.Count; i++)
        {
            edge += Lanes[i].Width;
            if (lateral <= edge) return i;
        }
        return null;
    }

    private Polyline? _axis;
    private int _axisKey;

    /// <summary>Eixo amostrado (curva suave ou polilinha), recalculado quando a geometria muda.</summary>
    public Polyline Axis()
    {
        var key = GeometryKey();
        if (_axis is null || key != _axisKey)
        {
            _axis = new Polyline(Smooth ? Curves.CatmullRom(Points) : [.. Points]);
            _axisKey = key;
        }
        return _axis;
    }

    private int GeometryKey()
    {
        var h = new HashCode();
        h.Add(Smooth);
        foreach (var p in Points) h.Add(p);
        return h.ToHashCode();
    }
}
