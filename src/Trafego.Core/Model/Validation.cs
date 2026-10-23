namespace Trafego.Core.Model;

public enum Severity { Warning, Error }

public sealed record ValidationIssue(Severity Severity, int ObjectId, string Message);

public static class Validation
{
    public static List<ValidationIssue> Check(Network net)
    {
        var issues = new List<ValidationIssue>();
        void Error(int id, string msg) => issues.Add(new(Severity.Error, id, msg));
        void Warn(int id, string msg) => issues.Add(new(Severity.Warning, id, msg));

        var ids = new HashSet<int>();
        foreach (var id in net.Links.Select(l => l.Id).Concat(net.Connectors.Select(c => c.Id)).Concat(net.Inputs.Select(i => i.Id)))
        {
            if (!ids.Add(id)) Error(id, $"Id {id} repetido.");
            if (id >= net.NextId) Error(id, $"Id {id} não é menor que o próximo id livre ({net.NextId}).");
        }

        foreach (var l in net.Links)
        {
            if (l.Points.Count < 2) Error(l.Id, $"{l.Name}: precisa de pelo menos 2 pontos.");
            else if (l.Axis().Length < 1) Error(l.Id, $"{l.Name}: comprimento menor que 1 m.");
            if (l.Lanes.Count == 0) Error(l.Id, $"{l.Name}: sem faixas.");
            if (l.Lanes.Any(f => !(f.Width > 0))) Error(l.Id, $"{l.Name}: largura de faixa deve ser maior que zero.");
            if (!(l.SpeedKmh > 0)) Error(l.Id, $"{l.Name}: velocidade deve ser maior que zero.");
        }

        foreach (var c in net.Connectors)
        {
            var from = net.FindLink(c.FromLinkId);
            var to = net.FindLink(c.ToLinkId);
            if (from is null) { Error(c.Id, $"{c.Name}: link de origem {c.FromLinkId} não existe."); continue; }
            if (to is null) { Error(c.Id, $"{c.Name}: link de destino {c.ToLinkId} não existe."); continue; }
            if (c.LaneCount < 1) { Error(c.Id, $"{c.Name}: precisa ligar pelo menos 1 faixa."); continue; }
            if (c.FromLane < 0 || c.FromLane + c.LaneCount > from.Lanes.Count)
                Error(c.Id, $"{c.Name}: faixas de origem fora de {from.Name}.");
            if (c.ToLane < 0 || c.ToLane + c.LaneCount > to.Lanes.Count)
                Error(c.Id, $"{c.Name}: faixas de destino fora de {to.Name}.");
            if (from.Points.Count >= 2 && (c.FromS < 0 || c.FromS > from.Axis().Length + 1e-6))
                Error(c.Id, $"{c.Name}: posição de saída fora de {from.Name}.");
            if (to.Points.Count >= 2 && (c.ToS < 0 || c.ToS > to.Axis().Length + 1e-6))
                Error(c.Id, $"{c.Name}: posição de chegada fora de {to.Name}.");
            if (c.FromLinkId == c.ToLinkId) Warn(c.Id, $"{c.Name}: liga {from.Name} a ele mesmo.");
        }

        foreach (var input in net.Inputs)
        {
            if (net.FindLink(input.LinkId) is null) Error(input.Id, $"{input.Name}: link {input.LinkId} não existe.");
            if (!(input.VolumeVph >= 0)) Error(input.Id, $"{input.Name}: volume não pode ser negativo.");
            if (!(input.TruckShare is >= 0 and <= 1)) Error(input.Id, $"{input.Name}: fração de pesados deve estar entre 0 e 1.");
        }
        if (net.Inputs.GroupBy(i => i.LinkId).FirstOrDefault(g => g.Count() > 1) is { } dup)
            Warn(dup.First().Id, $"Há mais de uma entrada no link {dup.Key}; os volumes se somam.");

        CheckSignals(net, Error, Warn);
        CheckDemand(net, Error);
        CheckMeasurements(net, Error);

        if (issues.All(i => i.Severity != Severity.Error))
        {
            var analysis = ConflictAnalysis.Of(net);
            CheckSimultaneousGreens(net, analysis, Warn);
            foreach (var (o, d) in net.Demand.Pairs())
                if (net.FindInput(o) is { } input && RouteFinder.Alternatives(analysis, input.LinkId, d, 1).Count == 0)
                    Warn(o, $"Matriz O/D: não há caminho de {input.Name} até a saída {NameOf(net, d)}.");
            foreach (var p in analysis.Problems) Error(0, p);
            foreach (var c in analysis.UnresolvedLinkCrossings)
                Warn(c.A, $"{NameOf(net, c.A)} cruza {NameOf(net, c.B)} fora de um nó: se for no mesmo nível, " +
                          "termine os links antes do cruzamento e ligue por conectores; se for viaduto, marque \"níveis diferentes\".");
        }
        return issues;
    }

    private static void CheckMeasurements(Network net, Action<int, string> error)
    {
        foreach (var m in net.Measurements)
        {
            if (net.FindLink(m.LinkId) is not { } link) { error(m.Id, $"{m.Name}: link {m.LinkId} não existe."); continue; }
            if (m.S < 0 || m.S > link.Axis().Length + 1e-6) error(m.Id, $"{m.Name}: posição fora de {link.Name}.");
            if (m.Kind == MeasurementKind.Queue && !(m.QueueLength > 0)) error(m.Id, $"{m.Name}: comprimento da fila deve ser maior que zero.");
            if (m.Kind == MeasurementKind.TravelTime)
            {
                if (net.FindLink(m.ToLinkId) is not { } to) error(m.Id, $"{m.Name}: link do fim {m.ToLinkId} não existe.");
                else if (m.ToS < 0 || m.ToS > to.Axis().Length + 1e-6) error(m.Id, $"{m.Name}: posição do fim fora de {to.Name}.");
            }
        }
    }

