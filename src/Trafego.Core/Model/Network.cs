using Trafego.Core.Geometry;

namespace Trafego.Core.Model;

public sealed class Network
{
    public List<Link> Links { get; set; } = [];
    public List<Connector> Connectors { get; set; } = [];
    public List<VehicleInput> Inputs { get; set; } = [];
    public List<ConflictRule> ConflictRules { get; set; } = [];
    public List<JunctionSetting> Junctions { get; set; } = [];
    public List<SignalController> SignalControllers { get; set; } = [];
    public DemandSettings Demand { get; set; } = new();
    public List<Measurement> Measurements { get; set; } = [];

    public Measurement? FindMeasurement(int id) => Measurements.Find(m => m.Id == id);

    public Measurement AddMeasurement(MeasurementKind kind, Link link, double s)
    {
        var id = NewId();
        var count = Measurements.Count(m => m.Kind == kind) + 1;
        var m = new Measurement { Id = id, Kind = kind, Name = $"{Measurement.KindName(kind)} {count}", LinkId = link.Id, S = s, ToLinkId = link.Id, ToS = s };
        Measurements.Add(m);
        return m;
    }

    public void RemoveMeasurement(int id) => Measurements.RemoveAll(m => m.Id == id);

    public SignalController? FindController(int id) => SignalControllers.Find(c => c.Id == id);

    public SignalController AddController()
    {
        var c = SignalController.CreateDefault(NewId());
        SignalControllers.Add(c);
        return c;
    }

    /// <summary>Remove o controlador e as cabeças semafóricas que apontavam para ele.</summary>
    public void RemoveController(int id)
    {
        SignalControllers.RemoveAll(c => c.Id == id);
        foreach (var c in Connectors.Where(c => c.Signal?.ControllerId == id)) c.Signal = null;
    }

    /// <summary>Remove um grupo do controlador, seus tempos e as cabeças ligadas a ele.</summary>
    public void RemoveGroup(SignalController controller, int groupId)
    {
        controller.Groups.RemoveAll(g => g.Id == groupId);
        foreach (var p in controller.Plans) p.Timings.RemoveAll(t => t.GroupId == groupId);
        foreach (var c in Connectors.Where(c => c.Signal is { } s && s.ControllerId == controller.Id && s.GroupId == groupId))
            c.Signal = null;
    }

    /// <summary>Próximo id livre; links, conectores e entradas compartilham a numeração.</summary>
    public int NextId { get; set; } = 1;

    public int NewId() => NextId++;

    public Link? FindLink(int id) => Links.Find(l => l.Id == id);
    public Connector? FindConnector(int id) => Connectors.Find(c => c.Id == id);
    public VehicleInput? FindInput(int id) => Inputs.Find(i => i.Id == id);

    public VehicleInput AddInput(Link link, double volumeVph = 600)
    {
        var id = NewId();
        var input = new VehicleInput { Id = id, Name = $"Entrada {id}", LinkId = link.Id, VolumeVph = volumeVph };
        Inputs.Add(input);
        return input;
    }

    public void RemoveInput(int id)
    {
        Inputs.RemoveAll(i => i.Id == id);
        Demand.Prune(this);
    }

    public Link AddLink(IEnumerable<Vec2> points, int laneCount = 1, double laneWidth = Lane.DefaultWidth)
    {
        var id = NewId();
        var link = new Link
        {
            Id = id,
            Name = $"Link {id}",
            Points = [.. points],
            Lanes = [.. Enumerable.Range(0, laneCount).Select(_ => new Lane { Width = laneWidth })],
        };
        Links.Add(link);
        return link;
    }

    /// <summary>
    /// Cria o sentido oposto de um link, como o "Generate opposite direction" do Vissim: mesma forma,
    /// percorrida ao contrário, encostada do lado esquerdo de quem dirige no link original (trânsito pela
    /// direita, como no Brasil). Mesmas faixas, velocidade e suavização.
    /// </summary>
    /// <param name="gap">Afastamento entre as bordas dos dois sentidos (canteiro central), m.</param>
    public Link AddOppositeLink(Link link, double gap = 0)
    {
        var distance = link.TotalWidth / 2 + Math.Max(0, gap) + link.TotalWidth / 2;
        var points = Polyline.Offset(link.Points, distance);
        points.Reverse();
        var id = NewId();
        var opposite = new Link
        {
            Id = id,
            Name = $"{link.Name} (oposto)",
            Points = points,
            Smooth = link.Smooth,
            SpeedKmh = link.SpeedKmh,
            Lanes = [.. link.Lanes.Select(l => new Lane { Width = l.Width })],
        };
        Links.Add(opposite);
        return opposite;
    }

