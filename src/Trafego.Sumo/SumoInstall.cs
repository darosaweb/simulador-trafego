using System.Diagnostics;

namespace Trafego.Sumo;

/// <summary>Localiza o SUMO instalado (variável SUMO_HOME) e roda as ferramentas de linha de comando.</summary>
public static class SumoInstall
{
    public static string? Home
    {
        get
        {
            var home = Environment.GetEnvironmentVariable("SUMO_HOME");
            return !string.IsNullOrWhiteSpace(home) && Directory.Exists(home) ? home : null;
        }
    }

    public static string BinDir => Path.Combine(Home ?? throw NotFound(), "bin");

    public static string Tool(string name)
    {
        var exe = Path.Combine(BinDir, OperatingSystem.IsWindows() ? name + ".exe" : name);
        return File.Exists(exe) ? exe : throw new FileNotFoundException($"{name} não encontrado em {BinDir}.");
    }

    public static Exception NotFound() =>
        new InvalidOperationException("SUMO não encontrado: instale o Eclipse SUMO e defina a variável SUMO_HOME.");

    public sealed record ToolResult(int ExitCode, string Output);

    /// <summary>Roda uma ferramenta do SUMO e devolve a saída (stdout + stderr).</summary>
    public static ToolResult Run(string tool, IEnumerable<string> args, string workingDir, TimeSpan? timeout = null,
        CancellationToken cancel = default)
    {
        var psi = new ProcessStartInfo(Tool(tool))
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Não foi possível iniciar {tool}.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(5));
        while (!p.WaitForExit(200))
        {
            if (cancel.IsCancellationRequested)
            {
                p.Kill(entireProcessTree: true);
                cancel.ThrowIfCancellationRequested();
            }
            if (DateTime.UtcNow > deadline)
            {
                p.Kill(entireProcessTree: true);
                throw new TimeoutException($"{tool} não terminou a tempo.");
            }
        }
        p.WaitForExit(); // garante que a saída redirecionada terminou de ser lida
        return new ToolResult(p.ExitCode, (stdout.Result + stderr.Result).Trim());
    }
}
