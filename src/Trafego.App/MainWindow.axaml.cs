using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Trafego.App.Editor;
using Trafego.App.Help;
using Trafego.Core.Geometry;
using Trafego.Core.Model;
using Trafego.Core.Results;
using Trafego.Core.Serialization;
using Trafego.Sumo;

namespace Trafego.App;

public partial class MainWindow : Window
{
    private readonly EditorState _editor = new();
    private readonly SimulationController _sim = new();
    private bool _refreshing;
    private bool _closeConfirmed;
    private (Vec2 A, Vec2 B)? _pendingCalibration;

    private static readonly FilePickerFileType ProjectType = new("Projeto de tráfego") { Patterns = ["*" + ProjectFile.Extension] };
    private static readonly FilePickerFileType PackageType = new("Pacote de projeto") { Patterns = ["*" + ProjectPackage.Extension, "*.zip"] };
    private static readonly FilePickerFileType ImageType = new("Imagens") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp"] };

    public MainWindow() : this([]) { }

    public MainWindow(string[] args)
    {
        InitializeComponent();
        MapCanvas.Status += SetStatus;
        MapCanvas.CursorMoved += p => CursorText.Text =
            string.Create(CultureInfo.CurrentCulture, $"x {p.X:0.0} m   y {p.Y:0.0} m   {MapCanvas.Zoom:0.00} px/m");
        MapCanvas.CalibrationPicked += OnCalibrationPicked;
        MapCanvas.Editor = _editor;
        _editor.Changed += Refresh;
        _editor.ToolChanged += RefreshTools;
        _sim.StateChanged += OnSimStateChanged;
        _sim.ResultsReady += set =>
        {
            var p = _editor.Project;
            set.Name = $"{Titled(ProjectName, p, p.Active)} · {set.CreatedAt:dd/MM HH:mm:ss} · animação{(set.Partial ? " (interrompida)" : "")}";
            set.ProjectName = ProjectName;
            set.ScenarioName = p.Active.Name;
            var path = SaveResult(set);
            SetStatus($"Resultados da simulação animada salvos (Rede → Resultados): {path}");
            _resultsWindow?.Reload(path);
        };
        _sim.Updated += snap =>
        {
            MapCanvas.Snapshot = _sim.Active ? snap : null;
            FillSimPanel();
        };

        NewLanes.ValueChanged += (_, e) => _editor.NewLinkLanes = (int)(e.NewValue ?? 1);
        NewWidth.ValueChanged += (_, e) => _editor.NewLinkLaneWidth = (double)(e.NewValue ?? 3.5m);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        if (args.Length > 0 && File.Exists(args[0]))
        {
            if (ProjectPackage.IsPackage(args[0])) Opened += (_, _) => _ = OpenPackageAsync(args[0]);
            else TryOpen(args[0]);
        }

        RefreshTools();
        Refresh();
        SetStatus("Pronto. Escolha a ferramenta Link (L) para desenhar o primeiro trecho.");
    }

    private void SetStatus(string msg) => StatusText.Text = msg;

    // ---------------------------------------------------------------- atualização da tela

    private void RefreshTools()
    {
        ToolSelect.IsChecked = _editor.Tool == Tool.Select;
        ToolLink.IsChecked = _editor.Tool == Tool.Link;
        ToolConnector.IsChecked = _editor.Tool == Tool.Connector;
        ToolInput.IsChecked = _editor.Tool == Tool.Input;
        ToolMeasure.IsChecked = _editor.Tool == Tool.Measure;
        ToolCalibrate.IsChecked = _editor.Tool == Tool.Calibrate;
        if (_editor.Tool != Tool.Calibrate) HideCalibrationBar();
    }