    /// <summary>Espaço entre as duas partes de um link dividido, ocupado pelo conector que as liga, m.</summary>
    public const double SplitGap = 2;

    /// <summary>
    /// Divide o link em dois na posição s, como o "Split link" do Vissim. O link original vira a parte A
    /// (até s) e mantém o id; a parte B (depois de s) é um link novo; um conector curto com todas as faixas
    /// liga A a B. Conectores e medições depois do ponto passam para B; a entrada fica em A; a matriz O/D e
    /// as rotas escolhidas que passam pelo ponto são atualizadas para continuar valendo.
    /// </summary>
    public (Link A, Link B, Connector Join) SplitLink(Link link, double s)
    {
        var axis = link.Axis();
        var len = axis.Length;
        var half = SplitGap / 2;
        if (s < half + Topology.MergeDistance || s > len - half - Topology.MergeDistance)
            throw new ArgumentException($"Ponto muito perto da ponta de {link.Name}: divida a pelo menos {SplitGap} m das pontas.");

        // Antes de mexer: quais rotas escolhidas passam pelo ponto (vão ganhar o conector novo).
        var routeFixes = RouteChoicesThrough(link.Id, s);

        var (sA, sB) = (s - half, s + half);
        var pA = axis.At(sA).Point;
        var pB = axis.At(sB).Point;
        List<Vec2> pointsA, pointsB;
        if (link.Smooth)
        {
            // Curva suave: corta o polígono de controle, mantendo os pontos de cada lado.
            var control = new Polyline(link.Points);
            var iA = control.Project(pA)!.Value.Segment;
            var iB = control.Project(pB)!.Value.Segment;
            pointsA = [.. link.Points.Take(iA + 1), pA];
            pointsB = [pB, .. link.Points.Skip(iB + 1)];
        }
        else
        {
            pointsA = axis.Slice(0, sA);
            pointsB = axis.Slice(sB, len);
        }

        var id = NewId();
        var b = new Link
        {
            Id = id,
            Name = $"{link.Name} (2)",
            Points = pointsB,
            Smooth = link.Smooth,
            SpeedKmh = link.SpeedKmh,
            Lanes = [.. link.Lanes.Select(l => new Lane { Width = l.Width })],
        };
        link.Points = pointsA;
        Links.Add(b);

        // O que fica depois do ponto passa para B, com a posição medida a partir do começo de B.
        foreach (var c in Connectors)
        {
            if (c.FromLinkId == link.Id && c.FromS > s) { c.FromLinkId = b.Id; c.FromS -= sB; }
            if (c.ToLinkId == link.Id && c.ToS >= s) { c.ToLinkId = b.Id; c.ToS -= sB; }
        }
        foreach (var m in Measurements)
        {
            if (m.LinkId == link.Id && m.S > s) { m.LinkId = b.Id; m.S -= sB; }
            if (m.ToLinkId == link.Id && m.ToS > s) { m.ToLinkId = b.Id; m.ToS -= sB; }
        }

        var join = AddConnector(link, 0, link.Axis().Length, b, 0, 0, link.Lanes.Count);

        // A saída da rede que era o fim do link agora é o fim de B.
        foreach (var e in Demand.Matrix.Where(e => e.DestinationLinkId == link.Id)) e.DestinationLinkId = b.Id;
        foreach (var r in Demand.RouteChoices.Where(r => r.DestinationLinkId == link.Id)) r.DestinationLinkId = b.Id;
        foreach (var (choice, index) in routeFixes) choice.ConnectorIds.Insert(index, join.Id);

        FitConnectorsTo(link);
        FitConnectorsTo(b);
        return (link, b, join);
    }

