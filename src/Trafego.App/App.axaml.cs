using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Trafego.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // A abertura aparece primeiro; a janela principal é criada logo depois e a substitui.
            var splash = new SplashWindow();
            splash.Show();
            var shown = DateTime.Now;
            Dispatcher.UIThread.Post(async () =>
            {
                var main = new MainWindow(desktop.Args ?? []);
                var wait = TimeSpan.FromSeconds(1.5) - (DateTime.Now - shown);
                if (wait > TimeSpan.Zero) await Task.Delay(wait);
                desktop.MainWindow = main;
                main.Show();
                splash.Close();
            }, DispatcherPriority.Background);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