    private void Refresh()
    {
        _refreshing = true;
        try
        {
            var name = _editor.FilePath is { } p ? Path.GetFileName(p) : "sem título";
            Title = $"{name}{(_editor.Dirty ? " •" : "")} — {Brand.Name}";
            UndoItem.IsEnabled = _editor.CanUndo;
            RedoItem.IsEnabled = _editor.CanRedo;
            var sim = _editor.Project.Simulation;
            if (!SimStart.IsFocused) SimStart.Text = SignalWindow.FormatTime(sim.StartTimeOfDay);
            SimMinutes.Value = (decimal)sim.DurationMinutes;
            EvalReplications.Value = sim.Replications;
            FillScenarios();

            var link = _editor.SelectedLink;
            var conn = _editor.SelectedConnector;
            var input = _editor.SelectedInput;
            var conflict = _editor.SelectedConflictInfo;
            var junction = _editor.SelectedJunction;
            var measurement = _editor.SelectedMeasurement;
            MeasurePanel.IsVisible = measurement is not null;
            LinkPanel.IsVisible = link is not null;
            ConnectorPanel.IsVisible = conn is not null;
            InputPanel.IsVisible = input is not null;
            ConflictPanel.IsVisible = conflict is not null;
            JunctionPanel.IsVisible = junction is not null;
            NetworkPanel.IsVisible = link is null && conn is null && input is null && conflict is null && junction is null && measurement is null;
            LinkPanel.IsEnabled = ConnectorPanel.IsEnabled = InputPanel.IsEnabled =
                ConflictPanel.IsEnabled = JunctionPanel.IsEnabled = MeasurePanel.IsEnabled = !_editor.ReadOnly;

            if (link is not null) FillLink(link);
            else if (conn is not null) FillConnector(conn);
            else if (input is not null) FillInput(input);
            else if (conflict is not null) FillConflict(conflict);
            else if (junction is not null) FillJunction(junction);
            else if (measurement is not null) FillMeasurement(measurement);
            else FillNetwork();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void FillLink(Link link)
    {
        if (!LinkName.IsFocused) LinkName.Text = link.Name;
        LinkLanes.Value = link.Lanes.Count;
        LinkWidth.Value = (decimal)link.Lanes[0].Width;
        LinkSpeed.Value = (decimal)link.SpeedKmh;
        LinkSmooth.IsChecked = link.Smooth;
        var outs = _editor.Network.Connectors.Count(c => c.FromLinkId == link.Id);
        var ins = _editor.Network.Connectors.Count(c => c.ToLinkId == link.Id);
        LinkInfo.Text = $"Id {link.Id} · {link.Axis().Length:0.0} m · {link.Points.Count} pontos\n" +
                        $"Conectores: {ins} chegando, {outs} saindo";
    }

    private void FillConnector(Connector c)
    {
        var net = _editor.Network;
        var from = net.FindLink(c.FromLinkId);
        var to = net.FindLink(c.ToLinkId);
        if (!ConnName.IsFocused) ConnName.Text = c.Name;
        ConnLanes.Maximum = Math.Max(1, Math.Min(from?.Lanes.Count ?? 1, to?.Lanes.Count ?? 1));
        ConnLanes.Value = c.LaneCount;
        ConnFromName.Text = $"DE: {from?.Name ?? "?"}";
        ConnFromLane.Maximum = Math.Max(1, (from?.Lanes.Count ?? 1) - c.LaneCount + 1);
        ConnFromLane.Value = c.FromLane + 1;
        ConnFromS.Maximum = (decimal)(from?.Axis().Length ?? 0);
        ConnFromS.Value = (decimal)c.FromS;
        ConnToName.Text = $"PARA: {to?.Name ?? "?"}";
        ConnToLane.Maximum = Math.Max(1, (to?.Lanes.Count ?? 1) - c.LaneCount + 1);
        ConnToLane.Value = c.ToLane + 1;
        ConnToS.Maximum = (decimal)(to?.Axis().Length ?? 0);
        ConnToS.Value = (decimal)c.ToS;
        var len = net.GeometryOf(c)?.Axis.Length;
        SetItems(ConnSignal, [(0, "Nenhum"), .. net.SignalControllers.Select(sc => (sc.Id, sc.Name))], c.Signal?.ControllerId ?? 0);
        var ctrl = c.Signal is { } sig ? net.FindController(sig.ControllerId) : null;
        SetItems(ConnGroup, ctrl?.Groups.Select(g => (g.Id, $"G{g.Id} · {g.Name}")).ToList() ?? [], c.Signal?.GroupId ?? -1);
        ConnGroup.IsEnabled = c.Signal is not null;

        // Divisão do tráfego no ponto de onde o conector sai.
        var topo = _editor.Analysis.Topology;
        var split = topo.Covers(c) ? RouteFinder.SplitAt(topo, topo.FromNode(c)) : [];
        var splitTruck = topo.Covers(c) ? RouteFinder.SplitAt(topo, topo.FromNode(c), VehicleClass.Truck) : [];
        var mine = split.FirstOrDefault(s => s.ConnectorId == c.Id).Share;
        var mineTruck = splitTruck.FirstOrDefault(s => s.ConnectorId == c.Id).Share;
        ConnSplitOn.IsChecked = c.SplitPercent is not null;
        ConnSplit.IsEnabled = c.SplitPercent is not null;
        ConnSplit.Value = (decimal)Math.Round(c.SplitPercent ?? mine * 100, 1);
        ConnSplitTruckOn.IsChecked = c.SplitPercentTruck is not null;
        ConnSplitTruck.IsEnabled = c.SplitPercentTruck is not null;
        ConnSplitTruck.Value = (decimal)Math.Round(c.SplitPercentTruck ?? mineTruck * 100, 1);

        string Describe(List<(int? ConnectorId, double Share)> s) => string.Join(" · ", s.Select(x =>
            $"{(x.ConnectorId is { } id ? Validation.NameOf(net, id) : $"seguir em {from?.Name}")} {x.Share:P0}"));
        var sameForTrucks = split.Select(s => Math.Round(s.Share, 6)).SequenceEqual(splitTruck.Select(s => Math.Round(s.Share, 6)));
        ConnSplitInfo.Text = split.Count < 2
            ? "Só há este caminho saindo deste ponto."
            : (sameForTrucks
                  ? $"Neste ponto: {Describe(split)}"
                  : $"Neste ponto, leves: {Describe(split)}\nPesados: {Describe(splitTruck)}") +
              (net.Inputs.Any(i => net.Demand.UsesMatrix(i.Id))
                  ? "\nEntradas com matriz O/D não usam esta divisão (seguem as rotas da matriz)."
                  : "");

        ConnInfo.Text = $"Id {c.Id} · {(len is { } l ? $"{l:0.0} m" : "geometria inválida")} · " +
                        $"{c.Points.Count} ponto(s) intermediário(s)\nFaixa 1 = a da direita, no sentido do tráfego.";
    }

    /// <summary>Atualiza os itens (id, texto) da ComboBox só se mudaram, e seleciona o id.</summary>
    // ---------------------------------------------------------------- cenários

    private void FillScenarios()
    {
        var project = _editor.Project;
        var names = project.Scenarios.Select((s, i) => (i, s.Name)).ToList();
        SetItems(ScenarioBox, names, project.ActiveScenario);
        ScenarioBox.IsEnabled = !_editor.ReadOnly;
        if (!ScenarioName.IsFocused) ScenarioName.Text = project.Active.Name;
        SetItems(ReferenceBox, [(-1, "Nenhum"), .. names.Where(n => n.i != project.ActiveScenario)],
            _editor.ReferenceNetwork is not null ? _editor.ReferenceScenario!.Value : -1);
        var several = project.Scenarios.Count > 1;
        ReferenceBox.IsEnabled = several;
        RemoveScenarioButton.IsEnabled = RemoveScenarioItem.IsEnabled = several && !_editor.ReadOnly;
        EvalAll.IsVisible = several;
    }

    private void OnScenarioChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || ScenarioBox.SelectedItem is not ComboBoxItem { Tag: int index }) return;
        MapCanvas.CancelToolState();
        MapCanvas.ClearFocus();
        _editor.SwitchScenario(index);
        SetStatus($"Editando o cenário \"{_editor.Project.Active.Name}\".");
    }