    /// <summary>
    /// Rotas escolhidas (matriz O/D) que atravessam a posição s do link, e onde o conector da divisão
    /// entra na lista de conectores de cada uma.
    /// </summary>
    private List<(RouteChoice Choice, int Index)> RouteChoicesThrough(int linkId, double s)
    {
        var result = new List<(RouteChoice, int)>();
        if (Demand.RouteChoices.Count == 0) return result;
        var analysis = ConflictAnalysis.Of(this);
        var cuts = analysis.Topology.Has(linkId) ? analysis.Topology.Cuts(linkId) : [];
        foreach (var choice in Demand.RouteChoices)
        {
            if (FindInput(choice.OriginId) is not { } input) continue;
            var path = RouteFinder.PathsFrom(analysis, input.LinkId)
                .FirstOrDefault(p => p.EndLinkId == choice.DestinationLinkId && p.ConnectorIds.SequenceEqual(choice.ConnectorIds));
            if (path is null) continue;
            var connectorsBefore = 0;
            foreach (var step in path.Steps)
            {
                if (step is ConnectorStep) connectorsBefore++;
                else if (step is LinkStep ls && ls.LinkId == linkId && cuts[ls.Piece] < s && s < cuts[ls.Piece + 1])
                {
                    result.Add((choice, connectorsBefore));
                    break;
                }
            }
        }
        return result;
    }

    public Connector AddConnector(Link from, int fromLane, double fromS, Link to, int toLane, double toS, int laneCount = 1)
    {
        var id = NewId();
        var c = new Connector
        {
            Id = id,
            Name = $"Conector {id}",
            FromLinkId = from.Id,
            FromLane = fromLane,
            FromS = fromS,
            ToLinkId = to.Id,
            ToLane = toLane,
            ToS = toS,
            LaneCount = laneCount,
        };
        Connectors.Add(c);
        return c;
    }

    /// <summary>Remove o link e todos os conectores e entradas ligados a ele.</summary>
    public void RemoveLink(int id)
    {
        Links.RemoveAll(l => l.Id == id);
        Connectors.RemoveAll(c => c.FromLinkId == id || c.ToLinkId == id);
        Inputs.RemoveAll(i => i.LinkId == id);
        Measurements.RemoveAll(m => m.LinkId == id || m.ToLinkId == id);
        PruneRules();
    }

    public void RemoveConnector(int id)
    {
        Connectors.RemoveAll(c => c.Id == id);
        PruneRules();
    }

    /// <summary>Tira regras de conflito e de nó que apontam para objetos que não existem mais.</summary>
    public void PruneRules()
    {
        var connectorIds = Connectors.Select(c => c.Id).ToHashSet();
        var ids = connectorIds.Concat(Links.Select(l => l.Id)).ToHashSet();
        ConflictRules.RemoveAll(r => !ids.Contains(r.A) || (r.B != ConflictRule.MainLine && !ids.Contains(r.B)));
        Junctions.RemoveAll(j => !connectorIds.Contains(j.Key));
        Demand.Prune(this);
    }

    public ConflictPriority PriorityOf(int a, int b)
    {
        var (na, nb, swapped) = ConflictRule.Normalize(a, b);
        var p = ConflictRules.Find(r => r.A == na && r.B == nb)?.Priority ?? ConflictPriority.Auto;
        return swapped ? p switch { ConflictPriority.A => ConflictPriority.B, ConflictPriority.B => ConflictPriority.A, _ => p } : p;
    }

    /// <summary>Define quem tem preferência entre a e b (A = a, B = b). Auto apaga a regra.</summary>
    public void SetPriority(int a, int b, ConflictPriority priority)
    {
        var (na, nb, swapped) = ConflictRule.Normalize(a, b);
        if (swapped) priority = priority switch { ConflictPriority.A => ConflictPriority.B, ConflictPriority.B => ConflictPriority.A, _ => priority };
        ConflictRules.RemoveAll(r => r.A == na && r.B == nb);
        if (priority != ConflictPriority.Auto) ConflictRules.Add(new ConflictRule { A = na, B = nb, Priority = priority });
    }

    public JunctionControl ControlOf(int junctionKey) =>
        Junctions.Find(j => j.Key == junctionKey)?.Control ?? JunctionControl.Priority;

