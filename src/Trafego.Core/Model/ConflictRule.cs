namespace Trafego.Core.Model;

public enum ConflictPriority
{
    /// <summary>O SUMO decide pela hierarquia das vias e pela geometria.</summary>
    Auto,
    /// <summary>O movimento A tem preferência; B espera uma brecha.</summary>
    A,
    /// <summary>O movimento B tem preferência; A espera uma brecha.</summary>
    B,
    /// <summary>Não há conflito: os movimentos estão em níveis diferentes (viaduto, passagem inferior).</summary>
    None,
}

/// <summary>
/// Preferência num conflito entre dois movimentos, como as "áreas de conflito" do Vissim.
/// A é sempre um conector; B é outro conector ou 0, que quer dizer "seguir no link de destino de A"
/// (o fluxo principal onde A converge).
/// </summary>
public sealed class ConflictRule
{
    public const int MainLine = 0;

    public int A { get; set; }
    public int B { get; set; }
    public ConflictPriority Priority { get; set; }

    /// <summary>Normaliza o par: dois conectores ficam em ordem crescente de id; o fluxo principal fica em B.</summary>
    public static (int A, int B, bool Swapped) Normalize(int a, int b)
    {
        if (a == MainLine) return (b, a, true);
        if (b == MainLine) return (a, b, false);
        return a <= b ? (a, b, false) : (b, a, true);
    }
}

public enum JunctionControl
{
    /// <summary>Via preferencial: o SUMO usa a hierarquia (links acima de conectores) e as regras de conflito.</summary>
    Priority,
    /// <summary>Preferência de quem vem pela direita (CTB, art. 29, quando não há sinalização).</summary>
    RightBeforeLeft,
    /// <summary>Parada obrigatória em todas as aproximações.</summary>
    AllWayStop,
}

/// <summary>Configuração de um nó, identificado pelo menor id de conector dentro dele.</summary>
public sealed class JunctionSetting
{
    public int Key { get; set; }
    public JunctionControl Control { get; set; }
}