    private void OnReferenceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || ReferenceBox.SelectedItem is not ComboBoxItem { Tag: int index }) return;
        _editor.SetReferenceScenario(index < 0 ? null : index);
    }

    private void OnScenarioNameCommit(object? sender, RoutedEventArgs e)
    {
        if (ScenarioName.Text is { } text) _editor.RenameScenario(_editor.Project.UniqueScenarioNameFor(text, _editor.Project.ActiveScenario));
    }

    private void OnDuplicateScenario(object? sender, RoutedEventArgs e)
    {
        if (_editor.ReadOnly) return;
        var from = _editor.Project.Active.Name;
        MapCanvas.CancelToolState();
        _editor.DuplicateScenario();
        SetStatus($"Cenário \"{_editor.Project.Active.Name}\" criado como cópia de \"{from}\". O contorno amarelo mostra \"{from}\"; " +
                  "dê um nome ao novo cenário no painel à direita.");
    }

    private void OnRemoveScenario(object? sender, RoutedEventArgs e)
    {
        if (_editor.ReadOnly || _editor.Project.Scenarios.Count <= 1) return;
        var name = _editor.Project.Active.Name;
        MapCanvas.CancelToolState();
        _editor.RemoveScenario();
        SetStatus($"Cenário \"{name}\" excluído (Ctrl+Z desfaz).");
    }

    private async void OnImportScenario(object? sender, RoutedEventArgs e)
    {
        if (_editor.ReadOnly) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Importar cenários de outro projeto",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Projeto ou pacote") { Patterns = ["*" + ProjectFile.Extension, "*" + ProjectPackage.Extension, "*.zip"] },
                ProjectType, PackageType, FilePickerFileTypes.All],
        });
        var names = new List<string>();
        var warnings = new List<string>();
        foreach (var f in files)
        {
            if (f.TryGetLocalPath() is not { } path) continue;
            if (_editor.FilePath is { } own && string.Equals(Path.GetFullPath(own), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("o próprio projeto aberto não pode ser importado (use Duplicar)");
                continue;
            }
            try
            {
                MapCanvas.CancelToolState();
                var result = _editor.ImportScenarios(ProjectPackage.IsPackage(path) ? ExtractForImport(path) : path);
                names.AddRange(result.Added.Select(s => $"\"{s.Name}\""));
                if (result.Alignment == ImportAlignment.DifferentImage)
                    warnings.Add($"{Path.GetFileName(path)} usa outra foto: confira se a rede ficou no lugar certo");
            }
            catch (Exception ex)
            {
                warnings.Add($"não foi possível importar {Path.GetFileName(path)}: {ex.Message.Split('\n')[0]}");
            }
        }
        if (names.Count == 0 && warnings.Count == 0) return;
        var text = names.Count > 0 ? $"Importado(s): {string.Join(", ", names)}." : "Nada importado.";
        if (warnings.Count > 0) text += " Atenção: " + string.Join("; ", warnings) + ".";
        SetStatus(text);
        if (names.Count > 0) MapCanvas.FitToContent();
    }

    private static void SetItems(ComboBox box, IReadOnlyList<(int Id, string Text)> items, int selected)
    {
        var current = box.Items.OfType<ComboBoxItem>().Select(i => ((int)i.Tag!, i.Content as string ?? "")).ToList();
        if (!current.SequenceEqual(items))
        {
            box.SelectedItem = null;
            box.Items.Clear();
            foreach (var (id, text) in items) box.Items.Add(new ComboBoxItem { Content = text, Tag = id });
        }
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag! == selected);
    }

    private void FillInput(VehicleInput input)
    {
        if (!InputName.IsFocused) InputName.Text = input.Name;
        InputVolume.Value = (decimal)input.VolumeVph;
        InputTrucks.Value = (decimal)Math.Round(input.TruckShare * 100);
        var link = _editor.Network.FindLink(input.LinkId);
        var demand = _editor.Network.Demand;
        var mode = demand.UsesMatrix(input.Id)
            ? "Usa a matriz O/D (o volume acima não vale): edite em Rede → Demanda."
            : demand.SliceStarts.Count > 1 || input.Volumes.Count > 0
                ? "Tem volumes por intervalo (Rede → Demanda); o volume acima vale nos intervalos sem valor próprio."
                : "Em cada escolha, as opções têm a mesma chance.";
        InputInfo.Text = $"Id {input.Id} · no início de {link?.Name ?? "?"}\n" +
                         $"Chegadas aleatórias (Poisson). {mode}";
    }

    private void FillConflict(Conflict c)
    {
        var net = _editor.Network;
        var (a, b) = (Validation.NameOf(net, c.A), Validation.NameOf(net, c.B));
        if (c.B == ConflictRule.MainLine && c.MainLinkId is { } main) b = $"fluxo principal de {Validation.NameOf(net, main)}";
        ConflictInfo.Text = c.Kind switch
        {
            ConflictKind.Crossing => $"{a} cruza {b}" + (c.JunctionKey is { } k ? $" dentro do nó {k}." : "."),
            ConflictKind.Merge => $"{a} converge com {b}" + (c.JunctionKey is { } k ? $" (nó {k})." : "."),
            _ => $"{a} cruza {b} fora de um nó. Se for no mesmo nível, termine os links antes do cruzamento " +
                 "e ligue os movimentos por conectores; isso ainda não é simulado como interseção.",
        };

        var linkCrossing = c.Kind == ConflictKind.LinkCrossing;
        ConflictAuto.Content = linkCrossing ? "Mesmo nível (avisar)" : "Automático (o SUMO decide)";
        ConflictA.Content = $"{a} tem preferência";
        ConflictB.Content = $"{b} tem preferência";
        ConflictA.IsVisible = ConflictB.IsVisible = !linkCrossing;
        ConflictNone.IsVisible = c.Kind != ConflictKind.Merge; // convergência não tem como ser em desnível
        var p = net.PriorityOf(c.A, c.B);
        ConflictAuto.IsChecked = p == ConflictPriority.Auto;
        ConflictA.IsChecked = p == ConflictPriority.A;
        ConflictB.IsChecked = p == ConflictPriority.B;
        ConflictNone.IsChecked = p == ConflictPriority.None;
        ConflictJunctionButton.IsVisible = c.JunctionKey is not null;
    }

    private void FillJunction(Junction j)
    {
        var net = _editor.Network;
        JunctionTitle.Text = $"NÓ {j.Key}";
        JunctionControlBox.SelectedIndex = (int)net.ControlOf(j.Key);
        var conflicts = _editor.Analysis.Conflicts.Where(c => c.JunctionKey == j.Key).ToList();
        var decided = conflicts.Count(c => net.PriorityOf(c.A, c.B) is ConflictPriority.A or ConflictPriority.B);
        JunctionInfo.Text =
            $"{j.ConnectorIds.Count} conector(es) internos: {string.Join(", ", j.ConnectorIds.Select(id => Validation.NameOf(net, id)))}\n" +
            $"{conflicts.Count} conflito(s), {decided} com preferência definida\n\n" +
            "Formado automaticamente pelos conectores que se cruzam. No SUMO vira uma interseção; " +
            "os conectores de dentro viram conexões com a forma desenhada.";
    }

    /// <summary>Soma dos volumes das entradas no intervalo em vigor no início da simulação (matriz ou volume da entrada).</summary>
    private double TotalVolumeAtStart()
    {
        var net = _editor.Network;
        var slice = net.Demand.SliceAt(_editor.Project.Simulation.StartTimeOfDay);
        return net.Inputs.Sum(i => net.Demand.UsesMatrix(i.Id)
            ? net.Demand.Matrix.Where(e => e.OriginId == i.Id && Math.Abs(e.SliceStart - slice) < 1e-6).Sum(e => e.Vph)
            : i.VolumeAt(slice));
    }

    private void FillNetwork()
    {
        var net = _editor.Network;
        var total = net.Links.Where(l => l.Points.Count >= 2).Sum(l => l.Axis().Length);
        var bg = _editor.Project.Background;
        var scale = bg is null ? "sem foto"
            : _editor.Project.Calibration is null ? "foto NÃO calibrada"
            : $"foto calibrada: {bg.MetersPerPixel:0.000} m/px";
        var analysis = _editor.Analysis;
        NetworkInfo.Text = $"{net.Links.Count} link(s), {total:0} m de eixo\n{net.Connectors.Count} conector(es)\n" +
                           $"{analysis.Junctions.Count} nó(s), {analysis.Conflicts.Count} conflito(s)\n" +
                           $"{net.Inputs.Count} entrada(s), {TotalVolumeAtStart():0} veíc/h no início da simulação\n{scale}";

        var issues = Validation.Check(net);
        ShowNotices(IssuesList, issues.Select(i => (i.Severity == Severity.Error ? "✖" : "⚠", MapNotice.Of(net, i))).ToList(),
            "#9aa4b2", empty: "Nenhum problema encontrado.");
    }

    // ---------------------------------------------------------------- avisos clicáveis

    private IReadOnlyList<MapTarget>? _shownNotice;

    /// <summary>
    /// Lista de avisos; os que citam objetos do mapa são clicáveis: destacam esses objetos e enquadram a
    /// vista neles. Clicar de novo no mesmo aviso tira o destaque.
    /// </summary>
    private void ShowNotices(StackPanel panel, IReadOnlyList<(string Icon, MapNotice Notice)> notices, string color, string? empty = null)
    {
        var key = string.Join("\n", notices.Select(n => n.Icon + n.Notice.Text + string.Join(",", n.Notice.Targets)));
        if (panel.Tag as string == key) return; // nada mudou: não refaz (a tela atualiza a cada passo da simulação)
        panel.Tag = key;
        panel.Children.Clear();
        if (notices.Count == 0 && empty is not null)
            panel.Children.Add(new TextBlock { Classes = { "muted" }, Text = empty });
        foreach (var (icon, notice) in notices)
        {
            var clickable = notice.Targets.Count > 0;
            var item = new Border
            {
                Classes = { "notice" },
                Child = new TextBlock { Classes = { "muted" }, Text = $"{icon} {notice.Text}", Foreground = Avalonia.Media.Brush.Parse(color) },
            };
            if (clickable)
            {
                item.Classes.Add("link");
                ToolTip.SetTip(item, "Clique para mostrar no mapa");
                item.PointerPressed += (_, e) =>
                {
                    e.Handled = true;
                    var again = _shownNotice == notice.Targets && MapCanvas.FocusTargets == notice.Targets;
                    foreach (var other in new[] { IssuesList, SimNotices }.SelectMany(p => p.Children))
                        other.Classes.Remove("active");
                    if (again)
                    {
                        _shownNotice = null;
                        MapCanvas.ClearFocus();
                        return;
                    }
                    _shownNotice = notice.Targets;
                    item.Classes.Add("active");
                    MapCanvas.FocusOn(notice.Targets);
                    SetStatus($"Em destaque (magenta): {notice.Text}");
                };
            }
            panel.Children.Add(item);
        }
    }

    // ---------------------------------------------------------------- ferramentas e teclado

    private void OnToolClick(object? sender, RoutedEventArgs e)
    {
        _editor.SetTool(sender == ToolLink ? Tool.Link
            : sender == ToolConnector ? Tool.Connector
            : sender == ToolInput ? Tool.Input
            : sender == ToolMeasure ? Tool.Measure
            : sender == ToolCalibrate ? Tool.Calibrate
            : Tool.Select);
        RefreshTools();
        MapCanvas.Focus();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.Z: _editor.Undo(); break;
                case Key.Y: _editor.Redo(); break;
                case Key.S when shift: _ = SaveAsAsync(); break;
                case Key.S: _ = SaveAsync(); break;
                case Key.O: OnOpen(null, e); break;
                case Key.N: OnNew(null, e); break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F1)
        {
            ShowHelp(ContextTopic());
            e.Handled = true;
            return;
        }

        // Teclas simples não valem enquanto se digita num campo.
        if (e.Source is TextBox) return;
        switch (e.Key)
        {
            case Key.V: _editor.SetTool(Tool.Select); break;
            case Key.L: _editor.SetTool(Tool.Link); break;
            case Key.C: _editor.SetTool(Tool.Connector); break;
            case Key.E: _editor.SetTool(Tool.Input); break;
            case Key.M: _editor.SetTool(Tool.Measure); break;
            case Key.K: _editor.SetTool(Tool.Calibrate); break;
            case Key.F: MapCanvas.FitToContent(); break;
            case Key.Delete: _editor.DeleteSelection(); break;
            case Key.Escape:
                if (!MapCanvas.CancelToolState())
                {
                    if (_editor.Tool != Tool.Select) _editor.SetTool(Tool.Select);
                    else _editor.ClearSelection();
                }
                HideCalibrationBar();
                break;
            default: return;
        }
        e.Handled = true;
    }

    // ---------------------------------------------------------------- propriedades do link

    private void OnNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) MapCanvas.Focus(); // tira o foco e dispara o LostFocus
    }

    private void OnLinkNameCommit(object? sender, RoutedEventArgs e)
    {
        if (_editor.SelectedLink is not { } link || LinkName.Text is not { } text) return;
        link.Name = text.Trim();
        _editor.Checkpoint();
    }

    private void OnLinkLanesChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_refreshing || _editor.SelectedLink is not { } link || e.NewValue is not { } v) return;
        var n = (int)v;
        var width = link.Lanes[^1].Width;
        while (link.Lanes.Count < n) link.Lanes.Add(new Lane { Width = width });
        // Remove pela esquerda: a faixa 1 (direita) continua sendo a mesma.
        while (link.Lanes.Count > n) link.Lanes.RemoveAt(link.Lanes.Count - 1);
        _editor.Network.FitConnectorsTo(link);
        _editor.Checkpoint();
    }

    private void OnLinkWidthChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_refreshing || _editor.SelectedLink is not { } link || e.NewValue is not { } v) return;
        foreach (var lane in link.Lanes) lane.Width = (double)v;
        _editor.Checkpoint();
    }

    private void OnLinkSpeedChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_refreshing || _editor.SelectedLink is not { } link || e.NewValue is not { } v) return;
        link.SpeedKmh = (double)v;
        _editor.Checkpoint();
    }

    private void OnSplitLink(object? sender, RoutedEventArgs e)
    {
        if (_editor.ReadOnly || _editor.SelectedLink is not { } link) return;
        MapCanvas.BeginSplit(link.Id);
        MapCanvas.Focus();
    }

    private void OnCreateOpposite(object? sender, RoutedEventArgs e)
    {
        if (_editor.ReadOnly || _editor.SelectedLink is not { } link) return;
        var opposite = _editor.Network.AddOppositeLink(link, (double)(OppositeGap.Value ?? 0));
        _editor.Select(opposite);
        _editor.Checkpoint();
        SetStatus($"{opposite.Name} criado. Ajuste os pontos se a via real não for paralela, e ligue-o com conectores.");
    }

    private void OnLinkSmoothChanged(object? sender, RoutedEventArgs e)
    {
        if (_refreshing || _editor.SelectedLink is not { } link) return;
        link.Smooth = LinkSmooth.IsChecked == true;
        _editor.Network.FitConnectorsTo(link);
        _editor.Checkpoint();
    }

    // ---------------------------------------------------------------- propriedades do conector

    private void OnConnNameCommit(object? sender, RoutedEventArgs e)
    {
        if (_editor.SelectedConnector is not { } c || ConnName.Text is not { } text) return;
        c.Name = text.Trim();
        _editor.Checkpoint();
    }

    private void OnConnChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_refreshing || _editor.SelectedConnector is not { } c || e.NewValue is not { } v) return;
        if (sender == ConnLanes) c.LaneCount = (int)v;
        else if (sender == ConnFromLane) c.FromLane = (int)v - 1;
        else if (sender == ConnToLane) c.ToLane = (int)v - 1;
        else if (sender == ConnFromS) c.FromS = (double)v;
        else if (sender == ConnToS) c.ToS = (double)v;
        if (_editor.Network.FindLink(c.FromLinkId) is { } from) _editor.Network.FitConnectorsTo(from);
        _editor.Checkpoint();
    }

    private void OnConnSplitToggled(object? sender, RoutedEventArgs e)
    {
        if (_refreshing || _editor.SelectedConnector is not { } c) return;
        // Ligando, começa com a parte que o conector já recebia; desligando, volta à divisão igual.
        c.SplitPercent = ConnSplitOn.IsChecked == true ? (double)(ConnSplit.Value ?? 50) : null;
        _editor.Checkpoint();
    }

    private void OnConnSplitTruckToggled(object? sender, RoutedEventArgs e)
    {
        if (_refreshing || _editor.SelectedConnector is not { } c) return;
        c.SplitPercentTruck = ConnSplitTruckOn.IsChecked == true ? (double)(ConnSplitTruck.Value ?? 50) : null;
        _editor.Checkpoint();
    }

    private void OnConnSplitTruckChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_refreshing || _editor.SelectedConnector is not { } c || c.SplitPercentTruck is null || e.NewValue is not { } v) return;
        c.SplitPercentTruck = (double)v;
        _editor.Checkpoint();
    }

    private void OnConnSplitChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_refreshing || _editor.SelectedConnector is not { } c || c.SplitPercent is null || e.NewValue is not { } v) return;
        c.SplitPercent = (double)v;
        _editor.Checkpoint();
    }

    private void OnConnSignalChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || _editor.SelectedConnector is not { } c) return;
        var controllerId = ConnSignal.SelectedItem is ComboBoxItem { Tag: int t } ? t : 0;
        var groupId = ConnGroup.SelectedItem is ComboBoxItem { Tag: int g } ? g : (int?)null;
        var fromController = sender == ConnSignal;
        // Depois do evento: o Refresh refaz as listas das próprias ComboBoxes, o que não pode
        // acontecer no meio da troca de seleção delas.
        Dispatcher.UIThread.Post(() =>
        {
            if (fromController)
            {
                if (controllerId == 0) c.Signal = null;
                else if (c.Signal?.ControllerId != controllerId && _editor.Network.FindController(controllerId) is { } sc)
                    c.Signal = new SignalRef { ControllerId = controllerId, GroupId = sc.Groups.FirstOrDefault()?.Id ?? 1 };
            }
            else if (c.Signal is not null && groupId is { } gid)
            {
                c.Signal.GroupId = gid;
            }
            _editor.Checkpoint();
        });
    }

    private SignalWindow? _signalWindow;

    private void OnOpenSignals(object? sender, RoutedEventArgs e)
    {
        if (_signalWindow is { } open)
        {
            open.Activate();
            return;
        }
        if (_editor.Network.SignalControllers.Count == 0 && !_editor.ReadOnly)
        {
            _editor.Network.AddController();
            _editor.Checkpoint();
        }
        _signalWindow = new SignalWindow(_editor, _editor.SelectedConnector?.Signal?.ControllerId);
        _signalWindow.Closed += (_, _) => _signalWindow = null;
        _signalWindow.Show(this);
    }

    // ---------------------------------------------------------------- medições

    private void FillMeasurement(Measurement m)
    {
        var net = _editor.Network;
        MeasureTitle.Text = Measurement.KindName(m.Kind).ToUpperInvariant();
        if (!MeasureName.IsFocused) MeasureName.Text = m.Name;
        MeasureLength.IsVisible = MeasureLengthLabel.IsVisible = m.Kind == MeasurementKind.Queue;
        MeasureLength.Value = (decimal)m.QueueLength;
        var where = $"{Validation.NameOf(net, m.LinkId)}, a {m.S:0.0} m do início";
        MeasureInfo.Text = m.Kind switch
        {
            MeasurementKind.Count => $"Id {m.Id} · {where}.\nVolume e velocidade de todas as faixas no ponto. Arraste a barra para mover.",
            MeasurementKind.Queue => $"Id {m.Id} · {where}.\nFila média e máxima a montante do ponto (na faixa mais carregada), dentro do trecho do link. " +
                                     "Coloque na linha de retenção.",
            _ => $"Id {m.Id} · de {where}\naté {Validation.NameOf(net, m.ToLinkId)}, a {m.ToS:0.0} m.\n" +
                 "Tempo de viagem e atraso dos veículos que passam pelos dois pontos. Arraste as barras para mover.",
        };
    }

    private void OnMeasureNameCommit(object? sender, RoutedEventArgs e)
    {
        if (_editor.SelectedMeasurement is not { } m || MeasureName.Text is not { } text) return;
        m.Name = text.Trim();
        _editor.Checkpoint();
    }

    private void OnMeasureLengthChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_refreshing || _editor.SelectedMeasurement is not { } m || e.NewValue is not { } v) return;
        m.QueueLength = (double)v;
        _editor.Checkpoint();
    }

    private void OnMeasureKindChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox box) return;
        _editor.MeasureKind = (MeasurementKind)Math.Max(0, box.SelectedIndex);
        // Também dispara durante o carregamento do XAML: só troca a ferramenta quando é o usuário escolhendo.
        if (IsLoaded && _editor.Tool != Tool.Measure) _editor.SetTool(Tool.Measure);
    }

    // ---------------------------------------------------------------- avaliação e resultados

    private CancellationTokenSource? _evalCancel;

    private void OnReplicationsChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_refreshing || e.NewValue is not { } v) return;
        _editor.Project.Simulation.Replications = (int)v;
        _editor.Checkpoint();
    }

    private string ProjectName => _editor.FilePath is { } p
        ? Path.GetFileName(p).Replace(ProjectFile.Extension, "", StringComparison.OrdinalIgnoreCase)
        : "sem título";

    /// <summary>Pasta dos resultados: ao lado do projeto salvo, ou na pasta temporária.</summary>
    private string ResultsFolder() => _editor.FilePath is { } p
        ? ResultSet.FolderFor(p)
        : Path.Combine(Path.GetTempPath(), "trafego", "sem-titulo.resultados");

    private string SaveResult(ResultSet set)
    {
        var dir = ResultsFolder();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{set.CreatedAt:yyyyMMdd-HHmmss}{ResultSet.Extension}");
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(dir, $"{set.CreatedAt:yyyyMMdd-HHmmss}-{i}{ResultSet.Extension}");
        set.Save(path);
        return path;
    }

    private void OnEvaluate(object? sender, RoutedEventArgs e) =>
        Evaluate(all: EvalAll.IsChecked == true && _editor.Project.Scenarios.Count > 1);

    private void OnEvaluateAll(object? sender, RoutedEventArgs e)
    {
        if (_evalCancel is null) Evaluate(all: true);
    }

    /// <summary>"Projeto · Cenário" (só o projeto quando há um cenário só).</summary>
    private static string Titled(string project, Project p, Scenario s) =>
        p.Scenarios.Count > 1 ? $"{project} · {s.Name}" : project;

    /// <summary>
    /// Roda as replicações em processos sumo paralelos, sem animação; clicar de novo cancela. Com todos os
    /// cenários, roda um depois do outro com as mesmas sementes e abre a comparação.
    /// </summary>
    private async void Evaluate(bool all)
    {
        if (_evalCancel is { } running)
        {
            running.Cancel();
            return;
        }
        if (SumoInstall.Home is null)
        {
            SetStatus("SUMO não encontrado: instale o Eclipse SUMO e defina SUMO_HOME.");
            return;
        }

        var copy = ProjectFile.FromJson(ProjectFile.ToJson(_editor.Project));
        var indices = all ? Enumerable.Range(0, copy.Scenarios.Count).ToList() : [copy.ActiveScenario];
        var n = Math.Max(1, copy.Simulation.Replications);
        _evalCancel = new CancellationTokenSource();
        EvalRun.Content = "Cancelar avaliação";
        var paths = new List<string>();
        try
        {
            foreach (var i in indices)
            {
                var sc = copy.Scenarios[i];
                var label = indices.Count > 1 ? $"Avaliação ({paths.Count + 1}/{indices.Count}, {sc.Name}): " : "Avaliação: ";
                var dir = Path.Combine(ScenarioDirectory(), copy.Scenarios.Count > 1 ? $"avaliacao-{i + 1}" : "avaliacao");
                SetStatus(label + "preparando o cenário no SUMO…");
                var scenario = await Task.Run(() => SumoScenario.Prepare(sc.Network, dir, copy.Simulation));
                SetStatus(label + $"rodando {n} replicação(ões)…");
                var set = await SumoBatch.RunAsync(scenario, $"{Titled(ProjectName, copy, sc)} · {DateTime.Now:dd/MM HH:mm:ss} · {n} replic.",
                    new Progress<string>(m => SetStatus(label + m)), _evalCancel.Token);
                set.ProjectName = ProjectName;
                set.ScenarioName = sc.Name;
                paths.Add(SaveResult(set));
            }
            SetStatus(paths.Count > 1 ? $"Avaliação dos {paths.Count} cenários concluída." : $"Avaliação concluída: {paths[0]}");
            if (paths.Count > 1) OpenResults(paths[0], compare: paths);
            else OpenResults(paths[0]);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Avaliação cancelada" + (paths.Count > 0 ? $" ({paths.Count} cenário(s) já avaliado(s) estão em Resultados)." : "."));
        }
        catch (Exception ex)
        {
            SetStatus("Avaliação falhou: " + ex.Message.Split('\n')[0]);
        }
        finally
        {
            _evalCancel.Dispose();
            _evalCancel = null;
            EvalRun.Content = "Avaliar";
        }
    }

    /// <summary>Exportar vídeo: precisa da simulação parada (o libsumo só aceita uma simulação por vez).</summary>
    private async void OnExportVideo(object? sender, RoutedEventArgs e)
    {
        if (_sim.Active)
        {
            SetStatus("Pare a simulação (■ Parar) antes de exportar o vídeo.");
            return;
        }
        if (!LibsumoSession.Available)
        {
            SetStatus("SUMO não encontrado: instale o Eclipse SUMO e defina SUMO_HOME.");
            return;
        }
        var window = new VideoWindow(MapCanvas, _editor.Project, Path.Combine(ScenarioDirectory(), "video"),
            $"{ProjectName} {DateTime.Now:yyyy-MM-dd HHmm}");
        _editor.SetReadOnly(true); // a rede não muda enquanto o vídeo é gerado
        EvalRun.IsEnabled = SimRun.IsEnabled = false;
        try
        {
            await window.ShowDialog(this);
        }
        finally
        {
            _editor.SetReadOnly(false);
            EvalRun.IsEnabled = SimRun.IsEnabled = true;
        }
    }

    private ResultsWindow? _resultsWindow;

    private void OnOpenResults(object? sender, RoutedEventArgs e) => OpenResults(null);

    /// <param name="compare">Resultados a deixar marcados (e mostrados) na aba Comparar.</param>
    private void OpenResults(string? select, IReadOnlyList<string>? compare = null)
    {
        if (_resultsWindow is null)
        {
            _resultsWindow = new ResultsWindow(_editor, ResultsFolder(), select);
            _resultsWindow.Closed += (_, _) => _resultsWindow = null;
            _resultsWindow.Show(this);
        }
        else
        {
            _resultsWindow.Reload(select);
            _resultsWindow.Activate();
        }
        if (compare is not null) _resultsWindow.ShowComparison(compare);
    }

    private DemandWindow? _demandWindow;

    private void OnOpenDemand(object? sender, RoutedEventArgs e)
    {
        if (_demandWindow is { } open)
        {
            open.Activate();
            return;
        }
        _demandWindow = new DemandWindow(_editor);
        _demandWindow.Closed += (_, _) => _demandWindow = null;
        _demandWindow.Show(this);
    }

    private void OnSimSettingsCommit(object? sender, RoutedEventArgs e)
    {
        if (SignalWindow.TryParseTime(SimStart.Text, out var start))
        {
            _editor.Project.Simulation.StartTimeOfDay = start;
            _editor.Checkpoint();
        }
        else
        {
            SimStart.Text = SignalWindow.FormatTime(_editor.Project.Simulation.StartTimeOfDay);
            SetStatus("Horário inválido: use hh:mm, por exemplo 07:30.");
        }
    }

    private void OnSimMinutesChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_refreshing || e.NewValue is not { } v) return;
        _editor.Project.Simulation.DurationMinutes = (double)v;
        _editor.Checkpoint();
    }

    private void OnConnStraighten(object? sender, RoutedEventArgs e)
    {
        if (_editor.SelectedConnector is not { } c) return;
        c.Points.Clear();
        _editor.Checkpoint();
    }

    // ---------------------------------------------------------------- conflitos e nós

    private void OnConflictChoice(object? sender, RoutedEventArgs e)
    {
        if (_refreshing || sender is not RadioButton { IsChecked: true } rb || _editor.SelectedConflictInfo is not { } c) return;
        var priority = rb == ConflictA ? ConflictPriority.A
            : rb == ConflictB ? ConflictPriority.B
            : rb == ConflictNone ? ConflictPriority.None
            : ConflictPriority.Auto;
        _editor.Network.SetPriority(c.A, c.B, priority);
        _editor.Checkpoint();
    }

    private void OnConflictJunction(object? sender, RoutedEventArgs e)
    {
        if (_editor.SelectedConflictInfo?.JunctionKey is { } k) _editor.SelectJunction(k);
    }

    private void OnJunctionControlChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || sender is not ComboBox box || _editor.SelectedJunction is not { } j) return;
        _editor.Network.SetControl(j.Key, (JunctionControl)Math.Max(0, box.SelectedIndex));
        _editor.Checkpoint();
    }

    // ---------------------------------------------------------------- entradas

    private void OnInputNameCommit(object? sender, RoutedEventArgs e)
    {
        if (_editor.SelectedInput is not { } input || InputName.Text is not { } text) return;
        input.Name = text.Trim();
        _editor.Checkpoint();
    }

    private void OnInputChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_refreshing || _editor.SelectedInput is not { } input || e.NewValue is not { } v) return;
        if (sender == InputVolume) input.VolumeVph = (double)v;
        else if (sender == InputTrucks) input.TruckShare = (double)v / 100;
        _editor.Checkpoint();
    }

    // ---------------------------------------------------------------- simulação

    private async void OnSimRun(object? sender, RoutedEventArgs e)
    {
        switch (_sim.State)
        {
            case SimState.Running: _sim.Pause(); return;
            case SimState.Paused: _sim.Resume(); return;
            case SimState.Stopped: break;
            default: return;
        }
        if (!LibsumoSession.Available)
        {
            SetStatus("SUMO não encontrado: instale o Eclipse SUMO e defina SUMO_HOME (e recompile).");
            return;
        }

        MapCanvas.CancelToolState();
        _editor.SetReadOnly(true);
        SimWarnings.Text = "";
        _simNoticesOf = null;
        ShowNotices(SimNotices, [], "#e8b04a");
        SetStatus("Preparando o cenário no SUMO (netconvert)…");
        try
        {
            await _sim.StartAsync(_editor.Project, ScenarioDirectory());
            SetStatus($"Simulando. Arquivos do SUMO em {_sim.Scenario!.Directory}");
        }
        catch (Exception ex)
        {
            _editor.SetReadOnly(false);
            SetStatus("Não foi possível simular: " + ex.Message.Split('\n')[0]);
            SimWarnings.Text = ex.Message;
            SimStats.Text = "";
            FillSimPanel();
        }
    }

    private void OnSimStop(object? sender, RoutedEventArgs e) => StopSimulation();

    private void StopSimulation()
    {
        if (!_sim.Active) return;
        _sim.Stop();
        _editor.SetReadOnly(false);
        SetStatus("Simulação encerrada. A rede pode ser editada de novo.");
    }

    private void OnSimSpeedChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Dispara já durante o carregamento do XAML, antes de SimSpeed ser atribuído: usa o sender.
        if (sender is not ComboBox box) return;
        var i = Math.Clamp(box.SelectedIndex, 0, SimulationController.Speeds.Length - 1);
        _sim.Speed = SimulationController.Speeds[i];
    }

    private void OnSimStateChanged()
    {
        SimRun.Content = _sim.State switch
        {
            SimState.Running => "⏸ Pausar",
            SimState.Paused => "▶ Continuar",
            SimState.Preparing => "Preparando…",
            SimState.Finished => "✓ Terminou",
            _ => "▶ Simular",
        };
        SimRun.IsEnabled = _sim.State is SimState.Stopped or SimState.Running or SimState.Paused;
        SimStop.IsEnabled = _sim.State is not SimState.Stopped and not SimState.Preparing;
        SimMinutes.IsEnabled = SimStart.IsEnabled = _sim.State == SimState.Stopped;
        if (_sim.State == SimState.Finished) SetStatus("Simulação terminou. Clique em Parar para voltar a editar.");
        FillSimPanel();
    }

    private void FillSimPanel()
    {
        SimPanel.IsVisible = _sim.Active || SimWarnings.Text is { Length: > 0 };
        if (!_sim.Active) return;
        var s = _sim.Last;
        var begin = _sim.Scenario?.BeginTime ?? 0;
        var now = Math.Max(s.Time, begin);
        var t = TimeSpan.FromSeconds(now - begin);
        SimStats.Text =
            $"relógio      {SimulationSettings.Clock(now)}\n" +
            $"decorrido    {(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}\n" +
            $"na rede      {s.Vehicles.Count}\n" +
            $"parados      {s.Stopped}\n" +
            $"entraram     {s.Departed}\n" +
            $"saíram       {s.Arrived}\n" +
            $"vel. média   {s.MeanSpeed * 3.6:0} km/h";
        if (_sim.Scenario is { } sc && !ReferenceEquals(_simNoticesOf, sc))
        {
            _simNoticesOf = sc;
            var notices = SumoMessages.ExplainAll(sc.Export, sc.Network);
            var shown = notices.Take(MaxSimNotices).Select(n => ("⚠", n)).ToList();
            if (notices.Count > MaxSimNotices)
                shown.Add(("…", new MapNotice($"mais {notices.Count - MaxSimNotices} aviso(s) — veja a pasta do cenário SUMO.", [])));
            ShowNotices(SimNotices, shown, "#e8b04a");
        }
    }

    private const int MaxSimNotices = 12;
    private SumoScenario? _simNoticesOf;

    private void OnOpenScenarioFolder(object? sender, RoutedEventArgs e)
    {
        var dir = _sim.Scenario?.Directory ?? ScenarioDirectory();
        if (Directory.Exists(dir)) Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }

    /// <summary>Pasta dos arquivos do SUMO: ao lado do projeto salvo, ou na pasta temporária.</summary>
    private string ScenarioDirectory()
    {
        if (_editor.FilePath is { } path)
        {
            var name = Path.GetFileName(path);
            name = name.EndsWith(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase)
                ? name[..^ProjectFile.Extension.Length]
                : Path.GetFileNameWithoutExtension(name);
            return Path.Combine(Path.GetDirectoryName(path)!, name + ".sumo");
        }
        return Path.Combine(Path.GetTempPath(), "trafego", "sem-titulo.sumo");
    }

    // ---------------------------------------------------------------- calibração

    private void OnCalibrationPicked(Vec2 a, Vec2 b)
    {
        _pendingCalibration = (a, b);
        var current = Vec2.Distance(a, b) * (_editor.Project.Background?.MetersPerPixel ?? 1);
        CalibMeters.Value = (decimal)Math.Round(current, 2);
        CalibrationBar.IsVisible = true;
        CalibMeters.Focus();
        SetStatus("Informe a distância real entre os dois pontos e clique em Aplicar.");
    }

    private void OnApplyCalibration(object? sender, RoutedEventArgs e)
    {
        if (_pendingCalibration is not { } pc || CalibMeters.Value is not { } meters) return;
        try
        {
            var factor = _editor.Project.Calibrate(pc.A, pc.B, (double)meters);
            _editor.Checkpoint();
            HideCalibrationBar();
            MapCanvas.FitToContent();
            SetStatus($"Foto calibrada: {_editor.Project.Background!.MetersPerPixel:0.0000} m/px" +
                (_editor.Network.Links.Count > 0 && Math.Abs(factor - 1) > 1e-9 ? $"; a rede foi reescalada ×{factor:0.000}." : "."));
            _editor.SetTool(Tool.Select);
        }
        catch (ArgumentException ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void OnCancelCalibration(object? sender, RoutedEventArgs e)
    {
        HideCalibrationBar();
        SetStatus("Calibração cancelada.");
    }

    private void HideCalibrationBar()
    {
        _pendingCalibration = null;
        CalibrationBar.IsVisible = false;
        MapCanvas.ClearCalibrationMarks();
    }

    // ---------------------------------------------------------------- menu Editar e Exibir

    private void OnUndo(object? sender, RoutedEventArgs e) => _editor.Undo();
    private void OnRedo(object? sender, RoutedEventArgs e) => _editor.Redo();
    private void OnDelete(object? sender, RoutedEventArgs e) => _editor.DeleteSelection();
    private void OnFit(object? sender, RoutedEventArgs e) => MapCanvas.FitToContent();

    private async void OnAbout(object? sender, RoutedEventArgs e) => await new AboutWindow().ShowDialog(this);

    // ---------------------------------------------------------------- ajuda

    private void ShowHelp(string? topic)
    {
        if (HelpPages.Open(topic) is { } error) SetStatus(error);
        else SetStatus("Ajuda aberta no navegador.");
    }

    /// <summary>Seção da Ajuda para o que está em uso agora: a simulação, o objeto selecionado ou a ferramenta.</summary>
    private string ContextTopic()
    {
        if (_sim.Active) return HelpPages.Topic.Simulation;
        if (_editor.SelectedLink is not null) return HelpPages.Topic.Links;
        if (_editor.SelectedConnector is not null) return HelpPages.Topic.Connectors;
        if (_editor.SelectedInput is not null) return HelpPages.Topic.Demand;
        if (_editor.SelectedConflictInfo is not null || _editor.SelectedJunction is not null) return HelpPages.Topic.Conflicts;
        if (_editor.SelectedMeasurement is not null) return HelpPages.Topic.Measurements;
        return _editor.Tool switch
        {
            Tool.Link => HelpPages.Topic.Links,
            Tool.Connector => HelpPages.Topic.Connectors,
            Tool.Input => HelpPages.Topic.Demand,
            Tool.Measure => HelpPages.Topic.Measurements,
            Tool.Calibrate => HelpPages.Topic.Photo,
            _ => HelpPages.Topic.Screen,
        };
    }

    private void OnHelp(object? sender, RoutedEventArgs e) => ShowHelp(null);

    private void OnHelpPdf(object? sender, RoutedEventArgs e)
    {
        var pdf = Path.Combine(AppContext.BaseDirectory, "ajuda", "Manual-daRosa-Trafego.pdf");
        if (File.Exists(pdf)) Process.Start(new ProcessStartInfo(pdf) { UseShellExecute = true });
        else SetStatus("O manual em PDF não está instalado. Use Ajuda → Manual (F1).");
    }

    /// <summary>Itens do menu Ajuda e botões "?" dos painéis: o assunto vem no Tag.</summary>
    private void OnHelpTopic(object? sender, RoutedEventArgs e) =>
        ShowHelp((sender as Control)?.Tag as string);

    // ---------------------------------------------------------------- arquivos

    private void TryOpen(string path, bool asTemplate = false)
    {
        try
        {
            _editor.Open(path, asTemplate);
            SetStatus(asTemplate ? "Exemplo aberto: foto calibrada pela régua de 370,20 m." : $"Aberto: {path}");
        }
        catch (Exception ex)
        {
            SetStatus($"Não foi possível abrir \"{path}\": {ex.Message}");
        }
    }

    private async void OnNew(object? sender, RoutedEventArgs e)
    {
        StopSimulation();
        if (!await ConfirmDiscardAsync()) return;
        _editor.New();
        SetStatus("Projeto novo. Abra uma foto aérea (Arquivo → Abrir foto) ou desenhe direto.");
    }

    private async void OnOpen(object? sender, RoutedEventArgs e)
    {
        StopSimulation();
        if (!await ConfirmDiscardAsync()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Abrir projeto ou pacote",
            FileTypeFilter = [new FilePickerFileType("Projeto ou pacote") { Patterns = ["*" + ProjectFile.Extension, "*" + ProjectPackage.Extension, "*.zip"] },
                ProjectType, PackageType, FilePickerFileTypes.All],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        if (ProjectPackage.IsPackage(path)) await OpenPackageAsync(path);
        else TryOpen(path);
    }

    // ---------------------------------------------------------------- pacote (enviar a outra pessoa)

    /// <summary>Extrai o pacote numa pasta nova, escolhida pelo usuário, e abre o projeto.</summary>
    private async Task OpenPackageAsync(string zip)
    {
        PackageInfo info;
        try { info = ProjectPackage.ReadInfo(zip); }
        catch (Exception ex)
        {
            SetStatus($"Não foi possível abrir o pacote: {ex.Message}");
            return;
        }
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Onde guardar o projeto recebido? (será criada uma pasta com o nome dele)",
        });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } parent) return;
        try
        {
            var folder = ProjectPackage.FreeFolder(parent, ProjectPackage.BaseName(info.Project));
            var path = ProjectPackage.Extract(zip, folder);
            _editor.Open(path);
            MapCanvas.FitToContent();
            SetStatus($"Pacote aberto e guardado em {folder}." + NewerVersionWarning(info));
        }
        catch (Exception ex)
        {
            SetStatus($"Não foi possível abrir o pacote: {ex.Message.Split('\n')[0]}");
        }
    }

    /// <summary>Extrai um pacote para importar cenários: fica guardado na pasta do programa, junto com a foto.</summary>
    private static string ExtractForImport(string zip)
    {
        var info = ProjectPackage.ReadInfo(zip);
        var parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "daRosa Trafego", "pacotes");
        Directory.CreateDirectory(parent);
        return ProjectPackage.Extract(zip, ProjectPackage.FreeFolder(parent, ProjectPackage.BaseName(info.Project)));
    }

    private static string NewerVersionWarning(PackageInfo info) =>
        Version.TryParse(info.AppVersion, out var theirs) && Version.TryParse(Brand.Version, out var ours) && theirs > ours
            ? $" Atenção: o pacote foi feito na versão {info.AppVersion} e este programa é a {Brand.Version}; atualize se algo não abrir certo."
            : "";

    private async void OnExportPackage(object? sender, RoutedEventArgs e)
    {
        var results = _editor.FilePath is { } fp && Directory.Exists(ResultSet.FolderFor(fp))
            ? Directory.GetFiles(ResultSet.FolderFor(fp), "*" + ResultSet.Extension)
            : [];
        var includeResults = false;
        if (results.Length > 0)
        {
            var mb = results.Sum(f => new FileInfo(f).Length) / 1048576.0;
            var choice = await ChooseAsync("Exportar pacote",
                $"Incluir os resultados das avaliações ({results.Length} arquivo(s), {mb:0.0} MB)? " +
                "Sem eles o pacote fica menor, e quem receber pode gerá-los de novo com Avaliar.",
                ("Incluir resultados", "yes"), ("Sem resultados", "no"), ("Cancelar", "cancel"));
            if (choice == "cancel") return;
            includeResults = choice == "yes";
        }
        var name = _editor.FilePath is { } f ? ProjectPackage.BaseName(f) : "projeto";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Exportar pacote para enviar",
            SuggestedFileName = name + ProjectPackage.Extension,
            FileTypeChoices = [PackageType],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        if (!path.EndsWith(ProjectPackage.Extension, StringComparison.OrdinalIgnoreCase))
            path = Path.Combine(Path.GetDirectoryName(path)!, ProjectPackage.BaseName(path) + ProjectPackage.Extension);
        try
        {
            var r = ProjectPackage.Export(_editor.Project, _editor.FilePath, path, includeResults, Brand.Version);
            var size = new FileInfo(path).Length / 1048576.0;
            SetStatus($"Pacote salvo: {path} ({size:0.0} MB). Quem receber abre por Arquivo → Abrir projeto…" +
                      (r.ImageMissing ? " Atenção: a foto aérea não foi encontrada e ficou de fora." : ""));
        }
        catch (Exception ex)
        {
            SetStatus($"Não foi possível exportar o pacote: {ex.Message}");
        }
    }

    private async void OnOpenImage(object? sender, RoutedEventArgs e)
    {
        if (_editor.ReadOnly) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Abrir foto aérea",
            FileTypeFilter = [ImageType],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        _editor.SetBackgroundImage(path);
        _editor.SetTool(Tool.Calibrate);
        SetStatus("Foto aberta. Calibre: clique nas duas pontas de uma distância conhecida.");
    }

    private async void OnSave(object? sender, RoutedEventArgs e) => await SaveAsync();
    private async void OnSaveAs(object? sender, RoutedEventArgs e) => await SaveAsAsync();

    private async Task<bool> SaveAsync()
    {
        if (_editor.FilePath is null) return await SaveAsAsync();
        return SaveTo(_editor.FilePath);
    }

    private async Task<bool> SaveAsAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Salvar projeto",
            SuggestedFileName = "cenario" + ProjectFile.Extension,
            FileTypeChoices = [ProjectType],
        });
        if (file?.TryGetLocalPath() is not { } path) return false;
        if (!path.EndsWith(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase))
            path = Path.ChangeExtension(path, null) + ProjectFile.Extension;
        return SaveTo(path);
    }

    private bool SaveTo(string path)
    {
        try
        {
            _editor.Save(path);
            SetStatus($"Salvo: {path}");
            return true;
        }
        catch (Exception ex)
        {
            SetStatus($"Não foi possível salvar: {ex.Message}");
            return false;
        }
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || !_editor.Dirty)
        {
            StopSimulation();
            return;
        }
        e.Cancel = true;
        if (await ConfirmDiscardAsync())
        {
            StopSimulation();
            _closeConfirmed = true;
            Close();
        }
    }

    /// <summary>Se houver alterações não salvas, pergunta. Devolve true se pode seguir.</summary>
    private async Task<bool> ConfirmDiscardAsync()
    {
        if (!_editor.Dirty) return true;

        var choice = await ChooseAsync("Alterações não salvas", "O projeto tem alterações não salvas. Salvar antes de continuar?",
            ("Salvar", "save"), ("Descartar", "discard"), ("Cancelar", "cancel"));
        return choice switch
        {
            "save" => await SaveAsync(),
            "discard" => true,
            _ => false,
        };
    }

    /// <summary>Pergunta com botões; devolve o valor do botão escolhido, ou o do último se a janela for fechada.</summary>
    private async Task<string> ChooseAsync(string title, string text, params (string Label, string Value)[] buttons)
    {
        var choice = buttons[^1].Value;
        var dialog = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        foreach (var (label, value) in buttons)
        {
            var b = new Button { Content = label };
            b.Click += (_, _) =>
            {
                choice = value;
                dialog.Close();
            };
            row.Children.Add(b);
        }
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 16,
            Children = { new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, row },
        };
        await dialog.ShowDialog(this);
        return choice;
    }
}
