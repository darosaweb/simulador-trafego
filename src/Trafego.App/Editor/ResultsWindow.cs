using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Trafego.Core.Model;
using Trafego.Core.Results;

namespace Trafego.App.Editor;

/// <summary>
/// Resultados das avaliações: indicadores da rede, medições por intervalo, links (com cores no mapa)
/// e comparação lado a lado entre conjuntos — inclusive de outros projetos (ex.: rotatória × DDI).
/// </summary>
public sealed class ResultsWindow : Window
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9aa4b2"));
    private static readonly IBrush Better = new SolidColorBrush(Color.Parse("#2ecc71"));
    private static readonly IBrush Worse = new SolidColorBrush(Color.Parse("#ff6b6b"));

    private readonly EditorState _editor;
    private readonly string _folder;
    private readonly List<(string Path, ResultSet Set)> _sets = [];
    private readonly List<string> _extraFiles = [];
    private readonly HashSet<string> _compare = [];
    private readonly ComboBox _picker = new() { Width = 460 };
    private readonly TabControl _tabs = new();
    private string? _current;
    private bool _building;

    public ResultsWindow(EditorState editor, string folder, string? select)
    {
        _editor = editor;
        _folder = folder;
        Title = "Resultados — Tráfego";
        Width = 1100;
        Height = 760;
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _picker.SelectionChanged += (_, _) =>
        {
            if (_building || _picker.SelectedItem is not ComboBoxItem { Tag: string path }) return;
            _current = path;
            Dispatcher.UIThread.Post(BuildTabs);
        };

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 10) };
        top.Children.Add(new TextBlock { Text = "Resultado", VerticalAlignment = VerticalAlignment.Center });
        top.Children.Add(_picker);
        top.Children.Add(Button("Abrir de outro projeto…", OpenFile));
        top.Children.Add(Button("Exportar CSV…", ExportCsv));
        top.Children.Add(Button("Excluir", DeleteCurrent));

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(_tabs);
        Content = root;

        Closed += (_, _) => _editor.SetLinkOverlay(null);
        Reload(select);
    }

    /// <summary>Relê a pasta de resultados (e os arquivos abertos à parte) e seleciona um deles.</summary>
    public void Reload(string? select)
    {
        _sets.Clear();
        var files = Directory.Exists(_folder) ? Directory.GetFiles(_folder, "*" + ResultSet.Extension) : [];
        foreach (var f in files.Concat(_extraFiles).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { _sets.Add((f, ResultSet.Load(f))); }
            catch (Exception) { /* arquivo de outra versão ou corrompido: fica de fora */ }
        }
        _sets.Sort((a, b) => b.Set.CreatedAt.CompareTo(a.Set.CreatedAt));
        _current = select ?? (_current is not null && _sets.Any(s => s.Path == _current) ? _current : _sets.FirstOrDefault().Path);
        if (_current is not null) _compare.Add(_current);

        _building = true;
        try
        {
            _picker.Items.Clear();
            foreach (var (path, set) in _sets)
            {
                var item = new ComboBoxItem { Content = Label(set, path), Tag = path };
                _picker.Items.Add(item);
                if (path == _current) _picker.SelectedItem = item;
            }
        }
        finally
        {
            _building = false;
        }
        BuildTabs();
    }

    private static string Label(ResultSet set, string path)
    {
        var name = string.IsNullOrWhiteSpace(set.Name) ? Path.GetFileName(path) : set.Name;
        return set.Partial ? name + " (parcial)" : name;
    }

    private ResultSet? Current => _sets.FirstOrDefault(s => s.Path == _current).Set;

    private void BuildTabs()
    {
        var selected = _tabs.SelectedIndex;
        _tabs.Items.Clear();
        if (Current is not { } set)
        {
            _tabs.Items.Add(new TabItem
            {
                Header = "Resultados",
                Content = Note("Nenhum resultado ainda. Clique em \"Avaliar\" na barra de ferramentas, ou rode a simulação animada e clique em Parar."),
            });
            return;
        }
        _tabs.Items.Add(new TabItem { Header = "Rede", Content = Scroll(NetworkTab(set)) });
        _tabs.Items.Add(new TabItem { Header = "Medições", Content = Scroll(MeasurementsTab(set)) });
        _tabs.Items.Add(new TabItem { Header = "Links", Content = Scroll(LinksTab(set)) });
        _tabs.Items.Add(new TabItem { Header = "Comparar", Content = Scroll(CompareTab()) });
        _tabs.SelectedIndex = Math.Clamp(selected, 0, 3);
    }

    // ------------------------------------------------------------------ abas

    private Control NetworkTab(ResultSet set)
    {
        var panel = Panel();
        var s = set.Settings;
        panel.Children.Add(Note(
            $"Período: {SimulationSettings.Clock(s.Begin)} a {SimulationSettings.Clock(s.End)} · " +
            $"{set.Runs.Count} replicação(ões), sementes {string.Join(", ", set.Seeds)} · gerado em {set.CreatedAt:dd/MM/yyyy HH:mm}" +
            (set.Partial ? " · simulação interrompida antes do fim" : "")));
        var multi = set.Runs.Count > 1;
        var headers = new List<string> { "Indicador", "Média" };
        if (multi) headers.Add("Desvio-padrão");
        headers.AddRange(set.Runs.Select(r => $"Semente {r.Seed}"));
        var rows = NetworkKpis.Fields.Select(f =>
        {
            var st = set.Kpi(f.Key);
            var row = new List<string> { $"{f.Label}{(f.Unit.Length > 0 ? $" ({f.Unit})" : "")}", N(st.Mean) };
            if (multi) row.Add(N(st.StdDev));
            row.AddRange(set.Runs.Select(r => N(r.Kpis.Get(f.Key))));
            return row;
        });
        panel.Children.Add(Table(headers, rows));
        panel.Children.Add(Note("Atraso: tempo perdido em relação a andar na velocidade desejada. Tempo parado: com velocidade " +
                                "abaixo de 0,1 m/s. Veículos sem conseguir entrar indicam fila na entrada da rede (demanda acima da capacidade). " +
                                "Teleportes indicam travamentos que o SUMO desfez tirando o veículo do lugar."));
        return panel;
    }

    private Control MeasurementsTab(ResultSet set)
    {
        var panel = Panel();
        var ms = set.MeanMeasurements();
        if (ms.Count == 0)
        {
            panel.Children.Add(Note("Nenhuma medição. Use a ferramenta Medição (M) no mapa: contagem, fila ou tempo de viagem."));
            return panel;
        }
        if (set.Runs.Count > 1) panel.Children.Add(Note($"Médias de {set.Runs.Count} replicações."));
        foreach (var m in ms)
        {
            panel.Children.Add(Header($"{m.Name} · {Measurement.KindName(m.Kind)}"));
            var fields = MeasurementResult.FieldsOf(m.Kind);
            var rows = m.Intervals.Select(i => (IEnumerable<string>)
                [$"{SimulationSettings.Clock(i.Begin)[..5]}–{SimulationSettings.Clock(i.End)[..5]}", .. fields.Select(f => N(i.Values.GetValueOrDefault(f.Key, double.NaN)))])
                .Append(["Período todo", .. fields.Select(f => N(m.Total.GetValueOrDefault(f.Key, double.NaN)))]);
            panel.Children.Add(Table(["Intervalo", .. fields.Select(f => f.Label)], rows, boldLast: true));
        }
        return panel;
    }

    private Control LinksTab(ResultSet set)
    {
        var panel = Panel();
        var links = set.MeanLinks();
        panel.Children.Add(Row(
            Button("Colorir o mapa pela velocidade", () =>
                _editor.SetLinkOverlay(links.Where(l => double.IsFinite(l.SpeedRatio)).ToDictionary(l => l.LinkId, l => l.SpeedRatio))),
            Button("Cores normais", () => _editor.SetLinkOverlay(null))));
        panel.Children.Add(Note("No mapa: verde na velocidade máxima do link, amarelo pela metade, vermelho parado."));
        panel.Children.Add(Table(
            ["Link", "Volume (veíc/h)", "Velocidade média (km/h)", "Velocidade / máxima", "Atraso (s/veíc)", "Densidade (veíc/km/faixa)"],
            links.OrderBy(l => l.SpeedRatio).Select(l => (IEnumerable<string>)
                [l.Name, N(l.VolumeVph, 0), N(l.MeanSpeedKmh), double.IsFinite(l.SpeedRatio) ? $"{l.SpeedRatio:P0}" : "—", N(l.DelayPerVehicle), N(l.Density)])));
        return panel;
    }

    private Control CompareTab()
    {
        var panel = Panel();
        panel.Children.Add(Note("Marque os resultados a comparar. O primeiro marcado é a referência; as colunas Δ mostram a diferença em relação a ele. " +
                                "Para comparar com outro projeto (outra alternativa de projeto), use \"Abrir de outro projeto…\"."));
        foreach (var (path, set) in _sets)
        {
            var box = new CheckBox { Content = Label(set, path), IsChecked = _compare.Contains(path) };
            box.IsCheckedChanged += (_, _) =>
            {
                if (box.IsChecked == true) _compare.Add(path);
                else _compare.Remove(path);
                Dispatcher.UIThread.Post(BuildTabs);
            };
            panel.Children.Add(box);
        }

        var chosen = _sets.Where(s => _compare.Contains(s.Path)).Reverse().ToList(); // mais antigo primeiro
        if (chosen.Count < 2)
        {
            panel.Children.Add(Note("Marque pelo menos dois resultados."));
            return panel;
        }

        var headers = new List<string> { "Indicador" };
        for (var i = 0; i < chosen.Count; i++)
        {
            headers.Add(Label(chosen[i].Set, chosen[i].Path));
            if (i > 0) headers.Add("Δ");
        }

        var rows = new List<(string Label, Func<ResultSet, double> Value, bool LowerIsBetter)>();
        foreach (var (key, label, unit) in NetworkKpis.Fields)
            rows.Add(($"{label}{(unit.Length > 0 ? $" ({unit})" : "")}", s => s.Kpi(key).Mean,
                key is not (nameof(NetworkKpis.Completed) or nameof(NetworkKpis.MeanSpeedKmh))));
        // Medições com o mesmo nome nos dois cenários comparam entre si (ex.: "Fila aproximação norte").
        var names = chosen.SelectMany(c => c.Set.MeanMeasurements().Select(m => (m.Name, m.Kind))).Distinct();
        foreach (var (name, kind) in names)
            foreach (var (key, label) in MeasurementResult.FieldsOf(kind))
                rows.Add(($"{name}: {label}",
                    s => s.MeanMeasurements().FirstOrDefault(m => m.Name == name && m.Kind == kind)?.Total.GetValueOrDefault(key, double.NaN) ?? double.NaN,
                    !(key is "volume" or "vehicles" or "speed")));

        var grid = NewGrid(headers.Count, rows.Count + 1);
        for (var c = 0; c < headers.Count; c++) Put(grid, 0, c, Bold(headers[c], maxWidth: 200));
        for (var r = 0; r < rows.Count; r++)
        {
            var (label, value, lowerIsBetter) = rows[r];
            Put(grid, r + 1, 0, Cell(label));
            var reference = value(chosen[0].Set);
            var col = 1;
            for (var i = 0; i < chosen.Count; i++)
            {
                var v = value(chosen[i].Set);
                Put(grid, r + 1, col++, Cell(N(v)));
                if (i == 0) continue;
                var delta = Cell(Delta(reference, v));
                if (double.IsFinite(reference) && double.IsFinite(v) && Math.Abs(v - reference) > 1e-9)
                    delta.Foreground = (v < reference) == lowerIsBetter ? Better : Worse;
                Put(grid, r + 1, col++, delta);
            }
        }
        panel.Children.Add(grid);
        panel.Children.Add(Note("Verde: melhor que a referência; vermelho: pior (menos atraso, fila e tempo é melhor; mais veículos atendidos e velocidade é melhor). " +
                                "Com poucas replicações, diferenças menores que o desvio-padrão podem ser só acaso."));
        return panel;
    }

    private static string Delta(double reference, double value)
    {
        if (!double.IsFinite(reference) || !double.IsFinite(value)) return "—";
        var diff = value - reference;
        var pct = Math.Abs(reference) > 1e-9 ? $" ({diff / Math.Abs(reference):+0%;-0%;0%})" : "";
        return diff.ToString("+0.0;-0.0;0", Pt) + pct;
    }

    // ------------------------------------------------------------------ arquivos

    private async void OpenFile()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Abrir resultado",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Resultados") { Patterns = ["*" + ResultSet.Extension] }],
        });
        string? last = null;
        foreach (var f in files)
            if (f.TryGetLocalPath() is { } path)
            {
                _extraFiles.Add(path);
                _compare.Add(path);
                last = path;
            }
        if (last is not null) Reload(last);
    }

    private void DeleteCurrent()
    {
        if (_current is null) return;
        if (_extraFiles.Remove(_current)) { /* só tira da lista: o arquivo é de outro projeto */ }
        else if (File.Exists(_current)) File.Delete(_current);
        _compare.Remove(_current);
        _current = null;
        Reload(null);
    }

    /// <summary>CSV com ponto e vírgula e vírgula decimal, que o Excel em português abre direto.</summary>
    private async void ExportCsv()
    {
        if (Current is not { } set) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Exportar resultados",
            SuggestedFileName = $"resultados-{set.CreatedAt:yyyyMMdd-HHmm}.csv",
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;

        var sb = new StringBuilder();
        void Line(params string[] cells) => sb.AppendLine(string.Join(";", cells.Select(c => c.Contains(';') ? $"\"{c}\"" : c)));
        string V(double v) => double.IsFinite(v) ? v.ToString("0.###", Pt) : "";

        Line("Resultado", Label(set, path));
        Line(["Indicador", "Média", "Desvio-padrão", .. set.Runs.Select(r => $"Semente {r.Seed}")]);
        foreach (var (key, label, unit) in NetworkKpis.Fields)
        {
            var st = set.Kpi(key);
            Line([$"{label} ({unit})", V(st.Mean), V(st.StdDev), .. set.Runs.Select(r => V(r.Kpis.Get(key)))]);
        }
        foreach (var m in set.MeanMeasurements())
        {
            sb.AppendLine();
            var fields = MeasurementResult.FieldsOf(m.Kind);
            Line($"{m.Name} ({Measurement.KindName(m.Kind)})");
            Line(["Início", "Fim", .. fields.Select(f => f.Label)]);
            foreach (var i in m.Intervals)
                Line([SimulationSettings.Clock(i.Begin), SimulationSettings.Clock(i.End), .. fields.Select(f => V(i.Values.GetValueOrDefault(f.Key, double.NaN)))]);
            Line(["Total", "", .. fields.Select(f => V(m.Total.GetValueOrDefault(f.Key, double.NaN)))]);
        }
        sb.AppendLine();
        Line("Link", "Volume (veíc/h)", "Velocidade média (km/h)", "Velocidade/máxima", "Atraso (s/veíc)", "Densidade (veíc/km/faixa)");
        foreach (var l in set.MeanLinks())
            Line(l.Name, V(l.VolumeVph), V(l.MeanSpeedKmh), V(l.SpeedRatio), V(l.DelayPerVehicle), V(l.Density));

        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    // ------------------------------------------------------------------ peças

    private static string N(double v, int decimals = 1) =>
        double.IsFinite(v) ? v.ToString(decimals == 0 ? "N0" : "N1", Pt) : "—";

    private static StackPanel Panel() => new() { Margin = new Thickness(16, 12, 16, 16), Spacing = 6 };

    private static ScrollViewer Scroll(Control c) => new() { Content = c, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };

    private static TextBlock Note(string text) => new()
    {
        Text = text, Foreground = Muted, TextWrapping = TextWrapping.Wrap, MaxWidth = 960, HorizontalAlignment = HorizontalAlignment.Left,
    };

    private static TextBlock Header(string text) => new()
    {
        Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 12, 0, 2),
    };

    private static TextBlock Bold(string text, double maxWidth = double.PositiveInfinity) => new()
    {
        Text = text, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = maxWidth,
        VerticalAlignment = VerticalAlignment.Bottom,
    };

    private static TextBlock Cell(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };

    private static StackPanel Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var c in children) row.Children.Add(c);
        return row;
    }

    private static Button Button(string text, Action onClick)
    {
        var b = new Button { Content = text, VerticalAlignment = VerticalAlignment.Center };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static Grid NewGrid(int columns, int rows)
    {
        var g = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        for (var c = 0; c < columns; c++) g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (var r = 0; r < rows; r++) g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        return g;
    }

    private static void Put(Grid grid, int row, int column, Control c)
    {
        Grid.SetRow(c, row);
        Grid.SetColumn(c, column);
        c.Margin = new Thickness(0, 2, 18, 2);
        grid.Children.Add(c);
    }

    private static Grid Table(IReadOnlyList<string> headers, IEnumerable<IEnumerable<string>> rows, bool boldLast = false)
    {
        var data = rows.Select(r => r.ToList()).ToList();
        var grid = NewGrid(headers.Count, data.Count + 1);
        for (var c = 0; c < headers.Count; c++) Put(grid, 0, c, Bold(headers[c]));
        for (var r = 0; r < data.Count; r++)
            for (var c = 0; c < data[r].Count && c < headers.Count; c++)
                Put(grid, r + 1, c, boldLast && r == data.Count - 1 ? Bold(data[r][c]) : Cell(data[r][c]));
        return grid;
    }
}
