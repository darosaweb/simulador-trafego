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
            set.Name = $"{ProjectName} · {set.CreatedAt:dd/MM HH:mm:ss} · animação{(set.Partial ? " (interrompida)" : "")}";
            set.ProjectName = ProjectName;
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

        var sample = Path.Combine(AppContext.BaseDirectory, "samples", "exemplo" + ProjectFile.Extension);
        if (args.Length > 0 && File.Exists(args[0])) TryOpen(args[0]);
        else if (File.Exists(sample)) TryOpen(sample, asTemplate: true);

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
            Title = $"{name}{(_editor.Dirty ? " •" : "")} — Tráfego";
            UndoItem.IsEnabled = _editor.CanUndo;
            RedoItem.IsEnabled = _editor.CanRedo;
            var sim = _editor.Project.Simulation;
            if (!SimStart.IsFocused) SimStart.Text = SignalWindow.FormatTime(sim.StartTimeOfDay);
            SimMinutes.Value = (decimal)sim.DurationMinutes;
            EvalReplications.Value = sim.Replications;

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
        var mine = split.FirstOrDefault(s => s.ConnectorId == c.Id).Share;
        ConnSplitOn.IsChecked = c.SplitPercent is not null;
        ConnSplit.IsEnabled = c.SplitPercent is not null;
        ConnSplit.Value = (decimal)Math.Round(c.SplitPercent ?? mine * 100, 1);
        ConnSplitInfo.Text = split.Count < 2
            ? "Só há este caminho saindo deste ponto."
            : "Neste ponto: " + string.Join(" · ", split.Select(s =>
                  $"{(s.ConnectorId is { } id ? Validation.NameOf(net, id) : $"seguir em {from?.Name}")} {s.Share:P0}")) +
              (net.Inputs.Any(i => net.Demand.UsesMatrix(i.Id))
                  ? "\nEntradas com matriz O/D não usam esta divisão (seguem as rotas da matriz)."
                  : "");

        ConnInfo.Text = $"Id {c.Id} · {(len is { } l ? $"{l:0.0} m" : "geometria inválida")} · " +
                        $"{c.Points.Count} ponto(s) intermediário(s)\nFaixa 1 = a da direita, no sentido do tráfego.";
    }

    /// <summary>Atualiza os itens (id, texto) da ComboBox só se mudaram, e seleciona o id.</summary>
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
        IssuesText.Text = issues.Count == 0
            ? "Nenhum problema encontrado."
            : string.Join("\n", issues.Select(i => (i.Severity == Severity.Error ? "✖ " : "⚠ ") + i.Message));
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
        set.Save(path);
        return path;
    }

    /// <summary>Roda as replicações em processos sumo paralelos, sem animação; clicar de novo cancela.</summary>
    private async void OnEvaluate(object? sender, RoutedEventArgs e)
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
        var dir = Path.Combine(ScenarioDirectory(), "avaliacao");
        var n = Math.Max(1, copy.Simulation.Replications);
        _evalCancel = new CancellationTokenSource();
        EvalRun.Content = "Cancelar avaliação";
        try
        {
            SetStatus("Avaliação: preparando o cenário no SUMO…");
            var scenario = await Task.Run(() => SumoScenario.Prepare(copy.Network, dir, copy.Simulation));
            SetStatus($"Avaliação: rodando {n} replicação(ões)…");
            var set = await SumoBatch.RunAsync(scenario, $"{ProjectName} · {DateTime.Now:dd/MM HH:mm:ss} · {n} replic.",
                new Progress<string>(m => SetStatus("Avaliação: " + m)), _evalCancel.Token);
            set.ProjectName = ProjectName;
            var path = SaveResult(set);
            SetStatus($"Avaliação concluída: {path}");
            OpenResults(path);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Avaliação cancelada.");
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

    private ResultsWindow? _resultsWindow;

    private void OnOpenResults(object? sender, RoutedEventArgs e) => OpenResults(null);

    private void OpenResults(string? select)
    {
        if (_resultsWindow is { } open)
        {
            open.Reload(select);
            open.Activate();
            return;
        }
        _resultsWindow = new ResultsWindow(_editor, ResultsFolder(), select);
        _resultsWindow.Closed += (_, _) => _resultsWindow = null;
        _resultsWindow.Show(this);
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
        SimWarnings.Text = _sim.Scenario is { Export.Warnings.Count: > 0 } sc
            ? string.Join("\n", sc.Export.Warnings.Distinct().Take(8).Select(w => "⚠ " + w))
            : "";
    }

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
            Title = "Abrir projeto",
            FileTypeFilter = [ProjectType, FilePickerFileTypes.All],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) TryOpen(path);
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

        var choice = "cancel";
        var dialog = new Window
        {
            Title = "Alterações não salvas",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        Button Make(string text, string result)
        {
            var b = new Button { Content = text };
            b.Click += (_, _) =>
            {
                choice = result;
                dialog.Close();
            };
            return b;
        }
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = "O projeto tem alterações não salvas. Salvar antes de continuar?", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { Make("Salvar", "save"), Make("Descartar", "discard"), Make("Cancelar", "cancel") },
                },
            },
        };
        await dialog.ShowDialog(this);

        return choice switch
        {
            "save" => await SaveAsync(),
            "discard" => true,
            _ => false,
        };
    }
}
