namespace Trafego.Core.Model;

public enum MeasurementKind
{
    /// <summary>Contagem num ponto: volume e velocidade (laço indutivo).</summary>
    Count,
    /// <summary>Fila a montante de um ponto (ex.: linha de retenção): média e máxima.</summary>
    Queue,
    /// <summary>Tempo de viagem e atraso entre dois pontos.</summary>
    TravelTime,
}

/// <summary>
/// Ponto de medição na rede, como os "data collection points", "queue counters" e
/// "vehicle travel times" do Vissim. Contagem e fila usam só o ponto inicial.
/// </summary>
public sealed class Measurement
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public MeasurementKind Kind { get; set; }

    public int LinkId { get; set; }
    /// <summary>Posição ao longo do link, m.</summary>
    public double S { get; set; }

    /// <summary>Tempo de viagem: link e posição do fim do trecho medido.</summary>
    public int ToLinkId { get; set; }
    public double ToS { get; set; }

    /// <summary>Fila: até quantos metros a montante do ponto a fila é medida.</summary>
    public double QueueLength { get; set; } = 150;

    public static string KindName(MeasurementKind k) => k switch
    {
        MeasurementKind.Count => "Contagem",
        MeasurementKind.Queue => "Fila",
        _ => "Tempo de viagem",
    };
}
