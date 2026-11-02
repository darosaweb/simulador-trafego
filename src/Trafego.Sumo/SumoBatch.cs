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
        // Com balsa e sem processo auxiliar, as replicações rodam aqui mesmo pelo libsumo: uma de cada vez.
        var parallel = scenario.Ferries.Count > 0 && WorkerPath is null ? 1 : Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        using var gate = new SemaphoreSlim(parallel);
        var done = 0;

        var tasks = seeds.Select(async seed =>
        {
            await gate.WaitAsync(cancel);
            try
            {
                var prefix = Prefix(seed);
                var result = await Task.Run(() => scenario.Ferries.Count == 0
                    ? SumoInstall.Run("sumo", scenario.SumoArgs("--seed", seed.ToString(), "--output-prefix", prefix),
                        scenario.Directory, TimeSpan.FromHours(2), cancel)
                    : RunWithFerries(scenario, seed, cancel), cancel);
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

    private static string Prefix(int seed) => $"r{seed}_";

    /// <summary>
    /// Programa que roda uma replicação com balsa em outro processo (o próprio daRosa Tráfego, com
    /// "--replicacao pasta semente"). As balsas precisam do libsumo, que só aceita uma simulação por processo:
    /// assim as replicações continuam em paralelo. Nulo: roda no processo atual, uma de cada vez (testes).
    /// </summary>
    public static string? WorkerPath { get; set; }

    private static SumoInstall.ToolResult RunWithFerries(SumoScenario scenario, int seed, CancellationToken cancel)
    {
        if (WorkerPath is { } exe)
            return SumoInstall.RunProcess(exe, ["--replicacao", scenario.Directory, seed.ToString()], scenario.Directory,
                TimeSpan.FromHours(2), cancel);
        try
        {
            RunReplication(scenario, seed);
            return new SumoInstall.ToolResult(0, "");
        }
        catch (Exception ex)
        {
            return new SumoInstall.ToolResult(1, ex.Message);
        }
    }

    /// <summary>Uma replicação completa pelo libsumo (com as balsas), gravando as saídas com o prefixo da semente.</summary>
    public static void RunReplication(SumoScenario scenario, int seed)
    {
        using var session = new LibsumoSession(scenario, Prefix(seed), ["--seed", seed.ToString()]);
        while (!session.Step(1000).Finished) { }
    }
}
