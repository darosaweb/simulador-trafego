using Avalonia;
using Trafego.App.Help;

namespace Trafego.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // "Trafego.exe --gerar-ajuda <pasta>": só gera as páginas da Ajuda (usado para fazer o PDF do manual).
        if (args is ["--gerar-ajuda", var dir])
        {
            HelpPages.Build(outputDir: dir);
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
