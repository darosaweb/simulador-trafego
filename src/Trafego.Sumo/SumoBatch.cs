using Trafego.Core.Results;

namespace Trafego.Sumo;

/// <summary>
/// Avaliação: roda o cenário várias vezes, com sementes diferentes, em processos sumo paralelos
/// (sem animação), e junta os resultados. Cada rodada grava as saídas com o prefixo "r{semente}_".
/// </summary>
public static class SumoBatch
{
    public static async Task<ResultSet> RunAsync(SumoScenario scenario, string name, IProgress<string>? progress = null,
        CancellationToken cancel = default)
    {
        var settings = scenario.Settings;
        var seeds = Enumerable.Range(settings.Seed, Math.Max(1, settings.Replications)).ToList();
        var parallel = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        using var gate = new SemaphoreSlim(parallel);
        var done = 0;

        var tasks = seeds.Select(async seed =>
        {
            await gate.WaitAsync(cancel);
            try
            {
                var prefix = $"r{seed}_";
                var result = await Task.Run(() => SumoInstall.Run("sumo",
                    scenario.SumoArgs("--seed", seed.ToString(), "--output-prefix", prefix), scenario.Directory,
                    TimeSpan.FromHours(2), cancel), cancel);
                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"O sumo falhou na replicação com semente {seed}:\n{result.Output}");
                var run = ResultReader.Read(scenario, prefix, seed);
                progress?.Report($"Replicação {Interlocked.Increment(ref done)} de {seeds.Count} concluída.");
                return run;
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        var runs = await Task.WhenAll(tasks);
        return new ResultSet
        {
            Name = name,
            ProjectName = name,
            Settings = settings,
            Runs = [.. runs.OrderBy(r => r.Seed)],
        };
    }
}
