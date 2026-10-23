namespace Trafego.Core.Tests;

/// <summary>
/// O libsumo só aceita uma simulação por processo, e o xUnit roda classes de teste em paralelo:
/// as classes que abrem o libsumo ficam nesta coleção, que roda uma de cada vez.
/// </summary>
[CollectionDefinition(Collection, DisableParallelization = true)]
public sealed class Libsumo
{
    public const string Collection = "libsumo";
}
