using Trafego.Core.Geometry;

namespace Trafego.Core.Model;

/// <summary>
/// Liga um grupo de faixas contíguas de um link a um grupo de mesmo tamanho em outro,
/// como o "conector" do Vissim. É por conectores que se fazem conversões, alças e
/// entradas e saídas de rotatória.
/// </summary>
public sealed class Connector
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    public int FromLinkId { get; set; }
    /// <summary>Faixa mais à direita do grupo de origem.</summary>
    public int FromLane { get; set; }
    /// <summary>Posição ao longo do link de origem onde o conector sai, m.</summary>
    public double FromS { get; set; }

    public int ToLinkId { get; set; }
    /// <summary>Faixa mais à direita do grupo de destino.</summary>
    public int ToLane { get; set; }
    /// <summary>Posição ao longo do link de destino onde o conector chega, m.</summary>
    public double ToS { get; set; }

    /// <summary>Quantidade de faixas ligadas (a mesma na origem e no destino).</summary>
    public int LaneCount { get; set; } = 1;

    /// <summary>Pontos intermediários opcionais; sem eles, o conector é uma curva tangente às duas vias.</summary>
    public List<Vec2> Points { get; set; } = [];

    /// <summary>Semáforo que controla a entrada no conector (null = sem semáforo).</summary>
    public SignalRef? Signal { get; set; }

    /// <summary>
    /// Percentual dos veículos que pegam este conector no ponto de onde ele sai (0 a 100), como a
    /// "decisão de rota" do Vissim numa divergência. O que sobra fica com as opções sem percentual
    /// (seguir no link e os outros conectores), em partes iguais. Null = parte igual (padrão).
    /// Vale para entradas sem matriz O/D.
    /// </summary>
    public double? SplitPercent { get; set; }

    /// <summary>
    /// Percentual só para os veículos pesados (caminhões), quando é diferente do dos leves.
    /// Null = os pesados seguem <see cref="SplitPercent"/>, como os leves.
    /// </summary>
    public double? SplitPercentTruck { get; set; }

    /// <summary>Percentual que vale para a classe de veículo (null = parte igual).</summary>
    public double? SplitFor(VehicleClass cls) => cls == VehicleClass.Truck ? SplitPercentTruck ?? SplitPercent : SplitPercent;
}

/// <summary>Geometria calculada de um conector.</summary>
/// <param name="Axis">Eixo do grupo de faixas.</param>
/// <param name="LaneOffsets">Deslocamento lateral do centro de cada faixa em relação ao eixo (índice 0 = direita).</param>
/// <param name="LaneWidths">Largura de cada faixa, herdada do link de origem.</param>
public sealed record ConnectorGeometry(Polyline Axis, double[] LaneOffsets, double[] LaneWidths)
{
    public double TotalWidth => LaneWidths.Sum();
}
