using System.Text.Json.Serialization;

namespace Trafego.Core.Model;

/// <summary>
/// Travessia de balsa. O link de ida (e o de volta, se houver) é desenhado sobre a água, da rampa de uma margem
/// até a outra: os veículos esperam na rampa, embarcam até lotar o convés, atravessam juntos e desembarcam.
/// Sem link de volta, a balsa volta vazia.
/// </summary>
public sealed class Ferry
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Link da travessia de ida (da margem A para a B).</summary>
    public int LinkId { get; set; }
    /// <summary>Link da travessia de volta (da B para a A), se a balsa leva veículos nos dois sentidos.</summary>
    public int? ReturnLinkId { get; set; }

    /// <summary>Quantas embarcações fazem a travessia (cada margem atende uma de cada vez).</summary>
    public int Vessels { get; set; } = 1;
    /// <summary>Comprimento útil de cada faixa do convés, m: nela os veículos ficam em fila, um atrás do outro.</summary>
    public double DeckLength { get; set; } = 40;
    /// <summary>Faixas do convés, lado a lado. O embarque enche uma faixa por vez, pela rampa de uma faixa só.</summary>
    public int DeckLanes { get; set; } = 1;

    /// <summary>
    /// Pátio de acúmulo antes da rampa da margem A (um link de várias faixas): quem chega recebe uma faixa, as faixas
    /// enchem uma por vez e embarcam uma por vez. Null = fila única na rampa.
    /// </summary>
    public int? YardLinkId { get; set; }
    /// <summary>Pátio de acúmulo da margem B (para a volta).</summary>
    public int? ReturnYardLinkId { get; set; }
    /// <summary>Tempo de travessia, da partida até atracar do outro lado.</summary>
    public double CrossingMinutes { get; set; } = 10;
    /// <summary>Atracada, a balsa sai quando lota ou depois deste tempo, o que vier primeiro.</summary>
    public double MaxWaitMinutes { get; set; } = 10;

    public const double MinDeck = 5;
    /// <summary>Folga entre veículos no convés, m (a mesma entre uma faixa e a seguinte na fila da simulação).</summary>
    public const double DeckSpacing = 1.25;
    /// <summary>Largura de cada faixa do convés, no desenho, m.</summary>
    public const double DeckLaneWidth = 3.2;

    /// <summary>
    /// Na simulação o convés é uma fila só, com as faixas uma atrás da outra: o comprimento dela, m. O desenho
    /// põe as faixas lado a lado.
    /// </summary>
    [JsonIgnore] public double LinearDeck => Math.Max(1, DeckLanes) * DeckLength + (Math.Max(1, DeckLanes) - 1) * DeckSpacing;

    /// <summary>Pátio de onde sai quem atravessa no sentido (0 ida, 1 volta).</summary>
    public int? YardOf(int dir) => dir == 0 ? YardLinkId : ReturnYardLinkId;

    public bool Uses(int linkId) => LinkId == linkId || ReturnLinkId == linkId;
}