    private static void CheckDemand(Network net, Action<int, string> error)
    {
        var d = net.Demand;
        if (d.SliceStarts.Any(s => s is < 0 or >= SignalController.Day)) error(0, "Demanda: intervalo com horário fora do dia.");
        foreach (var i in net.Inputs.Where(i => i.Volumes.Any(v => v.Vph < 0)))
            error(i.Id, $"{i.Name}: volume negativo num intervalo.");
        if (d.Matrix.Any(e => e.Vph < 0)) error(0, "Matriz O/D: volume negativo.");
        if (d.RouteChoices.Any(r => r.Share < 0)) error(0, "Rotas: participação negativa.");
    }

    private static void CheckSignals(Network net, Action<int, string> error, Action<int, string> warn)
    {
        foreach (var sc in net.SignalControllers)
        {
            if (sc.Groups.Count == 0) warn(sc.Id, $"{sc.Name}: sem grupos.");
            if (sc.Groups.Select(g => g.Id).Distinct().Count() != sc.Groups.Count) error(sc.Id, $"{sc.Name}: grupos com id repetido.");
            if (sc.Plans.Count == 0) error(sc.Id, $"{sc.Name}: sem planos.");
            foreach (var p in sc.Plans)
            {
                if (!(p.Cycle > 0)) { error(sc.Id, $"{sc.Name}, {p.Name}: ciclo deve ser maior que zero."); continue; }
                foreach (var t in p.Timings)
                {
                    if (sc.FindGroup(t.GroupId) is null) error(sc.Id, $"{sc.Name}, {p.Name}: tempo de um grupo que não existe ({t.GroupId}).");
                    if (t.GreenStart < 0 || t.GreenStart > p.Cycle || t.GreenEnd < 0 || t.GreenEnd > p.Cycle)
                        error(sc.Id, $"{sc.Name}, {p.Name}: os tempos devem ficar entre 0 e o ciclo ({p.Cycle:0} s).");
                    if (t.Amber < 0) error(sc.Id, $"{sc.Name}, {p.Name}: amarelo não pode ser negativo.");
                }
                foreach (var g in sc.Groups)
                    if (p.TimingOf(g.Id) is not { } t || Math.Abs(t.GreenEnd - t.GreenStart) < 1e-9)
                        warn(sc.Id, $"{sc.Name}, {p.Name}: {g.Name} não tem verde (fica sempre vermelho).");
            }
            foreach (var s in sc.Schedule)
            {
                if (sc.FindPlan(s.PlanId) is null) error(sc.Id, $"{sc.Name}: horário aponta para um plano que não existe.");
                if (s.TimeOfDay is < 0 or >= SignalController.Day) error(sc.Id, $"{sc.Name}: horário fora do dia.");
            }
        }

        foreach (var c in net.Connectors.Where(c => c.Signal is not null))
        {
            var sc = net.FindController(c.Signal!.ControllerId);
            if (sc is null) error(c.Id, $"{c.Name}: semáforo {c.Signal.ControllerId} não existe.");
            else if (sc.FindGroup(c.Signal.GroupId) is null) error(c.Id, $"{c.Name}: grupo {c.Signal.GroupId} não existe em {sc.Name}.");
        }
    }

    /// <summary>
    /// Dois movimentos que se cruzam ou convergem, controlados pelo mesmo semáforo, não deveriam ter
    /// verde (ou amarelo) ao mesmo tempo, a não ser que um deles seja verde permitido (cede a passagem).
    /// </summary>
    private static void CheckSimultaneousGreens(Network net, ConflictAnalysis analysis, Action<int, string> warn)
    {
        foreach (var conflict in analysis.Conflicts.Where(x => x.Kind != ConflictKind.LinkCrossing))
        {
            if (net.FindConnector(conflict.A)?.Signal is not { } sa || net.FindConnector(conflict.B)?.Signal is not { } sb) continue;
            if (sa.ControllerId != sb.ControllerId || sa.GroupId == sb.GroupId) continue;
            if (net.PriorityOf(conflict.A, conflict.B) == ConflictPriority.None) continue;
            var sc = net.FindController(sa.ControllerId);
            var (ga, gb) = (sc?.FindGroup(sa.GroupId), sc?.FindGroup(sb.GroupId));
            if (sc is null || ga is null || gb is null || ga.Permissive || gb.Permissive) continue;

            foreach (var p in sc.Plans.Where(p => p.Cycle > 0))
            {
                var steps = (int)Math.Ceiling(p.Cycle / 0.5);
                var overlap = Enumerable.Range(0, steps).Select(i => (i + 0.5) * p.Cycle / steps)
                    .Any(tc => p.StateAt(ga.Id, tc) != SignalState.Red && p.StateAt(gb.Id, tc) != SignalState.Red);
                if (overlap)
                    warn(conflict.A, $"{sc.Name}, {p.Name}: {ga.Name} e {gb.Name} abrem juntos, mas " +
                                     $"{NameOf(net, conflict.A)} e {NameOf(net, conflict.B)} se cruzam ou convergem.");
            }
        }
    }

    public static string NameOf(Network net, int id) =>
        id == ConflictRule.MainLine ? "fluxo principal"
        : net.FindLink(id)?.Name ?? net.FindConnector(id)?.Name ?? net.FindInput(id)?.Name ?? $"#{id}";
}
