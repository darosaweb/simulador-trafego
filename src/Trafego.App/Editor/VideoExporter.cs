using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Trafego.Core.Geometry;
using Trafego.Core.Model;
using Trafego.Core.Serialization;
using Trafego.Sumo;

namespace Trafego.App.Editor;

/// <param name="StartMinutes">A partir de quantos minutos depois do início da simulação o vídeo começa.</param>
/// <param name="DurationMinutes">Quantos minutos de simulação o vídeo cobre.</param>
/// <param name="Speed">Segundos de simulação por segundo de vídeo (10 = dez vezes mais rápido).</param>
public sealed record VideoOptions(double StartMinutes, double DurationMinutes, double Speed, int Width, int Height, int Fps = 30)
{
    public double VideoSeconds => DurationMinutes * 60 / Speed;
    public int Frames => (int)Math.Ceiling(VideoSeconds * Fps);
}

/// <summary>
/// Gera o vídeo de um teste: roda de novo o cenário (mesma semente, mesmo resultado) pelo libsumo,
/// sem tempo real, desenha cada quadro fora da tela com o mesmo desenho do mapa e manda para o FFmpeg.
/// Roda na thread da interface (o libsumo e o desenho exigem), devolvendo o controle a cada poucos quadros.
/// </summary>
public static class VideoExporter
{
    public const string OutputPrefix = "video_";

    public static async Task RunAsync(NetworkCanvas canvas, Project project, string scenarioDirectory, VideoOptions o,
        string outputPath, IProgress<(double Fraction, string Text)> progress, CancellationToken cancel)
    {
        if (!LibsumoSession.Available) throw new InvalidOperationException("SUMO não encontrado.");
        var ffmpeg = FfmpegEncoder.Locate() ?? throw new InvalidOperationException(
            "FFmpeg não encontrado. Instale com: winget install Gyan.FFmpeg");

        progress.Report((0, "Preparando o cenário no SUMO…"));
        var copy = ProjectFile.FromJson(ProjectFile.ToJson(project));
        var scenario = await Task.Run(() => SumoScenario.Prepare(copy.Network, scenarioDirectory, copy.Simulation), cancel);

        // Enquadra o mesmo que está na tela: mesmo centro, mesma largura visível.
        var (zoom0, pan0, size0) = canvas.CurrentView;
        var center = new Vec2((size0.Width / 2 - pan0.X) / zoom0, (size0.Height / 2 - pan0.Y) / zoom0);

        using var session = new LibsumoSession(scenario, OutputPrefix);
        using var encoder = new FfmpegEncoder(outputPath, o.Width, o.Height, o.Fps, ffmpeg);
        var zoom = zoom0 * encoder.Width / Math.Max(1, size0.Width);
        var pan = new Vec2(encoder.Width / 2.0 - center.X * zoom, encoder.Height / 2.0 - center.Y * zoom);
        var size = new Size(encoder.Width, encoder.Height);
        using var bitmap = new RenderTargetBitmap(new PixelSize(encoder.Width, encoder.Height), new Vector(96, 96));
        var buffer = new byte[encoder.FrameBytes];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var start = Math.Min(scenario.BeginTime + o.StartMinutes * 60, scenario.EndTime);
            var end = Math.Min(start + o.DurationMinutes * 60, scenario.EndTime);
            var frames = (int)Math.Ceiling((end - start) / o.Speed * o.Fps);
            var caption = $"trecho {SimulationSettings.Clock(start)[..5]}–{SimulationSettings.Clock(end)[..5]} · vídeo {o.Speed:0.#}× mais rápido";

            // Até o começo do trecho escolhido, só simula (a rede vai enchendo).
            while (session.Time < start - 1e-6)
            {
                cancel.ThrowIfCancellationRequested();
                var steps = (int)Math.Ceiling((start - session.Time) / session.StepLength - 1e-6);
                session.Step(Math.Min(steps, 300));
                progress.Report((0, $"Simulando até o início do vídeo ({SimulationSettings.Clock(session.Time)})…"));
                await Yield();
            }

            for (var i = 0; i < frames; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var target = start + i * o.Speed / o.Fps;
                var steps = (int)Math.Round((target - session.Time) / session.StepLength);
                var snapshot = session.Step(Math.Max(0, steps));

                using (var ctx = bitmap.CreateDrawingContext())
                    canvas.RenderVideoFrame(ctx, size, zoom, pan, snapshot, caption);
                bitmap.CopyPixels(new PixelRect(0, 0, encoder.Width, encoder.Height), handle.AddrOfPinnedObject(), buffer.Length, encoder.Width * 4);
                encoder.Write(buffer);

                if (i % 4 == 0)
                {
                    progress.Report(((i + 1.0) / frames, $"Quadro {i + 1} de {frames} · {SimulationSettings.Clock(snapshot.Time)}"));
                    await Yield();
                }
            }

            progress.Report((1, "Finalizando o arquivo…"));
            await Task.Run(encoder.Finish, cancel);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Devolve o controle para a interface (progresso, botão Cancelar) e volta logo em seguida.</summary>
    private static async Task Yield() =>
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
}
