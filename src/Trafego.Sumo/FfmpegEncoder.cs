using System.Diagnostics;
using System.Text;

namespace Trafego.Sumo;

/// <summary>
/// Gera um vídeo MP4 (H.264) mandando quadros brutos (BGRA) para o FFmpeg pela entrada padrão.
/// O FFmpeg é um programa à parte; <see cref="Locate"/> o procura no PATH e na pasta do winget.
/// </summary>
public sealed class FfmpegEncoder : IDisposable
{
    private readonly Process _process;
    private readonly Stream _input;
    private readonly StringBuilder _log = new();
    private readonly string _output;
    private bool _finished;

    public int Width { get; }
    public int Height { get; }
    public int FrameBytes => Width * Height * 4;

    /// <summary>Caminho do ffmpeg.exe, ou null se não estiver instalado.</summary>
    public static string? Locate()
    {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        if (Environment.GetEnvironmentVariable("FFMPEG_PATH") is { Length: > 0 } explicitPath && File.Exists(explicitPath))
            return explicitPath;

        // PATH do processo e, no Windows, o do usuário e o da máquina (o winget muda o do usuário, e um
        // programa aberto antes da instalação ainda não enxerga a mudança).
        var paths = new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine }
            .Select(t => Environment.GetEnvironmentVariable("PATH", t) ?? "")
            .SelectMany(p => p.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (var dir in paths)
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* entrada inválida no PATH */ }
        }

        var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(winget))
            foreach (var pkg in Directory.GetDirectories(winget, "Gyan.FFmpeg*"))
                if (Directory.GetFiles(pkg, exe, SearchOption.AllDirectories).FirstOrDefault() is { } found)
                    return found;
        return null;
    }

    /// <param name="width">Largura do quadro; é arredondada para baixo para um número par (exigência do H.264).</param>
    public FfmpegEncoder(string outputPath, int width, int height, int fps, string? ffmpegPath = null)
    {
        var ffmpeg = ffmpegPath ?? Locate() ?? throw new InvalidOperationException(
            "FFmpeg não encontrado. Instale com: winget install Gyan.FFmpeg");
        Width = Math.Max(2, width - width % 2);
        Height = Math.Max(2, height - height % 2);
        _output = outputPath;

        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{Width}x{Height}", "-r", fps.ToString(), "-i", "-",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "22", "-pix_fmt", "yuv420p",
            "-movflags", "+faststart", outputPath,
        }) psi.ArgumentList.Add(a);

        _process = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o FFmpeg.");
        _process.ErrorDataReceived += (_, e) => { if (e.Data is { } line) lock (_log) _log.AppendLine(line); };
        _process.OutputDataReceived += (_, _) => { };
        _process.BeginErrorReadLine();
        _process.BeginOutputReadLine();
        _input = _process.StandardInput.BaseStream;
    }

    /// <summary>Manda um quadro BGRA de Width × Height pixels, linha a linha, sem preenchimento.</summary>
    public void Write(ReadOnlySpan<byte> bgra)
    {
        if (bgra.Length != FrameBytes)
            throw new ArgumentException($"Quadro com {bgra.Length} bytes; esperado {FrameBytes}.");
        try
        {
            _input.Write(bgra);
        }
        catch (IOException)
        {
            throw new InvalidOperationException("O FFmpeg parou de aceitar quadros:\n" + Log());
        }
    }

    /// <summary>Fecha a entrada e espera o FFmpeg terminar de gravar o arquivo.</summary>
    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        _input.Close();
        if (!_process.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            _process.Kill(entireProcessTree: true);
            throw new TimeoutException("O FFmpeg não terminou de gravar o vídeo a tempo.");
        }
        _process.WaitForExit();
        if (_process.ExitCode != 0)
            throw new InvalidOperationException("O FFmpeg falhou ao gravar o vídeo:\n" + Log());
    }

    private string Log()
    {
        lock (_log) return _log.ToString().Trim();
    }

    /// <summary>Sem <see cref="Finish"/> (cancelado ou com erro): mata o FFmpeg e apaga o arquivo incompleto.</summary>
    public void Dispose()
    {
        if (!_finished)
        {
            _finished = true;
            try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            _process.WaitForExit();
            try { File.Delete(_output); } catch (IOException) { }
        }
        _process.Dispose();
    }
}
