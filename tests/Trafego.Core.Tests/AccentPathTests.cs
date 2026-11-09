using Trafego.Core.Model;
using Trafego.Sumo;

namespace Trafego.Core.Tests;

/// <summary>Projetos em pastas com acento (ex.: "Ribeirão Preto"): o libsumo falhava com "unknown exception".</summary>
[Collection(Libsumo.Collection)]
public class AccentPathTests
{
    [Fact]
    public void Simula_em_pasta_com_acento()
    {
        if (!LibsumoSession.Available) return;
        var (net, _) = FerryTests.River(twoWay: false);
        var dir = Path.Combine(Path.GetTempPath(), $"trafego Ribeirão Preto {Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var before = Environment.CurrentDirectory;
        try
        {
            using (var session = new LibsumoSession(SumoScenario.Prepare(net, dir, new SimulationSettings { StartTimeOfDay = 0, DurationMinutes = 2 })))
            {
                SimSnapshot last;
                do last = session.Step(300);
                while (!last.Finished);
            }
            Assert.True(File.Exists(Path.Combine(dir, LibsumoSession.AnimationPrefix + "viagens.xml")));
            Assert.Equal(before, Environment.CurrentDirectory);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
