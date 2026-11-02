using Avalonia;
using Trafego.App.Help;
using Trafego.Sumo;

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
        // "Trafego.exe --replicacao <pasta> <semente>": uma replicação com balsa, pedida pelo Avaliar (SumoBatch).
        if (args is ["--replicacao", var scenarioDir, var seedText])
        {
            try
            {
                SumoBatch.RunReplication(SumoScenario.Open(scenarioDir), int.Parse(seedText));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.Exit(1);
            }
            return;
        }
        SumoBatch.WorkerPath = Environment.ProcessPath;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
