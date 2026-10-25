using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Trafego.Core.Model;
using Trafego.Sumo;

namespace Trafego.App.Editor;

/// <summary>
/// Exportar vídeo do teste: trecho, velocidade e resolução; o enquadramento é o que está na tela.
/// Mostra o progresso, permite cancelar e, no fim, abrir o vídeo.
/// </summary>
public sealed class VideoWindow : Window
{
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9aa4b2"));
    private static readonly (string Label, double Speed)[] Speeds =
        [("1× (tempo real)", 1), ("2×", 2), ("5×", 5), ("10×", 10), ("20×", 20), ("30×", 30), ("60×", 60)];
    private static readonly (string Label, int W, int H)[] Resolutions =
        [("1920 × 1080 (Full HD)", 1920, 1080), ("1280 × 720 (HD)", 1280, 720), ("Tamanho da tela", 0, 0)];

    private readonly NetworkCanvas _canvas;
    private readonly Project _project;
    private readonly string _scenarioDirectory;
    private readonly string _suggestedName;

    private readonly NumericUpDown _start = new() { Minimum = 0, Increment = 1, FormatString = "0.#", Width = 120 };
    private readonly NumericUpDown _duration = new() { Minimum = 0.5m, Increment = 1, FormatString = "0.#", Width = 120 };
    private readonly ComboBox _speed = new() { Width = 180 };
    private readonly ComboBox _resolution = new() { Width = 220 };
    private readonly TextBlock _estimate = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1, Height = 8, IsVisible = false };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _go = new() { Content = "Escolher arquivo e gerar vídeo" };
    private readonly Button _cancel = new() { Content = "Cancelar", IsVisible = false };
    private readonly StackPanel _done = new() { Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false };
    private CancellationTokenSource? _running;
    private string? _output;

    public VideoWindow(NetworkCanvas canvas, Project project, string scenarioDirectory, string suggestedName)
    {
        _canvas = canvas;
        _project = project;
        _scenarioDirectory = scenarioDirectory;
        _suggestedName = suggestedName;
        Title = "Exportar vídeo — Tráfego";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var total = project.Simulation.DurationMinutes;
        _start.Maximum = (decimal)Math.Max(0, total - 0.5);
        _start.Value = 0;
        _duration.Maximum = (decimal)total;
        _duration.Value = (decimal)Math.Min(total, 10);
        foreach (var (label, _) in Speeds) _speed.Items.Add(new ComboBoxItem { Content = label });
        _speed.SelectedIndex = 3;
        foreach (var (label, _, _) in Resolutions) _resolution.Items.Add(new ComboBoxItem { Content = label });
        _resolution.SelectedIndex = 0;
        _start.ValueChanged += (_, _) => UpdateEstimate();
        _duration.ValueChanged += (_, _) => UpdateEstimate();
        _speed.SelectionChanged += (_, _) => UpdateEstimate();

        _go.Click += async (_, _) => await GenerateAsync();
        _cancel.Click += (_, _) => _running?.Cancel();
        var open = new Button { Content = "Abrir vídeo" };
        open.Click += (_, _) => { if (_output is not null) Process.Start(new ProcessStartInfo(_output) { UseShellExecute = true }); };
        var folder = new Button { Content = "Abrir pasta" };
        folder.Click += (_, _) => { if (_output is not null) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_output}\"") { UseShellExecute = true }); };
        _done.Children.Add(open);
        _done.Children.Add(folder);

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "O vídeo roda de novo este teste (mesma semente, mesmo resultado) e mostra a área que está na tela agora: " +
                   "ajuste o zoom antes de exportar. Por cima, o relógio e um resumo da simulação.",
            TextWrapping = TextWrapping.Wrap, Foreground = Muted,
        });
        panel.Children.Add(Row("Começar aos", _start, Unit($"min (de {total:0} min simulados)")));
        panel.Children.Add(Row("Duração", _duration, Unit("min de simulação")));
        panel.Children.Add(Row("Velocidade", _speed));
        panel.Children.Add(Row("Resolução", _resolution));
        panel.Children.Add(_estimate);
        panel.Children.Add(_bar);
        panel.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_go);
        buttons.Children.Add(_cancel);
        buttons.Children.Add(_done);
        panel.Children.Add(buttons);
        Content = panel;

        Closing += (_, e) =>
        {
            if (_running is null) return;
            e.Cancel = true; // espera o cancelamento terminar (o libsumo e o FFmpeg precisam fechar direito)
            _running.Cancel();
        };
        UpdateEstimate();
        if (FfmpegEncoder.Locate() is null)
        {
            _status.Text = "FFmpeg não encontrado. Instale com: winget install Gyan.FFmpeg (e abra o programa de novo).";
            _go.IsEnabled = false;
        }
    }

    private VideoOptions Options()
    {
        var (_, w, h) = Resolutions[Math.Max(0, _resolution.SelectedIndex)];
        if (w == 0)
        {
            var size = _canvas.CurrentView.Size;
            (w, h) = ((int)size.Width, (int)size.Height);
        }
        var start = (double)(_start.Value ?? 0);
        var duration = Math.Min((double)(_duration.Value ?? 1), _project.Simulation.DurationMinutes - start);
        return new VideoOptions(start, Math.Max(0.1, duration), Speeds[Math.Max(0, _speed.SelectedIndex)].Speed, w, h);
    }

    private void UpdateEstimate()
    {
        var o = Options();
        var t = TimeSpan.FromSeconds(o.VideoSeconds);
        _estimate.Text = $"Vídeo de {(int)t.TotalMinutes}:{t.Seconds:00} ({o.Frames} quadros a {o.Fps} por segundo).";
    }

    private async Task GenerateAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Salvar vídeo",
            SuggestedFileName = _suggestedName + ".mp4",
            DefaultExtension = "mp4",
            FileTypeChoices = [new FilePickerFileType("Vídeo MP4") { Patterns = ["*.mp4"] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        if (!path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) path += ".mp4";

        _running = new CancellationTokenSource();
        _go.IsVisible = false;
        _done.IsVisible = false;
        _cancel.IsVisible = true;
        _bar.IsVisible = true;
        _bar.Value = 0;
        foreach (var c in new Control[] { _start, _duration, _speed, _resolution }) c.IsEnabled = false;
        var watch = Stopwatch.StartNew();
        try
        {
            await VideoExporter.RunAsync(_canvas, _project, _scenarioDirectory, Options(), path,
                new Progress<(double Fraction, string Text)>(p =>
                {
                    _bar.Value = p.Fraction;
                    _status.Text = p.Text;
                }), _running.Token);
            _output = path;
            _status.Text = $"Vídeo salvo em {watch.Elapsed.TotalSeconds:0} s: {path}";
            _done.IsVisible = true;
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Exportação cancelada.";
        }
        catch (Exception ex)
        {
            _status.Text = "Não foi possível gerar o vídeo: " + ex.Message;
        }
        finally
        {
            _running.Dispose();
            _running = null;
            _go.IsVisible = true;
            _go.Content = "Gerar outro vídeo";
            _cancel.IsVisible = false;
            foreach (var c in new Control[] { _start, _duration, _speed, _resolution }) c.IsEnabled = true;
        }
    }

    private static TextBlock Unit(string text) => new() { Text = text, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };

    private static StackPanel Row(string label, params Control[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center });
        foreach (var c in controls) row.Children.Add(c);
        return row;
    }
}
