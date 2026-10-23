using Trafego.Core.Model;
using Trafego.Core.Serialization;

namespace Trafego.App.Editor;

public enum Tool { Select, Link, Connector, Input, Measure, Calibrate }

/// <summary>
/// Documento aberto, seleção, ferramenta ativa e histórico de desfazer.
/// As edições mexem direto no modelo e depois chamam <see cref="Checkpoint"/>,
/// que guarda o estado anterior se algo mudou.
/// </summary>
public sealed class EditorState
{
    public Project Project { get; private set; } = new();
    public string? FilePath { get; private set; }
    public bool Dirty { get; private set; }

    public Tool Tool { get; private set; } = Tool.Select;
    public int? SelectedLinkId { get; private set; }
    public int? SelectedConnectorId { get; private set; }
    public int? SelectedInputId { get; private set; }
    /// <summary>Conflito selecionado, pelo par de movimentos (como em <see cref="Conflict"/>).</summary>
    public (int A, int B)? SelectedConflict { get; private set; }
    public int? SelectedJunctionKey { get; private set; }
    public int? SelectedMeasurementId { get; private set; }

    /// <summary>Tipo de medição que a ferramenta Medição cria.</summary>
    public MeasurementKind MeasureKind { get; set; } = MeasurementKind.Count;

    /// <summary>Cor dos links pelos resultados (link → velocidade / máxima, 0 a 1); null = cores normais.</summary>
    public IReadOnlyDictionary<int, double>? LinkOverlay { get; private set; }

    public void SetLinkOverlay(IReadOnlyDictionary<int, double>? overlay)
    {
        LinkOverlay = overlay;
        HighlightChanged?.Invoke();
    }

    /// <summary>Durante a simulação a rede não pode ser editada.</summary>
    public bool ReadOnly { get; private set; }

    /// <summary>Padrões para links novos.</summary>
    public int NewLinkLanes { get; set; } = 1;
    public double NewLinkLaneWidth { get; set; } = Lane.DefaultWidth;

    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private string _current;

    /// <summary>Modelo ou seleção mudou: redesenhar e atualizar o painel.</summary>
    public event Action? Changed;
    public event Action? ToolChanged;
    /// <summary>Foto de fundo trocou (outro arquivo ou outro projeto).</summary>
    public event Action? BackgroundChanged;

    public EditorState()
    {
        _current = ProjectFile.ToJson(Project);
    }

    public Network Network => Project.Network;
    public Link? SelectedLink => SelectedLinkId is { } id ? Network.FindLink(id) : null;
    public Connector? SelectedConnector => SelectedConnectorId is { } id ? Network.FindConnector(id) : null;
    public VehicleInput? SelectedInput => SelectedInputId is { } id ? Network.FindInput(id) : null;
    public Conflict? SelectedConflictInfo =>
        SelectedConflict is { } k ? Analysis.Conflicts.Find(c => c.A == k.A && c.B == k.B) : null;
    public Junction? SelectedJunction => SelectedJunctionKey is { } k ? Analysis.FindJunction(k) : null;
    public Measurement? SelectedMeasurement => SelectedMeasurementId is { } id ? Network.FindMeasurement(id) : null;

    /// <summary>Caminho destacado no mapa (ex.: a rota escolhida na janela de demanda).</summary>
    public NetPath? Highlight { get; private set; }
    public event Action? HighlightChanged;

    public void SetHighlight(NetPath? path)
    {
        Highlight = path;
        HighlightChanged?.Invoke();
    }

    private ConflictAnalysis? _analysis;

    /// <summary>Nós e conflitos da rede atual; recalculado quando a rede muda.</summary>
    public ConflictAnalysis Analysis => _analysis ??= ConflictAnalysis.Of(Network);

    private void ModelChanged()
    {
        _analysis = null;
        Changed?.Invoke();
    }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void SetTool(Tool tool)
    {
        if (Tool == tool || (ReadOnly && tool != Tool.Select)) return;
        Tool = tool;
        ToolChanged?.Invoke();
        Changed?.Invoke();
    }

    public void Select(Link? link) => SetSelection(link: link?.Id);
    public void Select(Connector? c) => SetSelection(connector: c?.Id);
    public void Select(VehicleInput? input) => SetSelection(input: input?.Id);
    public void Select(Conflict? conflict) => SetSelection(conflict: conflict is null ? null : (conflict.A, conflict.B));
    public void SelectJunction(int? key) => SetSelection(junction: key);
    public void Select(Measurement? m) => SetSelection(measurement: m?.Id);
    public void ClearSelection() => SetSelection();

