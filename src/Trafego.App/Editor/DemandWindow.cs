using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Trafego.Core.Model;

namespace Trafego.App.Editor;

/// <summary>
/// Edição da demanda: intervalos de tempo, volume por entrada, matriz O/D e participação das rotas.
/// Montada em código porque as tabelas mudam com a quantidade de entradas, saídas e intervalos.
/// </summary>
public sealed class DemandWindow : Window
{
    private readonly EditorState _editor;
    private readonly ContentControl _content = new();
    private double? _matrixSlice;
    private (int Origin, int Destination)? _pair;
    private bool _building;
    private bool _rebuildOnBlur;
    /// <summary>Resumo da última importação de contagens, mostrado acima da tabela de volumes.</summary>
    private string? _importNote;
    /// <summary>Pares O/D quando a janela foi montada: se mudarem, a lista de rotas precisa ser refeita.</summary>
    private List<(int Origin, int Destination)> _builtPairs = [];

    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9aa4b2"));

    public DemandWindow(EditorState editor)
    {
        _editor = editor;
        Title = "Demanda — " + Brand.Name;
        Help.HelpPages.AttachF1(this, Help.HelpPages.Topic.Demand);
        Width = 1000;
        Height = 760;
        MinWidth = 700;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new ScrollViewer
        {
            Content = _content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _editor.Changed += OnEditorChanged;
        Closed += (_, _) =>
        {
            _editor.Changed -= OnEditorChanged;
            _editor.SetHighlight(null);
        };
        Build();
    }

    private Network Net => _editor.Network;
    private DemandSettings Demand => Net.Demand;

    private void OnEditorChanged()
    {
        if (!_building) Build();
    }

    private void Commit(bool rebuild = false)
    {
        _building = true;
        try { _editor.Checkpoint(); }
        finally { _building = false; }
        if (rebuild) Dispatcher.UIThread.Post(Build);
    }

    private void Build()
    {
        _building = true;
        try
        {
            IsEnabled = !_editor.ReadOnly;
            var slices = Demand.SortedSlices();
            if (_matrixSlice is not { } ms || !slices.Contains(ms)) _matrixSlice = slices[0];
            _builtPairs = Demand.Pairs();

            var panel = new StackPanel { Margin = new Thickness(16, 12, 16, 16), Spacing = 6 };
            BuildSlices(panel, slices);
            BuildVolumes(panel, slices);
            BuildMatrix(panel, slices);
            BuildRoutes(panel);
            _content.Content = panel;
        }
        finally
        {
            _building = false;
        }
    }

    // ------------------------------------------------------------------ intervalos

    private void BuildSlices(StackPanel panel, List<double> slices)
    {
        panel.Children.Add(Header("INTERVALOS DE TEMPO"));
        panel.Children.Add(Note("Cada intervalo vai do seu horário até o horário do seguinte, todos os dias. " +
                                "Volumes e matriz são definidos por intervalo; a simulação usa o intervalo em vigor no relógio."));
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var s in slices)
        {
            var box = new TextBox { Text = SignalWindow.FormatTime(s), Width = 70 };
            box.LostFocus += (_, _) =>
            {
                if (SignalWindow.TryParseTime(box.Text, out var v) && Math.Abs(v - s) > 1 && !slices.Any(x => Math.Abs(x - v) < 1))
                {
                    Demand.MoveSlice(s, v, Net);
                    Commit(rebuild: true);
                }
                else box.Text = SignalWindow.FormatTime(s);
            };
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 0, 12, 4) };
            item.Children.Add(box);
            if (slices.Count > 1)
                item.Children.Add(Button("×", () => { Demand.RemoveSlice(s, Net); Commit(rebuild: true); }));
            row.Children.Add(item);
        }
        row.Children.Add(Button("+ Intervalo", () =>
        {
            var next = slices.Max() + 15 * 60;
            if (next >= SignalController.Day) return;
            Demand.SliceStarts.Add(next);
            Commit(rebuild: true);
        }));
        panel.Children.Add(row);
    }

    // ------------------------------------------------------------------ volumes por entrada

    private void BuildVolumes(StackPanel panel, List<double> slices)
    {
        panel.Children.Add(Header("VOLUME POR ENTRADA (veíc/h)"));
        panel.Children.Add(Note("Para entradas sem matriz O/D: os veículos entram com este volume e, em cada escolha, as opções têm a mesma chance."));
        panel.Children.Add(Row(Button("Importar contagens…", ImportCounts),
            Note("CSV do Contador de Vias: cada via com o mesmo nome de uma entrada preenche o volume e o % de pesados dela.")));
        if (_importNote is not null) panel.Children.Add(new TextBlock { Text = _importNote, TextWrapping = TextWrapping.Wrap, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left });
        if (Net.Inputs.Count == 0)
        {
            panel.Children.Add(Note("Nenhuma entrada. Use a ferramenta Entrada (E) no mapa."));
            return;
        }
        var grid = Table(slices.Count + 1, Net.Inputs.Count + 1);
        Put(grid, 0, 0, Bold("Entrada"));
        for (var j = 0; j < slices.Count; j++) Put(grid, 0, j + 1, Bold(SignalWindow.FormatTime(slices[j])));
        for (var i = 0; i < Net.Inputs.Count; i++)
        {
            var input = Net.Inputs[i];
            Put(grid, i + 1, 0, Text(input.Name));
            if (Demand.UsesMatrix(input.Id))
            {
                var t = Text("usa a matriz O/D");
                t.Foreground = Muted;
                Put(grid, i + 1, 1, t);
                Grid.SetColumnSpan(t, slices.Count);
                continue;
            }
            for (var j = 0; j < slices.Count; j++)
            {
                var slice = slices[j];
                Put(grid, i + 1, j + 1, Number(input.VolumeAt(slice), v => { input.SetVolume(slice, v); Commit(); }));
            }
        }
        panel.Children.Add(grid);
    }

    private async void ImportCounts()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Importar contagens do Contador de Vias",
            FileTypeFilter = [new FilePickerFileType("CSV") { Patterns = ["*.csv", "*.txt"] }, FilePickerFileTypes.All],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        try
        {
            var samples = CountImport.Parse(await File.ReadAllTextAsync(path));
            var result = CountImport.Apply(Net, samples);
            _importNote = Describe(result, samples.Count);
            if (result.Applied.Count > 0) Commit();
        }
        catch (Exception ex)
        {
            _importNote = "Não foi possível importar: " + ex.Message.Split('\n')[0];
        }
        Dispatcher.UIThread.Post(Build);
    }

    private static string Describe(CountImportResult r, int samples)
    {
        if (samples == 0) return "Nenhuma contagem no arquivo.";
        var parts = new List<string>();
        if (r.Applied.Count == 0) parts.Add("Nenhuma via do arquivo tem o nome de uma entrada da rede: renomeie as entradas (no painel) ou as vias no contador.");
        else
        {
            parts.Add("Importado: " + string.Join("; ", r.Applied.Select(a =>
                $"{a.Input.Name} ({a.Samples} contage{(a.Samples > 1 ? "ns" : "m")}, {a.Slices} intervalo{(a.Slices > 1 ? "s" : "")}, {a.TruckShare * 100:0}% pesados" +
                (a.UsesMatrix ? ", mas usa a matriz O/D e o volume não vale" : "") + ")")) + ".");
            if (r.CreatedSlices > 0) parts.Add($"Criado(s) {r.CreatedSlices} intervalo(s) de uma hora.");
        }
        if (r.Unmatched.Count > 0) parts.Add("Sem entrada com o mesmo nome: " + string.Join(", ", r.Unmatched) + ".");
        return string.Join(" ", parts) + (r.Applied.Count > 0 ? " Ctrl+Z desfaz." : "");
    }

    // ------------------------------------------------------------------ matriz O/D

    private void BuildMatrix(StackPanel panel, List<double> slices)
    {
        panel.Children.Add(Header("MATRIZ O/D (veíc/h)"));
        panel.Children.Add(Note("Origens são as entradas; destinos são as saídas da rede (links que terminam sem conector). " +
                                "Uma entrada com algum valor na matriz manda cada veículo para o seu destino, pela rota escolhida abaixo."));
        var analysis = _editor.Analysis;
        var exits = RouteFinder.Exits(analysis.Topology);
        if (Net.Inputs.Count == 0 || exits.Count == 0)
        {
            panel.Children.Add(Note("A matriz precisa de entradas e de saídas."));
            return;
        }

        var sliceBox = new ComboBox { Width = 120 };
        foreach (var s in slices)
        {
            var item = new ComboBoxItem { Content = SignalWindow.FormatTime(s), Tag = s };
            sliceBox.Items.Add(item);
            if (Math.Abs(s - _matrixSlice!.Value) < 1e-6) sliceBox.SelectedItem = item;
        }
        sliceBox.SelectionChanged += (_, _) =>
        {
            if (_building || sliceBox.SelectedItem is not ComboBoxItem { Tag: double s }) return;
            _matrixSlice = s;
            Dispatcher.UIThread.Post(Build);
        };
        var slice = _matrixSlice!.Value;
        panel.Children.Add(Row(Text("Intervalo"), sliceBox, Button("Copiar este intervalo para todos", () =>
        {
            foreach (var input in Net.Inputs)
            foreach (var exit in exits)
            {
                var v = Demand.OdVolume(input.Id, exit.Id, slice);
                foreach (var other in slices) Demand.SetOdVolume(input.Id, exit.Id, other, v);
            }
            Commit(rebuild: true);
        })));

        var grid = Table(exits.Count + 2, Net.Inputs.Count + 1);
        Put(grid, 0, 0, Bold("Origem \\ Destino"));
        for (var j = 0; j < exits.Count; j++) Put(grid, 0, j + 1, Bold(exits[j].Name));
        Put(grid, 0, exits.Count + 1, Bold("Total"));
        for (var i = 0; i < Net.Inputs.Count; i++)
        {
            var input = Net.Inputs[i];
            var reachable = RouteFinder.ExitsFrom(analysis, input.LinkId).ToHashSet();
            var total = Text("");
            void UpdateTotal() => total.Text = $"{exits.Sum(x => Demand.OdVolume(input.Id, x.Id, slice)):0}";
            UpdateTotal();
            Put(grid, i + 1, 0, Text(input.Name));
            for (var j = 0; j < exits.Count; j++)
            {
                var exit = exits[j];
                if (!reachable.Contains(exit.Id))
                {
                    Put(grid, i + 1, j + 1, new TextBlock { Text = "sem caminho", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
                    continue;
                }
                var wasMatrix = Demand.UsesMatrix(input.Id);
                Put(grid, i + 1, j + 1, Number(Demand.OdVolume(input.Id, exit.Id, slice), v =>
                {
                    Demand.SetOdVolume(input.Id, exit.Id, slice, v);
                    UpdateTotal();
                    Commit();
                    // Mudou o modo da entrada (passou a usar a matriz ou deixou de usar) ou apareceu um par
                    // novo: as tabelas precisam ser refeitas, mas só quando o campo perder o foco — refazer
                    // agora jogaria o resto da digitação em outro campo.
                    _rebuildOnBlur |= wasMatrix != Demand.UsesMatrix(input.Id) || !Demand.Pairs().SequenceEqual(_builtPairs);
                }));
            }
            Put(grid, i + 1, exits.Count + 1, total);
        }
        panel.Children.Add(grid);
    }

    // ------------------------------------------------------------------ rotas

    private void BuildRoutes(StackPanel panel)
    {
        panel.Children.Add(Header("ROTAS"));
        panel.Children.Add(Note("Para cada par O/D, as até 3 rotas mais rápidas em fluxo livre. A participação divide os veículos do par " +
                                "entre elas (sem participação definida, todos vão pela mais rápida). \"Ver\" destaca a rota no mapa."));
        var pairs = Demand.Pairs();
        if (pairs.Count == 0)
        {
            panel.Children.Add(Note("Preencha a matriz O/D para escolher rotas."));
            return;
        }
        if (_pair is not { } current || !pairs.Contains(current)) _pair = pairs[0];

        var pairBox = new ComboBox { Width = 360 };
        foreach (var p in pairs)
        {
            var item = new ComboBoxItem { Content = $"{Validation.NameOf(Net, p.Origin)} → {Validation.NameOf(Net, p.Destination)}", Tag = p };
            pairBox.Items.Add(item);
            if (p == _pair) pairBox.SelectedItem = item;
        }
        pairBox.SelectionChanged += (_, _) =>
        {
            if (_building || pairBox.SelectedItem is not ComboBoxItem { Tag: ValueTuple<int, int> p }) return;
            _pair = p;
            _editor.SetHighlight(null);
            Dispatcher.UIThread.Post(Build);
        };
        panel.Children.Add(Row(Text("Par O/D"), pairBox));

        var (origin, dest) = _pair!.Value;
        if (Net.FindInput(origin) is not { } input) return;
        var alts = RouteFinder.Alternatives(_editor.Analysis, input.LinkId, dest);
        if (alts.Count == 0)
        {
            panel.Children.Add(Note("Não há caminho para este par."));
            return;
        }

        var choices = Demand.ChoicesOf(origin, dest);
        var shares = alts.Select(a => choices.FirstOrDefault(c => c.ConnectorIds.SequenceEqual(a.ConnectorIds))?.Share ?? 0).ToArray();
        if (shares.All(s => s <= 0)) shares[0] = 100; // padrão: todos pela mais rápida

        void Save()
        {
            Demand.RouteChoices.RemoveAll(c => c.OriginId == origin && c.DestinationLinkId == dest);
            for (var i = 0; i < alts.Count; i++)
                Demand.RouteChoices.Add(new RouteChoice { OriginId = origin, DestinationLinkId = dest, ConnectorIds = alts[i].ConnectorIds, Share = shares[i] });
            Commit();
        }

        var grid = Table(6, alts.Count + 1);
        string[] headers = ["Rota", "Passa por", "Comprimento", "Tempo livre", "Participação (%)", ""];
        for (var j = 0; j < headers.Length; j++) Put(grid, 0, j, Bold(headers[j]));
        for (var i = 0; i < alts.Count; i++)
        {
            var path = alts[i];
            var index = i;
            var via = path.Steps.OfType<LinkStep>().Select(s => s.LinkId).Distinct().Select(id => Validation.NameOf(Net, id));
            Put(grid, i + 1, 0, Text($"{i + 1}"));
            Put(grid, i + 1, 1, new TextBlock { Text = string.Join(" → ", via), MaxWidth = 380, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            Put(grid, i + 1, 2, Text($"{path.Length:0} m"));
            Put(grid, i + 1, 3, Text($"{path.FreeFlowTime:0} s"));
            Put(grid, i + 1, 4, Number(shares[i], v => { shares[index] = v; Save(); }, max: 100));
            Put(grid, i + 1, 5, Button("Ver", () => _editor.SetHighlight(path)));
        }
        panel.Children.Add(grid);
    }

    // ------------------------------------------------------------------ peças

    private static TextBlock Header(string text) => new()
    {
        Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Muted, Margin = new Thickness(0, 14, 0, 2),
    };

    private static TextBlock Note(string text) => new() { Text = text, Foreground = Muted, TextWrapping = TextWrapping.Wrap, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };

    private static TextBlock Text(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };

    private static TextBlock Bold(string text) => new()
    {
        Text = text, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 2),
    };

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

    private static Grid Table(int columns, int rows)
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
        c.Margin = new Thickness(0, 2, 8, 2);
        grid.Children.Add(c);
    }

    private NumericUpDown Number(double value, Action<double> changed, double max = 20000)
    {
        var n = new NumericUpDown
        {
            Value = (decimal)value, Minimum = 0, Maximum = (decimal)max, Increment = 50, FormatString = "0", Width = 120,
        };
        if (max <= 100) n.Increment = 5;
        n.ValueChanged += (_, e) =>
        {
            if (_building || e.NewValue is not { } v) return;
            changed((double)v);
        };
        n.LostFocus += (_, _) =>
        {
            if (!_rebuildOnBlur) return;
            _rebuildOnBlur = false;
            Dispatcher.UIThread.Post(Build);
        };
        return n;
    }
}