    public void SetControl(int junctionKey, JunctionControl control)
    {
        Junctions.RemoveAll(j => j.Key == junctionKey);
        if (control != JunctionControl.Priority) Junctions.Add(new JunctionSetting { Key = junctionKey, Control = control });
    }

    public IEnumerable<Connector> ConnectorsOf(int linkId) =>
        Connectors.Where(c => c.FromLinkId == linkId || c.ToLinkId == linkId);

    /// <summary>
    /// Ajusta os conectores de um link depois que ele mudou: posições dentro do comprimento
    /// e grupos de faixas dentro da quantidade de faixas.
    /// </summary>
    public void FitConnectorsTo(Link link)
    {
        var len = link.Axis().Length;
        foreach (var m in Measurements)
        {
            if (m.LinkId == link.Id) m.S = Math.Clamp(m.S, 0, len);
            if (m.ToLinkId == link.Id) m.ToS = Math.Clamp(m.ToS, 0, len);
        }
        foreach (var c in ConnectorsOf(link.Id))
        {
            if (c.FromLinkId == link.Id) c.FromS = Math.Clamp(c.FromS, 0, len);
            if (c.ToLinkId == link.Id) c.ToS = Math.Clamp(c.ToS, 0, len);
        }
        foreach (var c in Connectors.ToList())
        {
            var from = FindLink(c.FromLinkId);
            var to = FindLink(c.ToLinkId);
            if (from is null || to is null) continue;
            var maxCount = Math.Min(from.Lanes.Count, to.Lanes.Count);
            c.LaneCount = Math.Clamp(c.LaneCount, 1, maxCount);
            c.FromLane = Math.Clamp(c.FromLane, 0, from.Lanes.Count - c.LaneCount);
            c.ToLane = Math.Clamp(c.ToLane, 0, to.Lanes.Count - c.LaneCount);
        }
    }

    /// <summary>Geometria do conector, ou null se ele aponta para links inexistentes ou faixas inválidas.</summary>
    public ConnectorGeometry? GeometryOf(Connector c)
    {
        var from = FindLink(c.FromLinkId);
        var to = FindLink(c.ToLinkId);
        if (from is null || to is null || from.Points.Count < 2 || to.Points.Count < 2) return null;
        if (c.LaneCount < 1 || c.FromLane < 0 || c.ToLane < 0) return null;
        if (c.FromLane + c.LaneCount > from.Lanes.Count || c.ToLane + c.LaneCount > to.Lanes.Count) return null;

        var a = from.Axis().At(c.FromS);
        var b = to.Axis().At(c.ToS);
        var fromLat = (from.LaneRightEdge(c.FromLane) + from.LaneLeftEdge(c.FromLane + c.LaneCount - 1)) / 2;
        var toLat = (to.LaneRightEdge(c.ToLane) + to.LaneLeftEdge(c.ToLane + c.LaneCount - 1)) / 2;
        var p0 = a.Point + a.Tangent.LeftNormal() * fromLat;
        var p3 = b.Point + b.Tangent.LeftNormal() * toLat;

        var pts = c.Points.Count == 0
            ? Curves.TangentBezier(p0, a.Tangent, p3, b.Tangent)
            : Curves.CatmullRom([p0, .. c.Points, p3]);

        var widths = new double[c.LaneCount];
        for (var i = 0; i < c.LaneCount; i++) widths[i] = from.Lanes[c.FromLane + i].Width;
        var offsets = new double[c.LaneCount];
        var edge = -widths.Sum() / 2;
        for (var i = 0; i < c.LaneCount; i++)
        {
            offsets[i] = edge + widths[i] / 2;
            edge += widths[i];
        }
        return new ConnectorGeometry(new Polyline(pts), offsets, widths);
    }

    /// <summary>Escala toda a geometria em torno de um ponto (usado ao recalibrar a foto).</summary>
    public void ScaleAbout(Vec2 origin, double factor)
    {
        Vec2 Map(Vec2 p) => origin + (p - origin) * factor;
        foreach (var l in Links) l.Points = [.. l.Points.Select(Map)];
        foreach (var c in Connectors)
        {
            c.Points = [.. c.Points.Select(Map)];
            c.FromS *= factor;
            c.ToS *= factor;
        }
    }
}
