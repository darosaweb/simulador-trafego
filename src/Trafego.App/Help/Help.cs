using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace Trafego.App.Help;

/// <summary>
/// Ajuda do programa: o manual (docs/MANUAL.md, copiado para a pasta "ajuda" ao lado do executável) vira uma
/// página HTML com a marca, sumário lateral e busca, aberta no navegador padrão — sem internet.
/// A página é gerada de novo quando o manual muda.
/// </summary>
public static class HelpPages
{
    /// <summary>Assuntos para o F1 e os botões "?": o começo do título da seção (sem o número).</summary>
    public static class Topic
    {
        public const string Start = "seu-primeiro-cenário";
        public const string Screen = "a-tela";
        public const string Photo = "foto-aérea";
        public const string Links = "links";
        public const string Connectors = "conectores";
        public const string Conflicts = "nós-e-conflitos";
        public const string Signals = "semáforos";
        public const string Demand = "gerando-tráfego";
        public const string Simulation = "simulando";
        public const string Measurements = "medições";
        public const string Results = "avaliação-e-resultados";
        public const string Scenarios = "comparando-cenários";
        public const string Shortcuts = "atalhos-de-teclado";
        public const string Problems = "problemas-comuns";
    }

    /// <summary>F1 nesta janela abre a Ajuda no assunto dela.</summary>
    public static void AttachF1(Avalonia.Controls.Window window, string topic) =>
        window.KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.F1) return;
            Open(topic);
            e.Handled = true;
        };

    private static string SourceDir => Path.Combine(AppContext.BaseDirectory, "ajuda");

    private static string OutputDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "daRosa Trafego", "ajuda");

    /// <summary>Abre a Ajuda no navegador, na seção do assunto (ou no começo).</summary>
    /// <returns>Mensagem de erro, ou null se abriu.</returns>
    public static string? Open(string? topic = null)
    {
        try
        {
            var (index, ids) = Build();
            var anchor = topic is null ? null : Find(ids, topic);
            var target = index;
            if (anchor is not null)
            {
                // O Windows descarta o "#âncora" ao abrir um arquivo local: uma página de passagem leva até ela.
                target = Path.Combine(OutputDir, "abrir.html");
                File.WriteAllText(target,
                    $"<!doctype html><meta charset=\"utf-8\"><script>location.replace('index.html#'+{JsString(anchor)})</script>",
                    Encoding.UTF8);
            }
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex)
        {
            return "Não foi possível abrir a Ajuda: " + ex.Message;
        }
    }

    /// <summary>Gera a página se o manual mudou. Devolve o caminho dela e os ids das seções, na ordem.</summary>
    public static (string Index, List<string> Ids) Build(string? sourceDir = null, string? outputDir = null)
    {
        sourceDir ??= SourceDir;
        outputDir ??= OutputDir;
        var manual = Path.Combine(sourceDir, "MANUAL.md");
        if (!File.Exists(manual)) throw new FileNotFoundException("manual não encontrado", manual);
        var markdown = File.ReadAllText(manual, Encoding.UTF8);
        var (html, ids) = Render(markdown, Brand.Version);

        Directory.CreateDirectory(outputDir);
        var index = Path.Combine(outputDir, "index.html");
        if (!File.Exists(index) || File.ReadAllText(index, Encoding.UTF8) != html) File.WriteAllText(index, html, Encoding.UTF8);
        CopyIfNewer(Path.Combine(sourceDir, "rosa.png"), Path.Combine(outputDir, "rosa.png"));
        var img = Path.Combine(sourceDir, "img");
        if (Directory.Exists(img))
            foreach (var f in Directory.GetFiles(img))
                CopyIfNewer(f, Path.Combine(outputDir, "img", Path.GetFileName(f)));
        return (index, ids);
    }

    /// <summary>Primeira seção cujo id (sem o número da seção) começa pelo assunto.</summary>
    public static string? Find(IEnumerable<string> ids, string topic) =>
        ids.FirstOrDefault(id => Regex.Replace(id, @"^\d+-", "").StartsWith(topic, StringComparison.Ordinal))
        ?? ids.FirstOrDefault(id => id.Contains(topic, StringComparison.Ordinal));

    /// <summary>Manual em Markdown → página HTML completa (modelo embutido) e os ids das seções.</summary>
    public static (string Html, List<string> Ids) Render(string markdown, string version)
    {
        var pipeline = new MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseAutoIdentifiers(Markdig.Extensions.AutoIdentifiers.AutoIdentifierOptions.GitHub)
            .Build();
        var doc = Markdown.Parse(markdown, pipeline);

        var ids = new List<string>();
        var toc = new StringBuilder();
        string? title = null;
        foreach (var h in doc.Descendants<HeadingBlock>())
        {
            var text = Plain(h);
            if (h.Level == 1)
            {
                title ??= text;
                continue;
            }
            var id = h.GetAttributes().Id ?? "";
            ids.Add(id);
            if (h.Level == 2 && !text.Equals("Sumário", StringComparison.OrdinalIgnoreCase))
                toc.Append($"<a href=\"#{WebUtility.HtmlEncode(id)}\">{WebUtility.HtmlEncode(text)}</a>\n");
        }
        // O sumário do próprio Markdown (para quem lê no GitHub) sai da página: o lateral faz esse papel.
        var body = Regex.Replace(doc.ToHtml(pipeline),
            @"<h2 id=""sumário"">Sumário</h2>\s*<ol>.*?</ol>\s*(<hr\s*/>)?", "", RegexOptions.Singleline);
        // Cada seção (h2) vira um bloco, para a busca poder esconder as que não têm o termo.
        body = Regex.Replace(body, @"<hr\s*/>\s*", "");
        var parts = Regex.Split(body, @"(?=<h2 )");
        var sections = new StringBuilder();
        foreach (var p in parts)
            sections.Append(p.StartsWith("<h2 ", StringComparison.Ordinal) ? $"<section>{p}</section>\n" : $"<header class=\"intro\">{p}</header>\n");

        var template = Template();
        var html = template
            .Replace("{{title}}", WebUtility.HtmlEncode(title ?? "Manual"))
            .Replace("{{version}}", WebUtility.HtmlEncode(version))
            .Replace("{{toc}}", toc.ToString())
            .Replace("{{content}}", sections.ToString());
        return (html, ids);
    }

    private static string Plain(HeadingBlock h)
    {
        var sb = new StringBuilder();
        if (h.Inline is not null)
            foreach (var lit in h.Inline.Descendants<Markdig.Syntax.Inlines.LiteralInline>())
                sb.Append(lit.Content.ToString());
        return sb.ToString().Trim();
    }

    private static string Template()
    {
        using var s = typeof(HelpPages).Assembly.GetManifestResourceStream("Trafego.App.Help.ajuda.html")
            ?? throw new InvalidOperationException("modelo da Ajuda não encontrado");
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    private static void CopyIfNewer(string from, string to)
    {
        if (!File.Exists(from)) return;
        if (File.Exists(to) && File.GetLastWriteTimeUtc(to) >= File.GetLastWriteTimeUtc(from)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, overwrite: true);
    }

    private static string JsString(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
}
