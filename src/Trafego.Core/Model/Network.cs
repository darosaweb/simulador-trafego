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
