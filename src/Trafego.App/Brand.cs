using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Trafego.App;

/// <summary>Peças da marca daRosa Tráfego: a rosa, o nome e as cores (veja docs/MARCA.md).</summary>
public static class Brand
{
    public const string Name = "daRosa Tráfego";
    public const string Tagline = "Simulação de tráfego para projetos viários";

    public static readonly Color Blush = Color.Parse("#FECCCB");
    public static readonly Color Rose = Color.Parse("#FF8CA1");
    public static readonly Color Raspberry = Color.Parse("#CC3467");
    public static readonly Color Ink = Color.Parse("#231F20");
    /// <summary>Fundo claro das telas de marca (abertura e Sobre).</summary>
    public static readonly Color Paper = Color.Parse("#FFF7F6");

    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

    /// <summary>A rosa original da daRosa, com fundo transparente (320 px).</summary>
    public static Bitmap RoseImage() => new(AssetLoader.Open(new Uri("avares://Trafego/Assets/rosa.png")));

    /// <summary>"daRosa" leve e "Tráfego" forte, em framboesa — o logotipo para fundo claro.</summary>
    public static Control Wordmark(double size)
    {
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = size, LineHeight = size * 1.3 };
        text.Inlines!.Add(new Avalonia.Controls.Documents.Run("daRosa ")
        {
            FontWeight = FontWeight.Normal, Foreground = new SolidColorBrush(Ink),
        });
        text.Inlines.Add(new Avalonia.Controls.Documents.Run("Tráfego")
        {
            FontWeight = FontWeight.ExtraBold, Foreground = new SolidColorBrush(Raspberry),
        });
        return text;
    }

    /// <summary>Rosa + nome + frase, empilhados: o miolo da abertura e do Sobre.</summary>
    public static StackPanel Lockup(double roseSize)
    {
        var panel = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new Image { Source = RoseImage(), Width = roseSize, Height = roseSize, HorizontalAlignment = HorizontalAlignment.Center });
        var mark = Wordmark(roseSize * 0.27);
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        mark.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(mark);
        panel.Children.Add(new TextBlock
        {
            Text = Tagline, FontSize = 14, Foreground = new SolidColorBrush(Color.Parse("#6b5a5e")),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        return panel;
    }
}

/// <summary>Tela de abertura, enquanto a janela principal carrega.</summary>
public sealed class SplashWindow : Window
{
    public SplashWindow()
    {
        Title = Brand.Name;
        Width = 520;
        Height = 380;
        CanResize = false;
        ShowInTaskbar = false;
        SystemDecorations = SystemDecorations.None;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Brand.Paper);
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;

        var lockup = Brand.Lockup(150);
        lockup.VerticalAlignment = VerticalAlignment.Center;
        var footer = new TextBlock
        {
            Text = $"versão {Brand.Version} · carregando…", FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#9a8a8e")),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 16),
        };
        // Faixa framboesa no topo, como a faixa de bolinhas da rosa.
        var stripe = new Border { Height = 6, Background = new SolidColorBrush(Brand.Raspberry), VerticalAlignment = VerticalAlignment.Top };
        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#ecd6d6")), BorderThickness = new Thickness(1),
            Child = new Grid { Children = { stripe, lockup, footer } },
        };
    }
}

/// <summary>Ajuda → Sobre: versão, créditos e licenças.</summary>
public sealed class AboutWindow : Window
{
    public AboutWindow()
    {
        Title = "Sobre o " + Brand.Name;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Brand.Paper);
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;

        var panel = Brand.Lockup(120);
        panel.Margin = new Thickness(24, 24, 24, 20);
        TextBlock Line(string text, double top = 0) => new()
        {
            Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#4a3f42")), Margin = new Thickness(0, top, 0, 0),
        };
        panel.Children.Add(Line($"Versão {Brand.Version}", 10));
        panel.Children.Add(Line("Motor de simulação: Eclipse SUMO (EPL-2.0). Interface: Avalonia (MIT). Fonte: Inter (SIL OFL).", 10));
        panel.Children.Add(Line($"© {DateTime.Now.Year} daRosa. Todos os direitos reservados."));
        var ok = new Button { Content = "Fechar", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
        ok.Click += (_, _) => Close();
        panel.Children.Add(ok);
        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Brand.Raspberry), BorderThickness = new Thickness(0, 6, 0, 0), Child = panel,
        };
    }
}
