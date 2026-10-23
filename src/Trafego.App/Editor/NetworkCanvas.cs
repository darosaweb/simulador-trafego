using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Trafego.Core.Geometry;
using Trafego.Core.Model;
using Trafego.Sumo;

namespace Trafego.App.Editor;

/// <summary>
/// Área de desenho: foto de fundo, rede e as ferramentas de edição.
/// Coordenadas de tela = mundo (m) · Zoom + Pan.
/// </summary>
public sealed class NetworkCanvas : Control
{
    private const double HandleRadius = 5; // px
    private const double HitTolerance = 6; // px
    private const double SnapDistance = 20; // px: perto da ponta, o conector gruda nela
    private const double DragThreshold = 4; // px

    private EditorState _editor = new();
    private double _zoom = 1;
    private Vec2 _pan = Vec2.Zero;

    private Bitmap? _image;
    private string? _imagePath;

    // Ferramenta Link: pontos já clicados do link em construção.
    private readonly List<Vec2> _draft = [];
    // Ferramenta Conector: origem escolhida no primeiro clique.
    private (int LinkId, int Lane, double S)? _connectorFrom;
    // Ferramenta Medição (tempo de viagem): início escolhido no primeiro clique.
    private (int LinkId, double S)? _travelFrom;
    // Ferramenta Calibrar: pontos clicados, em pixels da imagem.
    private Vec2? _calibA;
    private Vec2? _calibB;

    private Vec2 _mouse; // tela
    private LinkHit? _hover;
    private bool _spaceDown;

    private abstract record Drag(Point Start);
    private sealed record PanDrag(Point Start, Vec2 PanAtStart, bool ClearSelectionOnClick) : Drag(Start);
    private sealed record LinkVertexDrag(Point Start, int LinkId, int Index) : Drag(Start);
    private sealed record LinkMoveDrag(Point Start, int LinkId, List<Vec2> Original) : Drag(Start);
    private sealed record ConnectorVertexDrag(Point Start, int ConnectorId, int Index) : Drag(Start);
    private sealed record ConnectorEndDrag(Point Start, int ConnectorId, bool AtFrom) : Drag(Start);
    private sealed record MeasurementDrag(Point Start, int MeasurementId, bool AtEnd) : Drag(Start);
    private Drag? _drag;
    private bool _dragMoved;

    /// <summary>Mensagem para a barra de status.</summary>
    public event Action<string>? Status;
    /// <summary>Posição do mouse no mundo (m).</summary>
    public event Action<Vec2>? CursorMoved;
    /// <summary>Os dois pontos da régua foram marcados (pixels da imagem) — falta a distância real.</summary>
    public event Action<Vec2, Vec2>? CalibrationPicked;

    public NetworkCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    public EditorState Editor
    {
        get => _editor;
        set
        {
            _editor = value;
            _editor.Changed += InvalidateVisual;
            _editor.BackgroundChanged += LoadBackground;
            _editor.ToolChanged += OnToolChanged;
            _editor.HighlightChanged += InvalidateVisual;
            LoadBackground();
            InvalidateVisual();
        }
    }

    public double Zoom => _zoom;

    private SimSnapshot? _snapshot;

    /// <summary>Estado da simulação a desenhar por cima da rede (null = sem simulação).</summary>
    public SimSnapshot? Snapshot
    {
        get => _snapshot;
        set
        {
            _snapshot = value;
            InvalidateVisual();
        }
    }

    private Network Net => _editor.Network;
    private Vec2 ToWorld(Point p) => new((p.X - _pan.X) / _zoom, (p.Y - _pan.Y) / _zoom);
    private Vec2 ToWorld(Vec2 p) => new((p.X - _pan.X) / _zoom, (p.Y - _pan.Y) / _zoom);
    private Point ToScreen(Vec2 w) => new(w.X * _zoom + _pan.X, w.Y * _zoom + _pan.Y);
    private static Point P(Vec2 v) => new(v.X, v.Y);
    private double Px(double screenPixels) => screenPixels / _zoom;

    // ---------------------------------------------------------------- fundo e enquadramento

    private void LoadBackground()
    {
        var path = _editor.Project.Background?.ImagePath;
        if (path == _imagePath) return;
        _image?.Dispose();
        _image = null;
        _imagePath = path;
        if (!string.IsNullOrEmpty(path))
        {
            try
            {
                _image = new Bitmap(path);
            }
            catch (Exception ex)
            {
                Status?.Invoke($"Não foi possível abrir a foto \"{path}\": {ex.Message}");
            }
        }
        FitToContent();
    }