    private void SetSelection(int? link = null, int? connector = null, int? input = null,
        (int, int)? conflict = null, int? junction = null, int? measurement = null)
    {
        SelectedLinkId = link;
        SelectedConnectorId = connector;
        SelectedInputId = input;
        SelectedConflict = conflict;
        SelectedJunctionKey = junction;
        SelectedMeasurementId = measurement;
        Changed?.Invoke();
    }

    public void SetReadOnly(bool value)
    {
        if (ReadOnly == value) return;
        ReadOnly = value;
        if (value) SetTool(Tool.Select);
        Changed?.Invoke();
    }

    /// <summary>Avisa que algo mudou durante uma edição em andamento (ex.: arrastando), sem registrar no histórico.</summary>
    public void Touch() => ModelChanged();

    /// <summary>Registra o estado atual no histórico, se for diferente do último registrado.</summary>
    public void Checkpoint()
    {
        var json = ProjectFile.ToJson(Project);
        if (json == _current)
        {
            ModelChanged();
            return;
        }
        _undo.Push(_current);
        _redo.Clear();
        _current = json;
        Dirty = true;
        ModelChanged();
    }

    public void Undo()
    {
        if (_undo.Count == 0 || ReadOnly) return;
        _redo.Push(_current);
        Restore(_undo.Pop());
    }

    public void Redo()
    {
        if (_redo.Count == 0 || ReadOnly) return;
        _undo.Push(_current);
        Restore(_redo.Pop());
    }

    private void Restore(string json)
    {
        var oldImage = Project.Background?.ImagePath;
        _current = json;
        Project = ProjectFile.FromJson(json);
        Dirty = true;
        if (SelectedLinkId is { } l && Network.FindLink(l) is null) SelectedLinkId = null;
        if (SelectedConnectorId is { } c && Network.FindConnector(c) is null) SelectedConnectorId = null;
        if (SelectedInputId is { } i && Network.FindInput(i) is null) SelectedInputId = null;
        if (SelectedMeasurementId is { } m && Network.FindMeasurement(m) is null) SelectedMeasurementId = null;
        if (Project.Background?.ImagePath != oldImage) BackgroundChanged?.Invoke();
        ModelChanged();
    }

    public void New() => Replace(new Project(), null);

    /// <param name="asTemplate">Abre como modelo: o arquivo original não é sobrescrito ao salvar.</param>
    public void Open(string path, bool asTemplate = false) => Replace(ProjectFile.Load(path), asTemplate ? null : path);

    public void Save(string path)
    {
        ProjectFile.Save(Project, path);
        FilePath = path;
        Dirty = false;
        Changed?.Invoke();
    }

    private void Replace(Project project, string? path)
    {
        Project = project;
        FilePath = path;
        Dirty = false;
        _undo.Clear();
        _redo.Clear();
        _current = ProjectFile.ToJson(Project);
        SelectedLinkId = null;
        SelectedConnectorId = null;
        SelectedInputId = null;
        SelectedConflict = null;
        SelectedJunctionKey = null;
        SelectedMeasurementId = null;
        BackgroundChanged?.Invoke();
        ModelChanged();
    }

    /// <summary>
    /// Troca a foto de fundo. Mantém a escala anterior, se havia, para a rede
    /// continuar no lugar; a recalibração fica com quem trocou.
    /// </summary>
    public void SetBackgroundImage(string path)
    {
        Project.Background ??= new Background();
        Project.Background.ImagePath = path;
        BackgroundChanged?.Invoke();
        Checkpoint();
    }

    public void DeleteSelection()
    {
        if (ReadOnly) return;
        if (SelectedLinkId is { } l) Network.RemoveLink(l);
        else if (SelectedConnectorId is { } c) Network.RemoveConnector(c);
        else if (SelectedInputId is { } i) Network.RemoveInput(i);
        else if (SelectedMeasurementId is { } m) Network.RemoveMeasurement(m);
        else return;
        SelectedLinkId = null;
        SelectedConnectorId = null;
        SelectedInputId = null;
        SelectedConflict = null;
        SelectedJunctionKey = null;
        SelectedMeasurementId = null;
        Network.PruneRules();
        Checkpoint();
    }
}
