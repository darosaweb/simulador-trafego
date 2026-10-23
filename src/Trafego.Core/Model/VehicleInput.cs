namespace Trafego.Core.Model;

/// <summary>
/// Entrada de veículos no início de um link, como o "vehicle input" do Vissim. É também uma origem
/// da matriz O/D: se tem algum volume na matriz, os veículos vão para os destinos dela; senão, entram
/// com o volume da entrada e, em cada escolha (conector saindo ou seguir em frente), as opções têm a
/// mesma chance.
/// </summary>
public sealed class VehicleInput
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int LinkId { get; set; }

    /// <summary>Volume padrão, veículos por hora (vale nos intervalos sem volume próprio).</summary>
    public double VolumeVph { get; set; } = 600;

    /// <summary>Volumes por intervalo de tempo; os intervalos sem entrada usam <see cref="VolumeVph"/>.</summary>
    public List<VolumeAt> Volumes { get; set; } = [];

    public double VolumeAt(double slice) =>
        Volumes.Find(v => Math.Abs(v.SliceStart - slice) < 1e-6)?.Vph ?? VolumeVph;

    public void SetVolume(double slice, double vph)
    {
        Volumes.RemoveAll(v => Math.Abs(v.SliceStart - slice) < 1e-6);
        Volumes.Add(new VolumeAt { SliceStart = slice, Vph = vph });
    }

    /// <summary>Fração de veículos pesados (0 a 1).</summary>
    public double TruckShare { get; set; } = 0.1;
}