    /// <summary>Enquadra a foto (ou a rede, se não houver foto).</summary>
    public void FitToContent()
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        Vec2 min, max;
        if (_image is not null && _editor.Project.Background is { } bg)
        {
            min = bg.Origin;
            max = bg.ToWorld(new Vec2(_image.PixelSize.Width, _image.PixelSize.Height));
        }
        else if (Net.Links.Count > 0)
        {
            var pts = Net.Links.SelectMany(l => l.Points).ToList();
            min = new Vec2(pts.Min(p => p.X), pts.Min(p => p.Y));
            max = new Vec2(pts.Max(p => p.X), pts.Max(p => p.Y));
        }
        else
        {
            min = new Vec2(-100, -100);
            max = new Vec2(100, 100);
        }
        var size = max - min;
        const double margin = 20;
        _zoom = Math.Min((Bounds.Width - 2 * margin) / Math.Max(size.X, 1), (Bounds.Height - 2 * margin) / Math.Max(size.Y, 1));
        _zoom = Math.Clamp(_zoom, 0.01, 500);
        var center = (min + max) / 2;
        _pan = new Vec2(Bounds.Width / 2 - center.X * _zoom, Bounds.Height / 2 - center.Y * _zoom);
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (e.PreviousSize.Width <= 0 || e.PreviousSize.Height <= 0) FitToContent();
    }

    private void OnToolChanged()
    {
        CancelToolState();
        Cursor = new Cursor(_editor.Tool == Tool.Select ? StandardCursorType.Arrow : StandardCursorType.Cross);
        Status?.Invoke(_editor.Tool switch
        {
            Tool.Link => "Link: clique para marcar o eixo no sentido do tráfego; duplo clique ou Enter termina, botão direito desfaz o último ponto.",
            Tool.Connector => "Conector: clique na faixa de origem (perto do fim do link) e depois na faixa de destino (perto do início).",
            Tool.Input => "Entrada: clique no link onde os veículos entram na rede.",
            Tool.Measure => "Medição: clique no link (contagem: no ponto; fila: na linha de retenção; tempo de viagem: no início e depois no fim).",
            Tool.Calibrate => "Calibrar: clique nas duas pontas de uma distância conhecida na foto.",
            _ => "Selecionar: clique num link ou conector. Arraste pontos para editar; duplo clique insere ponto; botão direito apaga ponto.",
        });
    }

    /// <summary>Esc: abandona o que está em construção na ferramenta atual.</summary>
    public bool CancelToolState()
    {
        var had = _draft.Count > 0 || _connectorFrom is not null || _calibA is not null || _travelFrom is not null;
        _travelFrom = null;
        _draft.Clear();
        _connectorFrom = null;
        _calibA = null;
        _calibB = null;
        InvalidateVisual();
        return had;
    }

    public void ClearCalibrationMarks()
    {
        _calibA = null;
        _calibB = null;
        InvalidateVisual();
    }

    // ---------------------------------------------------------------- entrada

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var p = e.GetPosition(this);
        var before = ToWorld(p);
        _zoom = Math.Clamp(_zoom * Math.Pow(1.2, e.Delta.Y), 0.01, 500);
        _pan = new Vec2(p.X - before.X * _zoom, p.Y - before.Y * _zoom);
        CursorMoved?.Invoke(before);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var pos = e.GetPosition(this);
        var world = ToWorld(pos);
        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        _dragMoved = false;

        if (kind == PointerUpdateKind.MiddleButtonPressed || (kind == PointerUpdateKind.LeftButtonPressed && (_spaceDown || _editor.ReadOnly)))
        {
            BeginDrag(e, new PanDrag(pos, _pan, false));
            return;
        }
        if (_editor.ReadOnly) return;

        switch (_editor.Tool)
        {
            case Tool.Select:
                if (kind == PointerUpdateKind.LeftButtonPressed) SelectPress(e, pos, world);
                else if (kind == PointerUpdateKind.RightButtonPressed) DeleteVertexAt(pos);
                break;
            case Tool.Link:
                if (kind == PointerUpdateKind.LeftButtonPressed)
                {
                    if (e.ClickCount >= 2) FinishLink();
                    else _draft.Add(world);
                }
                else if (kind == PointerUpdateKind.RightButtonPressed && _draft.Count > 0)
                {
                    _draft.RemoveAt(_draft.Count - 1);
                }
                break;
            case Tool.Connector:
                if (kind == PointerUpdateKind.LeftButtonPressed) ConnectorClick(world);
                else if (kind == PointerUpdateKind.RightButtonPressed) _connectorFrom = null;
                break;
            case Tool.Measure:
                if (kind == PointerUpdateKind.LeftButtonPressed) MeasureClick(world);
                else if (kind == PointerUpdateKind.RightButtonPressed) _travelFrom = null;
                break;
            case Tool.Input:
                if (kind == PointerUpdateKind.LeftButtonPressed) InputClick(world);
                break;
            case Tool.Calibrate:
                if (kind == PointerUpdateKind.LeftButtonPressed) CalibrateClick(world);
                break;
        }
        InvalidateVisual();
    }

    private void BeginDrag(PointerPressedEventArgs e, Drag drag)
    {
        _drag = drag;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        _mouse = new Vec2(pos.X, pos.Y);
        var world = ToWorld(pos);
        CursorMoved?.Invoke(world);

        if (_drag is not null)
        {
            var d = pos - _drag.Start;
            if (!_dragMoved && Math.Sqrt(d.X * d.X + d.Y * d.Y) < DragThreshold) return;
            _dragMoved = true;
            ContinueDrag(_drag, pos, world);
            InvalidateVisual();
            return;
        }

        _hover = _editor.Tool == Tool.Connector ? HitTest.Link(Net, world, Px(HitTolerance)) : null;
        if (_editor.Tool != Tool.Select || _draft.Count > 0) InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is null) return;
        var drag = _drag;
        _drag = null;
        e.Pointer.Capture(null);

        switch (drag)
        {
            case PanDrag { ClearSelectionOnClick: true } when !_dragMoved:
                _editor.ClearSelection();
                break;
            case LinkVertexDrag or LinkMoveDrag when _dragMoved:
                var linkId = drag is LinkVertexDrag v ? v.LinkId : ((LinkMoveDrag)drag).LinkId;
                if (Net.FindLink(linkId) is { } link) Net.FitConnectorsTo(link);
                _editor.Checkpoint();
                break;
            case ConnectorVertexDrag or ConnectorEndDrag or MeasurementDrag when _dragMoved:
                _editor.Checkpoint();
                break;
        }
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Space)
        {
            _spaceDown = true;
            Cursor = new Cursor(StandardCursorType.Hand);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _editor.Tool == Tool.Link)
        {
            FinishLink();
            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space)
        {
            _spaceDown = false;
            Cursor = new Cursor(_editor.Tool == Tool.Select ? StandardCursorType.Arrow : StandardCursorType.Cross);
        }
    }

    // ---------------------------------------------------------------- ferramenta Selecionar

    private void SelectPress(PointerPressedEventArgs e, Point pos, Vec2 world)
    {
        // 1) Alças do objeto selecionado.
        if (_editor.SelectedLink is { } sel)
        {
            var i = HandleAt(sel.Points, pos);
            if (i >= 0)
            {
                if (e.ClickCount < 2) BeginDrag(e, new LinkVertexDrag(pos, sel.Id, i));
                return;
            }
        }
        if (_editor.SelectedConnector is { } sc && Net.GeometryOf(sc) is { } g)
        {
            var i = HandleAt(sc.Points, pos);
            if (i >= 0)
            {
                BeginDrag(e, new ConnectorVertexDrag(pos, sc.Id, i));
                return;
            }
            if (Near(g.Axis.Points[0], pos))
            {
                BeginDrag(e, new ConnectorEndDrag(pos, sc.Id, AtFrom: true));
                return;
            }
            if (Near(g.Axis.Points[^1], pos))
            {
                BeginDrag(e, new ConnectorEndDrag(pos, sc.Id, AtFrom: false));
                return;
            }
        }

        // 2) Medições (marcadores sobre os links), marcadores de conflito e rótulos de nó, entradas (no início dos links),
        //    depois conectores, que ficam por cima dos links.
        foreach (var m in Net.Measurements)
        {
            foreach (var (point, atEnd) in MeasurementPoints(m))
            {
                if (!Near(point, pos)) continue;
                _editor.Select(m);
                BeginDrag(e, new MeasurementDrag(pos, m.Id, atEnd));
                return;
            }
        }
        foreach (var conflict in _editor.Analysis.Conflicts)
        {
            if (Near(conflict.Point, pos))
            {
                _editor.Select(conflict);
                return;
            }
        }
        foreach (var j in _editor.Analysis.Junctions)
        {
            if (JunctionLabelRect(j) is { } r && r.Contains(pos))
            {
                _editor.SelectJunction(j.Key);
                return;
            }
        }
        foreach (var input in Net.Inputs)
        {
            if (InputMarker(input) is { } m && Near(m, pos))
            {
                _editor.Select(input);
                return;
            }
        }
        var tol = Px(HitTolerance);
        if (HitTest.Connector(Net, world, tol) is { } ch)
        {
            if (e.ClickCount >= 2 && ch.Connector.Id == _editor.SelectedConnectorId)
            {
                InsertConnectorPoint(ch.Connector, world);
                return;
            }
            _editor.Select(ch.Connector);
            return;
        }
        if (HitTest.Link(Net, world, tol) is { } lh)
        {
            if (e.ClickCount >= 2 && lh.Link.Id == _editor.SelectedLinkId)
            {
                InsertLinkPoint(lh.Link, world);
                return;
            }
            if (lh.Link.Id == _editor.SelectedLinkId)
            {
                BeginDrag(e, new LinkMoveDrag(pos, lh.Link.Id, [.. lh.Link.Points]));
                return;
            }
            _editor.Select(lh.Link);
            return;
        }

        // 3) Espaço vazio: arrasta a vista; clique simples tira a seleção.
        BeginDrag(e, new PanDrag(pos, _pan, ClearSelectionOnClick: true));
    }

    private void ContinueDrag(Drag drag, Point pos, Vec2 world)
    {
        switch (drag)
        {
            case PanDrag p:
                _pan = p.PanAtStart + new Vec2(pos.X - p.Start.X, pos.Y - p.Start.Y);
                break;
            case LinkVertexDrag v when Net.FindLink(v.LinkId) is { } link:
                link.Points[v.Index] = world;
                _editor.Touch();
                break;
            case LinkMoveDrag m when Net.FindLink(m.LinkId) is { } link:
                var delta = ToWorld(pos) - ToWorld(m.Start);
                link.Points = [.. m.Original.Select(p => p + delta)];
                _editor.Touch();
                break;
            case ConnectorVertexDrag cv when Net.FindConnector(cv.ConnectorId) is { } c:
                c.Points[cv.Index] = world;
                _editor.Touch();
                break;
            case MeasurementDrag md when Net.FindMeasurement(md.MeasurementId) is { } m:
                var onLink = Net.FindLink(md.AtEnd ? m.ToLinkId : m.LinkId);
                if (onLink?.Axis().Project(world) is { } mp)
                {
                    if (md.AtEnd) m.ToS = mp.S;
                    else m.S = mp.S;
                    _editor.Touch();
                }
                break;
            case ConnectorEndDrag ce when Net.FindConnector(ce.ConnectorId) is { } c:
                var target = Net.FindLink(ce.AtFrom ? c.FromLinkId : c.ToLinkId);
                if (target?.Axis().Project(world) is { } proj)
                {
                    if (ce.AtFrom) c.FromS = proj.S;
                    else c.ToS = proj.S;
                    _editor.Touch();
                }
                break;
        }
    }

    private void DeleteVertexAt(Point pos)
    {
        if (_editor.SelectedLink is { } link)
        {
            var i = HandleAt(link.Points, pos);
            if (i < 0) return;
            if (link.Points.Count <= 2)
            {
                Status?.Invoke("Um link precisa de pelo menos 2 pontos. Para apagar o link, use Delete.");
                return;
            }
            link.Points.RemoveAt(i);
            Net.FitConnectorsTo(link);
            _editor.Checkpoint();
        }
        else if (_editor.SelectedConnector is { } c)
        {
            var i = HandleAt(c.Points, pos);
            if (i < 0) return;
            c.Points.RemoveAt(i);
            _editor.Checkpoint();
        }
    }

    private void InsertLinkPoint(Link link, Vec2 world)
    {
        // Insere no lado do polígono de controle mais próximo, que é o que o usuário vê
        // como "trecho" mesmo quando o link é suave.
        if (new Polyline(link.Points).Project(world) is not { } proj) return;
        link.Points.Insert(proj.Segment + 1, world);
        _editor.Checkpoint();
    }

    private void InsertConnectorPoint(Connector c, Vec2 world)
    {
        if (Net.GeometryOf(c) is not { } g || g.Axis.Project(world) is not { } at) return;
        // Mantém os pontos na ordem em que aparecem ao longo do conector.
        var index = c.Points.Count(p => (g.Axis.Project(p)?.S ?? 0) < at.S);
        c.Points.Insert(index, world);
        _editor.Checkpoint();
    }

    private int HandleAt(IReadOnlyList<Vec2> points, Point pos)
    {
        for (var i = points.Count - 1; i >= 0; i--)
            if (Near(points[i], pos)) return i;
        return -1;
    }

    private bool Near(Vec2 world, Point pos)
    {
        var s = ToScreen(world);
        return Math.Abs(s.X - pos.X) <= HandleRadius + 3 && Math.Abs(s.Y - pos.Y) <= HandleRadius + 3;
    }

    // ---------------------------------------------------------------- ferramentas Link, Conector e Calibrar

    private void FinishLink()
    {
        // Duplo clique: o primeiro clique já marcou um ponto no mesmo lugar.
        while (_draft.Count >= 2 && Vec2.Distance(_draft[^1], _draft[^2]) < Px(DragThreshold))
            _draft.RemoveAt(_draft.Count - 1);
        if (_draft.Count < 2)
        {
            Status?.Invoke("Um link precisa de pelo menos 2 pontos.");
            return;
        }
        var link = Net.AddLink(_draft, _editor.NewLinkLanes, _editor.NewLinkLaneWidth);
        _draft.Clear();
        _editor.Select(link);
        _editor.Checkpoint();
        Status?.Invoke($"{link.Name} criado com {link.Lanes.Count} faixa(s), {link.Axis().Length:0.0} m.");
    }

    private void ConnectorClick(Vec2 world)
    {
        if (HitTest.Link(Net, world, Px(HitTolerance)) is not { } hit)
        {
            Status?.Invoke("Clique sobre uma faixa de um link.");
            return;
        }
        var len = hit.Link.Axis().Length;
        var snap = Px(SnapDistance);

        if (_connectorFrom is not { } from)
        {
            var s = hit.Projection.S > len - snap ? len : hit.Projection.S;
            _connectorFrom = (hit.Link.Id, hit.Lane, s);
            Status?.Invoke($"Origem: {hit.Link.Name}, faixa {hit.Lane + 1}. Agora clique na faixa de destino.");
            return;
        }

        if (hit.Link.Id == from.LinkId)
        {
            Status?.Invoke("O destino precisa ser outro link.");
            return;
        }
        var fromLink = Net.FindLink(from.LinkId);
        if (fromLink is null)
        {
            _connectorFrom = null;
            return;
        }
        var toS = hit.Projection.S < snap ? 0 : hit.Projection.S;
        var c = Net.AddConnector(fromLink, from.Lane, from.S, hit.Link, hit.Lane, toS);
        _connectorFrom = null;
        _editor.Select(c);
        _editor.Checkpoint();
        Status?.Invoke($"{c.Name}: {fromLink.Name} faixa {from.Lane + 1} → {hit.Link.Name} faixa {hit.Lane + 1}.");
    }

    private void MeasureClick(Vec2 world)
    {
        if (HitTest.Link(Net, world, Px(HitTolerance)) is not { } hit)
        {
            Status?.Invoke("Clique sobre um link.");
            return;
        }
        var kind = _editor.MeasureKind;
        var s = hit.Projection.S;
        if (kind == MeasurementKind.Queue && hit.Link.Axis().Length - s < Px(SnapDistance))
            s = hit.Link.Axis().Length; // fila na linha de retenção: gruda no fim do link
        if (kind == MeasurementKind.TravelTime && _travelFrom is null)
        {
            _travelFrom = (hit.Link.Id, s);
            Status?.Invoke("Início marcado. Agora clique no fim do trecho do tempo de viagem.");
            return;
        }
        Measurement m;
        if (kind == MeasurementKind.TravelTime && _travelFrom is { } from && Net.FindLink(from.LinkId) is { } fromLink)
        {
            m = Net.AddMeasurement(kind, fromLink, from.S);
            m.ToLinkId = hit.Link.Id;
            m.ToS = s;
            _travelFrom = null;
        }
        else
        {
            m = Net.AddMeasurement(kind, hit.Link, s);
        }
        _editor.Select(m);
        _editor.Checkpoint();
        Status?.Invoke($"{m.Name} criada em {hit.Link.Name}.");
    }

    /// <summary>Pontos de uma medição no mapa: o início e, no tempo de viagem, o fim.</summary>
    private IEnumerable<(Vec2 Point, bool AtEnd)> MeasurementPoints(Measurement m)
    {
        if (Net.FindLink(m.LinkId) is { Points.Count: >= 2 } link) yield return (link.Axis().At(m.S).Point, false);
        if (m.Kind == MeasurementKind.TravelTime && Net.FindLink(m.ToLinkId) is { Points.Count: >= 2 } to)
            yield return (to.Axis().At(m.ToS).Point, true);
    }

    private void InputClick(Vec2 world)
    {
        if (HitTest.Link(Net, world, Px(HitTolerance)) is not { } hit)
        {
            Status?.Invoke("Clique sobre o link onde os veículos entram na rede.");
            return;
        }
        var input = Net.AddInput(hit.Link);
        _editor.Select(input);
        _editor.Checkpoint();
        Status?.Invoke($"{input.Name} no início de {hit.Link.Name}: {input.VolumeVph:0} veíc/h. Ajuste o volume no painel.");
    }

    /// <summary>Onde o marcador da entrada é desenhado: no início do link.</summary>
    private Vec2? InputMarker(VehicleInput input) =>
        Net.FindLink(input.LinkId) is { Points.Count: >= 2 } link ? link.Axis().At(0).Point : null;

    private void CalibrateClick(Vec2 world)
    {
        if (_editor.Project.Background is not { } bg || _image is null)
        {
            Status?.Invoke("Abra uma foto antes de calibrar.");
            return;
        }
        var pixel = bg.ToPixel(world);
        if (_calibA is null || _calibB is not null)
        {
            _calibA = pixel;
            _calibB = null;
            Status?.Invoke("Agora clique na outra ponta da distância conhecida.");
        }
        else
        {
            _calibB = pixel;
            CalibrationPicked?.Invoke(_calibA.Value, pixel);
        }
    }

    // ---------------------------------------------------------------- desenho

    private static readonly IBrush Backdrop = new SolidColorBrush(Color.Parse("#15181d"));
    private static readonly IBrush LinkFill = new SolidColorBrush(Color.FromArgb(175, 62, 70, 82));
    private static readonly IBrush ConnectorFill = new SolidColorBrush(Color.FromArgb(175, 104, 78, 148));
    private static readonly IBrush DraftFill = new SolidColorBrush(Color.FromArgb(140, 255, 159, 28));
    private static readonly IBrush HoverFill = new SolidColorBrush(Color.FromArgb(150, 76, 194, 255));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#4cc2ff"));
    private static readonly IBrush Orange = new SolidColorBrush(Color.Parse("#ff9f1c"));
    private static readonly IBrush EdgeBrush = new SolidColorBrush(Color.FromArgb(220, 235, 238, 242));
    private static readonly IBrush ArrowBrush = new SolidColorBrush(Color.FromArgb(200, 235, 238, 242));
    private static readonly IBrush HandleFill = Brushes.White;
    private static readonly IBrush LabelBack = new SolidColorBrush(Color.FromArgb(200, 21, 24, 29));

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Backdrop, new Rect(Bounds.Size));

        using (ctx.PushTransform(Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_pan.X, _pan.Y)))
        {
            if (_image is not null && _editor.Project.Background is { } bg)
            {
                var size = new Vec2(_image.PixelSize.Width, _image.PixelSize.Height) * bg.MetersPerPixel;
                ctx.DrawImage(_image, new Rect(_image.Size), new Rect(bg.Origin.X, bg.Origin.Y, size.X, size.Y));
            }

            foreach (var link in Net.Links) DrawLink(ctx, link);
            foreach (var c in Net.Connectors) DrawConnector(ctx, c);

            if (_hover is { } h) DrawLaneHighlight(ctx, h.Link, h.Lane);
            if (_connectorFrom is { } from && Net.FindLink(from.LinkId) is { } fl) DrawLaneHighlight(ctx, fl, from.Lane);
            DrawDraft(ctx);
            DrawConflictAreas(ctx);
            DrawHighlight(ctx);
            DrawVehicles(ctx);
            DrawSignalHeads(ctx);
        }

        DrawExits(ctx);

        DrawJunctions(ctx);
        DrawConflictMarkers(ctx);
        DrawMeasurements(ctx);
        DrawInputs(ctx);
        DrawHandles(ctx);
        DrawConnectorPreview(ctx);
        DrawCalibration(ctx);
        DrawScaleBar(ctx);
    }

    private static StreamGeometry Shape(IReadOnlyList<Vec2> pts, bool closed)
    {
        var geo = new StreamGeometry();
        using var g = geo.Open();
        if (pts.Count == 0) return geo;
        g.BeginFigure(P(pts[0]), closed);
        for (var i = 1; i < pts.Count; i++) g.LineTo(P(pts[i]));
        g.EndFigure(closed);
        return geo;
    }

    /// <summary>Faixa de largura entre dois deslocamentos laterais do eixo.</summary>
    private static StreamGeometry Ribbon(IReadOnlyList<Vec2> axis, double right, double left)
    {
        var l = Polyline.Offset(axis, left);
        var r = Polyline.Offset(axis, right);
        r.Reverse();
        return Shape([.. l, .. r], closed: true);
    }

    private void DrawLink(DrawingContext ctx, Link link)
    {
        if (link.Points.Count < 2) return;
        var axis = link.Axis().Points;
        var half = link.TotalWidth / 2;
        var selected = link.Id == _editor.SelectedLinkId;
        var fill = _editor.LinkOverlay is { } overlay && overlay.TryGetValue(link.Id, out var ratio) && double.IsFinite(ratio)
            ? SpeedRatioBrush(ratio)
            : LinkFill;
        ctx.DrawGeometry(fill, new Pen(selected ? Accent : EdgeBrush, Px(selected ? 2.5 : 1)), Ribbon(axis, -half, half));

        var dash = new Pen(EdgeBrush, Px(1), new DashStyle([6, 6], 0));
        for (var i = 0; i < link.Lanes.Count - 1; i++)
            ctx.DrawGeometry(null, dash, Shape(Polyline.Offset(axis, link.LaneLeftEdge(i)), closed: false));

        DrawArrows(ctx, link.Axis(), [.. Enumerable.Range(0, link.Lanes.Count).Select(link.LaneCenter)],
            [.. link.Lanes.Select(l => l.Width)]);
    }

    private void DrawConnector(DrawingContext ctx, Connector c)
    {
        if (Net.GeometryOf(c) is not { } g) return;
        var axis = g.Axis.Points;
        var half = g.TotalWidth / 2;
        var selected = c.Id == _editor.SelectedConnectorId;
        ctx.DrawGeometry(ConnectorFill, new Pen(selected ? Accent : EdgeBrush, Px(selected ? 2.5 : 0.8)), Ribbon(axis, -half, half));

        var dash = new Pen(EdgeBrush, Px(1), new DashStyle([3, 5], 0));
        for (var i = 0; i < g.LaneOffsets.Length - 1; i++)
            ctx.DrawGeometry(null, dash, Shape(Polyline.Offset(axis, g.LaneOffsets[i] + g.LaneWidths[i] / 2), closed: false));

        DrawArrows(ctx, g.Axis, g.LaneOffsets, g.LaneWidths);
    }

    /// <summary>Setas de sentido no centro de cada faixa, espaçadas ao longo do eixo.</summary>
    private void DrawArrows(DrawingContext ctx, Polyline axis, double[] centers, double[] widths)
    {
        var len = axis.Length;
        if (len <= 0) return;
        // Espaçamento em metros que fica em torno de 120 px na tela.
        var spacing = Math.Max(Px(120), 5);
        var count = Math.Max(1, (int)(len / spacing));
        for (var k = 0; k < count; k++)
        {
            var s = len * (k + 0.5) / count;
            var at = axis.At(s);
            var n = at.Tangent.LeftNormal();
            for (var i = 0; i < centers.Length; i++)
            {
                var size = Math.Min(widths[i] * 0.3, Px(10));
                var tip = at.Point + n * centers[i] + at.Tangent * size;
                var back = tip - at.Tangent * (2 * size);
                ctx.DrawGeometry(ArrowBrush, null, Shape([tip, back + n * size, back - n * size], closed: true));
            }
        }
    }

    private static readonly IBrush InputFill = new SolidColorBrush(Color.Parse("#2ec4b6"));
    private static readonly IBrush PriorityFill = new SolidColorBrush(Color.FromArgb(190, 46, 204, 113));
    private static readonly IBrush YieldFill = new SolidColorBrush(Color.FromArgb(190, 231, 76, 60));
    private static readonly IBrush AutoFill = new SolidColorBrush(Color.FromArgb(170, 241, 196, 15));
    private static readonly IBrush LevelFill = new SolidColorBrush(Color.FromArgb(90, 160, 170, 180));
    private static readonly IBrush UnmodeledFill = new SolidColorBrush(Color.FromArgb(190, 255, 140, 0));
    private static readonly IBrush JunctionFill = new SolidColorBrush(Color.FromArgb(28, 76, 194, 255));

    /// <summary>Cores das duas pontas do conflito: (movimento A, movimento B, marcador).</summary>
    private (IBrush A, IBrush B, IBrush Marker) ConflictColors(Conflict c)
    {
        var p = Net.PriorityOf(c.A, c.B);
        if (p == ConflictPriority.None) return (LevelFill, LevelFill, LevelFill);
        if (c.Kind == ConflictKind.LinkCrossing) return (UnmodeledFill, UnmodeledFill, UnmodeledFill);
        return p switch
        {
            ConflictPriority.A => (PriorityFill, YieldFill, PriorityFill),
            ConflictPriority.B => (YieldFill, PriorityFill, PriorityFill),
            _ => (AutoFill, AutoFill, AutoFill),
        };
    }

    /// <summary>Eixo e largura de um movimento do conflito (conector, link ou fluxo principal).</summary>
    private (Polyline Axis, double Width)? MovementShape(Conflict c, int movement)
    {
        if (movement == ConflictRule.MainLine)
            return c.MainLinkId is { } id && Net.FindLink(id) is { } main ? (main.Axis(), main.TotalWidth) : null;
        if (Net.FindConnector(movement) is { } conn && Net.GeometryOf(conn) is { } g) return (g.Axis, g.TotalWidth);
        if (Net.FindLink(movement) is { Points.Count: >= 2 } link) return (link.Axis(), link.TotalWidth);
        return null;
    }

    /// <summary>Áreas de conflito como no Vissim: verde tem preferência, vermelho espera, amarelo o SUMO decide.</summary>
    private void DrawConflictAreas(DrawingContext ctx)
    {
        foreach (var c in _editor.Analysis.Conflicts)
        {
            var (fa, fb, _) = ConflictColors(c);
            foreach (var (movement, at, fill) in new[] { (c.A, c.SA, fa), (c.B, c.SB, fb) })
            {
                if (MovementShape(c, movement) is not { } m) continue;
                var half = m.Width / 2;
                var slice = m.Axis.Slice(at - 4, at + 4);
                if (slice.Count >= 2) ctx.DrawGeometry(fill, null, Ribbon(slice, -half, half));
            }
        }
    }

    private void DrawConflictMarkers(DrawingContext ctx)
    {
        var selected = _editor.SelectedConflict;
        foreach (var c in _editor.Analysis.Conflicts)
        {
            var (_, _, marker) = ConflictColors(c);
            var isSel = selected is { } k && k.A == c.A && k.B == c.B;
            ctx.DrawEllipse(marker, new Pen(isSel ? Accent : Brushes.White, isSel ? 3 : 1.2), ToScreen(c.Point), 5, 5);
        }
    }

    private Rect? JunctionLabelRect(Junction j)
    {
        if (j.Outline.Count == 0) return null;
        var top = j.Outline.MinBy(p => p.Y);
        var s = ToScreen(top);
        return new Rect(s.X - 28, s.Y - 26, 56, 18);
    }

    private void DrawJunctions(DrawingContext ctx)
    {
        foreach (var j in _editor.Analysis.Junctions)
        {
            if (j.Outline.Count < 3) continue;
            var selected = j.Key == _editor.SelectedJunctionKey;
            var pts = j.Outline.Select(p => { var s = ToScreen(p); return new Vec2(s.X, s.Y); }).ToList();
            ctx.DrawGeometry(selected ? JunctionFill : null,
                new Pen(Accent, selected ? 2.5 : 1.2, new DashStyle([5, 4], 0)), Shape(pts, closed: true));
            if (JunctionLabelRect(j) is { } r)
            {
                var suffix = Net.ControlOf(j.Key) switch
                {
                    JunctionControl.RightBeforeLeft => " · dir.",
                    JunctionControl.AllWayStop => " · pare",
                    _ => "",
                };
                DrawLabelAt(ctx, $"Nó {j.Key}{suffix}", r.Center);
            }
        }
    }

    private static readonly IBrush HeadIdle = new SolidColorBrush(Color.Parse("#d0d6de"));
    private static readonly IBrush HeadGreen = new SolidColorBrush(Color.Parse("#2ecc71"));
    private static readonly IBrush HeadAmber = new SolidColorBrush(Color.Parse("#f1c40f"));
    private static readonly IBrush HeadRed = new SolidColorBrush(Color.Parse("#e74c3c"));

    /// <summary>
    /// Cabeças semafóricas: uma barra atravessando o início do conector. Na simulação, a cor é a do
    /// grupo naquele instante; editando, cinza com o número do grupo.
    /// </summary>
    private void DrawSignalHeads(DrawingContext ctx)
    {
        foreach (var c in Net.Connectors)
        {
            if (c.Signal is not { } s || Net.GeometryOf(c) is not { } g || Net.FindController(s.ControllerId) is not { } sc) continue;
            var start = g.Axis.At(0.6);
            var n = start.Tangent.LeftNormal() * (g.TotalWidth / 2);
            var brush = HeadIdle;
            var index = sc.Groups.FindIndex(x => x.Id == s.GroupId);
            if (_snapshot?.Signals is { } states && states.TryGetValue(SumoExport.TrafficLightId(sc.Id), out var state) &&
                index >= 0 && index < state.Length)
            {
                brush = char.ToLowerInvariant(state[index]) switch
                {
                    'g' => HeadGreen,
                    'y' => HeadAmber,
                    _ => HeadRed,
                };
            }
            ctx.DrawLine(new Pen(Brushes.Black, Math.Max(1.2, Px(6))), P(start.Point + n), P(start.Point - n));
            ctx.DrawLine(new Pen(brush, Math.Max(0.8, Px(4))), P(start.Point + n), P(start.Point - n));
        }
    }

    private static readonly IBrush HighlightFill = new SolidColorBrush(Color.FromArgb(150, 255, 140, 0));

    /// <summary>Rota destacada: faixa laranja por cima dos trechos e conectores que ela usa.</summary>
    private void DrawHighlight(DrawingContext ctx)
    {
        if (_editor.Highlight is not { } path) return;
        var topo = _editor.Analysis.Topology;
        foreach (var step in path.Steps)
        {
            switch (step)
            {
                case LinkStep ls when Net.FindLink(ls.LinkId) is { } link && topo.Has(link.Id) && ls.Piece < topo.PieceCount(link.Id):
                    var cuts = topo.Cuts(link.Id);
                    var slice = link.Axis().Slice(cuts[ls.Piece], cuts[ls.Piece + 1]);
                    if (slice.Count >= 2) ctx.DrawGeometry(HighlightFill, null, Ribbon(slice, -link.TotalWidth / 2 - 0.5, link.TotalWidth / 2 + 0.5));
                    break;
                case ConnectorStep cs when Net.FindConnector(cs.ConnectorId) is { } c && Net.GeometryOf(c) is { } g:
                    ctx.DrawGeometry(HighlightFill, null, Ribbon(g.Axis.Points, -g.TotalWidth / 2 - 0.5, g.TotalWidth / 2 + 0.5));
                    break;
            }
        }
    }

    /// <summary>Saídas da rede (destinos da matriz O/D): um quadrado no fim do link, quando há entradas.</summary>
    private void DrawExits(DrawingContext ctx)
    {
        if (Net.Inputs.Count == 0) return;
        foreach (var exit in RouteFinder.Exits(_editor.Analysis.Topology))
        {
            var s = ToScreen(exit.Axis().At(exit.Axis().Length).Point);
            ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#e0457b")), new Pen(Brushes.White, 1.5), new Rect(s.X - 6, s.Y - 6, 12, 12));
        }
    }

    private static readonly IBrush CountBrush = new SolidColorBrush(Color.Parse("#00bbf9"));
    private static readonly IBrush QueueBrush = new SolidColorBrush(Color.Parse("#ff5d73"));
    private static readonly IBrush QueueArea = new SolidColorBrush(Color.FromArgb(70, 255, 93, 115));
    private static readonly IBrush TravelBrush = new SolidColorBrush(Color.Parse("#9be564"));

    /// <summary>Cor de um link pela velocidade relativa: vermelho (parado) → amarelo → verde (na máxima).</summary>
    private static IBrush SpeedRatioBrush(double ratio)
    {
        var t = Math.Clamp(ratio, 0, 1);
        var r = t < 0.5 ? 1.0 : 2 - 2 * t;
        var g = t < 0.5 ? 2 * t : 1.0;
        return new SolidColorBrush(Color.FromArgb(200, (byte)(220 * r), (byte)(200 * g), 50));
    }

    /// <summary>
    /// Medições: contagem (barra azul), fila (barra vermelha com a área medida a montante) e tempo
    /// de viagem (barra verde no início e no fim). Barras atravessam o link na posição da medição.
    /// </summary>
    private void DrawMeasurements(DrawingContext ctx)
    {
        foreach (var m in Net.Measurements)
        {
            if (Net.FindLink(m.LinkId) is not { Points.Count: >= 2 } link) continue;
            var selected = m.Id == _editor.SelectedMeasurementId;
            var brush = m.Kind switch
            {
                MeasurementKind.Count => CountBrush,
                MeasurementKind.Queue => QueueBrush,
                _ => TravelBrush,
            };

            if (m.Kind == MeasurementKind.Queue)
            {
                var slice = link.Axis().Slice(m.S - m.QueueLength, m.S);
                if (slice.Count >= 2)
                {
                    var screen = slice.Select(p => { var s = ToScreen(p); return new Vec2(s.X, s.Y); }).ToList();
                    var half = link.TotalWidth / 2 * _zoom;
                    ctx.DrawGeometry(QueueArea, null, Ribbon(screen, -half, half));
                }
            }

            void Bar(Link l, double s)
            {
                var at = l.Axis().At(s);
                var n = at.Tangent.LeftNormal() * (l.TotalWidth / 2 + 0.5);
                var a = ToScreen(at.Point + n);
                var b = ToScreen(at.Point - n);
                ctx.DrawLine(new Pen(selected ? Accent : Brushes.Black, selected ? 7 : 6), a, b);
                ctx.DrawLine(new Pen(brush, 3.5), a, b);
            }

            Bar(link, m.S);
            if (m.Kind == MeasurementKind.TravelTime && Net.FindLink(m.ToLinkId) is { Points.Count: >= 2 } to)
            {
                Bar(to, m.ToS);
                if (selected)
                    ctx.DrawLine(new Pen(TravelBrush, 1.5, new DashStyle([4, 4], 0)),
                        ToScreen(link.Axis().At(m.S).Point), ToScreen(to.Axis().At(m.ToS).Point));
            }
            if (selected) DrawLabel(ctx, m.Name, link.Axis().At(m.S).Point);
        }

        if (_travelFrom is { } from && Net.FindLink(from.LinkId) is { } fl)
        {
            var p = ToScreen(fl.Axis().At(from.S).Point);
            ctx.DrawLine(new Pen(TravelBrush, 2, new DashStyle([4, 4], 0)), p, P(_mouse));
            ctx.DrawEllipse(TravelBrush, null, p, 4, 4);
        }
    }

    /// <summary>Entradas: círculo no início do link com o volume ao lado.</summary>
    private void DrawInputs(DrawingContext ctx)
    {
        foreach (var input in Net.Inputs)
        {
            if (InputMarker(input) is not { } m) continue;
            var s = ToScreen(m);
            var selected = input.Id == _editor.SelectedInputId;
            ctx.DrawEllipse(InputFill, new Pen(selected ? Accent : Brushes.White, selected ? 3 : 1.5), s, 8, 8);
            var label = Net.Demand.UsesMatrix(input.Id) ? "matriz O/D" : Net.Demand.SliceStarts.Count > 1 || input.Volumes.Count > 0 ? "perfil horário" : $"{input.VolumeVph:0} veíc/h";
            DrawLabelAt(ctx, label, new Point(s.X, s.Y + 20));
        }
    }

    /// <summary>Veículos da simulação, coloridos pela velocidade (vermelho parado → verde a 60 km/h ou mais).</summary>
    private void DrawVehicles(DrawingContext ctx)
    {
        if (_snapshot is null) return;
        var outline = new Pen(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), Px(1));
        foreach (var v in _snapshot.Vehicles)
        {
            var f = new Vec2(Math.Cos(v.Heading), Math.Sin(v.Heading));
            var n = new Vec2(-f.Y, f.X);
            var c = new Vec2(v.X, v.Y);
            var hl = f * (v.Length / 2);
            var hw = n * (v.Width / 2);
            var body = Shape([c + hl + hw, c + hl - hw, c - hl - hw, c - hl + hw], closed: true);
            ctx.DrawGeometry(SpeedBrush(v.Speed, v.Truck), outline, body);
        }
    }

    private static IBrush SpeedBrush(double speed, bool truck)
    {
        var t = Math.Clamp(speed / (60 / 3.6), 0, 1);
        // Vermelho → amarelo → verde.
        var r = t < 0.5 ? 1.0 : 2 - 2 * t;
        var g = t < 0.5 ? 2 * t : 1.0;
        var k = truck ? 0.75 : 1.0;
        return new SolidColorBrush(Color.FromRgb((byte)(230 * r * k), (byte)(210 * g * k), (byte)(60 * k)));
    }

    private void DrawLaneHighlight(DrawingContext ctx, Link link, int lane)
    {
        if (link.Points.Count < 2 || lane >= link.Lanes.Count) return;
        ctx.DrawGeometry(HoverFill, null, Ribbon(link.Axis().Points, link.LaneRightEdge(lane), link.LaneLeftEdge(lane)));
    }

    private void DrawDraft(DrawingContext ctx)
    {
        if (_editor.Tool != Tool.Link || _draft.Count == 0) return;
        List<Vec2> pts = [.. _draft, ToWorld(_mouse)];
        var half = _editor.NewLinkLanes * _editor.NewLinkLaneWidth / 2;
        ctx.DrawGeometry(DraftFill, new Pen(Orange, Px(1.5)), Ribbon(pts, -half, half));
        ctx.DrawGeometry(null, new Pen(Orange, Px(1.5), new DashStyle([4, 4], 0)), Shape(pts, closed: false));
    }

    private void DrawHandles(DrawingContext ctx)
    {
        var pen = new Pen(Accent, 1.5);
        void Square(Vec2 w)
        {
            var s = ToScreen(w);
            ctx.DrawRectangle(HandleFill, pen, new Rect(s.X - HandleRadius, s.Y - HandleRadius, 2 * HandleRadius, 2 * HandleRadius));
        }
        void Dot(Vec2 w) => ctx.DrawEllipse(Accent, new Pen(HandleFill, 1.5), ToScreen(w), HandleRadius, HandleRadius);

        if (_editor.SelectedLink is { } link)
        {
            if (link.Smooth)
                ctx.DrawGeometry(null, new Pen(Accent, 1, new DashStyle([3, 3], 0)),
                    Shape([.. link.Points.Select(p => new Vec2(ToScreen(p).X, ToScreen(p).Y))], closed: false));
            foreach (var p in link.Points) Square(p);
            DrawLabel(ctx, $"{link.Name} · {link.Axis().Length:0.0} m", link.Axis().At(link.Axis().Length / 2).Point);
        }
        if (_editor.SelectedConnector is { } c && Net.GeometryOf(c) is { } g)
        {
            Dot(g.Axis.Points[0]);
            Dot(g.Axis.Points[^1]);
            foreach (var p in c.Points) Square(p);
            DrawLabel(ctx, $"{c.Name} · {g.Axis.Length:0.0} m", g.Axis.At(g.Axis.Length / 2).Point);
        }
        foreach (var p in _draft) Square(p);
    }

    private void DrawConnectorPreview(DrawingContext ctx)
    {
        if (_connectorFrom is not { } from || Net.FindLink(from.LinkId) is not { } link) return;
        var at = link.Axis().At(from.S);
        var start = at.Point + at.Tangent.LeftNormal() * link.LaneCenter(from.Lane);
        ctx.DrawLine(new Pen(Accent, 2, new DashStyle([4, 4], 0)), ToScreen(start), P(_mouse));
        ctx.DrawEllipse(Accent, null, ToScreen(start), 4, 4);
    }

    private void DrawCalibration(DrawingContext ctx)
    {
        if (_editor.Project.Background is not { } bg) return;
        Vec2? a, b;
        string? label = null;
        if (_editor.Tool == Tool.Calibrate && _calibA is not null)
        {
            a = bg.ToWorld(_calibA.Value);
            b = _calibB is { } cb ? bg.ToWorld(cb) : ToWorld(_mouse);
            label = $"{Vec2.Distance(a.Value, b.Value):0.00} m na escala atual";
        }
        else if (_editor.Tool == Tool.Calibrate && _editor.Project.Calibration is { } cal)
        {
            a = bg.ToWorld(cal.A);
            b = bg.ToWorld(cal.B);
            label = $"régua atual: {cal.Meters:0.00} m";
        }
        else return;

        var pa = ToScreen(a.Value);
        var pb = ToScreen(b.Value);
        ctx.DrawLine(new Pen(Orange, 2), pa, pb);
        ctx.DrawEllipse(Orange, new Pen(Brushes.White, 1.5), pa, 5, 5);
        ctx.DrawEllipse(Orange, new Pen(Brushes.White, 1.5), pb, 5, 5);
        DrawLabelAt(ctx, label, new Point((pa.X + pb.X) / 2, (pa.Y + pb.Y) / 2 - 18));
    }

    /// <summary>Barra de escala no canto inferior esquerdo, com um comprimento "redondo".</summary>
    private void DrawScaleBar(DrawingContext ctx)
    {
        var target = Px(120);
        var pow = Math.Pow(10, Math.Floor(Math.Log10(target)));
        var meters = new[] { 1, 2, 5, 10 }.Select(k => k * pow).Last(m => m <= target);
        var w = meters * _zoom;
        var y = Bounds.Height - 18;
        var pen = new Pen(Brushes.White, 2);
        ctx.DrawLine(pen, new Point(14, y), new Point(14 + w, y));
        ctx.DrawLine(pen, new Point(14, y - 5), new Point(14, y + 1));
        ctx.DrawLine(pen, new Point(14 + w, y - 5), new Point(14 + w, y + 1));
        DrawLabelAt(ctx, meters >= 1 ? $"{meters:0} m" : $"{meters:0.##} m", new Point(14 + w / 2, y - 16));
    }

    private void DrawLabel(DrawingContext ctx, string text, Vec2 world)
    {
        var s = ToScreen(world);
        DrawLabelAt(ctx, text, new Point(s.X, s.Y - 24));
    }

    private static void DrawLabelAt(DrawingContext ctx, string text, Point center)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.White);
        var origin = new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2);
        ctx.FillRectangle(LabelBack, new Rect(origin.X - 4, origin.Y - 2, ft.Width + 8, ft.Height + 4), 3);
        ctx.DrawText(ft, origin);
    }
}
