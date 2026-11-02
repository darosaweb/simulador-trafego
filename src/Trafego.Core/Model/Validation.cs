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
            if (c.SplitPercent is { } pct && pct is < 0 or > 100) Error(c.Id, $"{c.Name}: o percentual deve ficar entre 0 e 100.");
            if (c.SplitPercentTruck is { } pt && pt is < 0 or > 100) Error(c.Id, $"{c.Name}: o percentual dos pesados deve ficar entre 0 e 100.");
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
        CheckFerries(net, Error, Warn);
        CheckDemand(net, Error);
        CheckMeasurements(net, Error);

        if (issues.All(i => i.Severity != Severity.Error))
        {
            var analysis = ConflictAnalysis.Of(net);
            CheckSimultaneousGreens(net, analysis, Warn);
            CheckSplits(net, analysis.Topology, Warn);
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

    /// <summary>
    /// Percentuais dos conectores que saem do mesmo ponto: somando mais de 100%, ou sem chegar a 100%
    /// quando todas as opções têm percentual, eles são normalizados — vale avisar.
    /// </summary>
    private static void CheckSplits(Network net, Topology topo, Action<int, string> warn)
    {
        foreach (var cls in new[] { VehicleClass.Car, VehicleClass.Truck })
        {
            // Os pesados só têm conta própria onde algum conector tem percentual de pesados.
            var groups = net.Connectors.Where(topo.Covers).GroupBy(topo.FromNode)
                .Where(g => cls == VehicleClass.Car ? g.Any(c => c.SplitPercent is not null) : g.Any(c => c.SplitPercentTruck is not null));
            var what = cls == VehicleClass.Truck ? "percentuais dos pesados" : "percentuais";
            foreach (var g in groups)
            {
                var sum = g.Sum(c => c.SplitFor(cls) ?? 0);
                var continues = !topo.IsLinkEnd(g.Key);
                var allSet = !continues && g.All(c => c.SplitFor(cls) is not null);
                var where = $"{NameOf(net, g.Key.LinkId)} ({string.Join(", ", g.Select(c => c.Name))})";
                if (sum > 100 + 1e-6)
                    warn(g.First().Id, $"Os {what} dos conectores que saem de {where} somam {sum:0}%; serão normalizados para 100%.");
                else if (allSet && Math.Abs(sum - 100) > 1e-6)
                    warn(g.First().Id, $"Os {what} dos conectores que saem de {where} somam {sum:0}% e não há outra opção; serão normalizados para 100%.");
            }
        }
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

    /// <summary>
    /// Balsas: a travessia é um link de uma faixa, sobre a água, em que só se chega por conectores no começo e
    /// de onde só se sai por conectores no fim (os veículos embarcam na rampa e desembarcam do outro lado).
    /// </summary>
    private static void CheckFerries(Network net, Action<int, string> error, Action<int, string> warn)
    {
        var used = new Dictionary<int, Ferry>();
        foreach (var f in net.Ferries)
        {
            if (!(f.DeckLength >= Ferry.MinDeck)) error(f.LinkId, $"{f.Name}: o convés deve ter pelo menos {Ferry.MinDeck:0} m.");
            if (!(f.CrossingMinutes > 0)) error(f.LinkId, $"{f.Name}: o tempo de travessia deve ser maior que zero.");
            if (!(f.MaxWaitMinutes >= 0)) error(f.LinkId, $"{f.Name}: a espera máxima não pode ser negativa.");
            if (f.Vessels < 1) error(f.LinkId, $"{f.Name}: precisa de pelo menos 1 embarcação.");
            if (f.DeckLanes < 1) error(f.LinkId, $"{f.Name}: o convés precisa de pelo menos 1 faixa.");
            if (f.ReturnLinkId == f.LinkId) error(f.LinkId, $"{f.Name}: a volta precisa ser outro link (o do sentido contrário).");
            foreach (var (linkId, role) in new[] { (f.LinkId, "ida"), (f.ReturnLinkId ?? -1, "volta") })
            {
                if (linkId < 0) continue;
                if (net.FindLink(linkId) is not { } link) { error(f.LinkId, $"{f.Name}: o link da {role} não existe."); continue; }
                if (used.TryGetValue(linkId, out var other) && other != f)
                    error(f.LinkId, $"{f.Name}: {link.Name} já é a travessia de {other.Name}.");
                used[linkId] = f;
                CheckCrossing(net, f, link, role, error, warn);
            }
            for (var dir = 0; dir < 2; dir++)
                if (f.YardOf(dir) is { } yard)
                    CheckYard(net, f, yard, dir == 0 ? f.LinkId : f.ReturnLinkId, dir == 0 ? "ida" : "volta", error, warn);
        }
    }

    private static void CheckCrossing(Network net, Ferry f, Link link, string role, Action<int, string> error, Action<int, string> warn)
    {
        var where = $"{f.Name} ({role}, {link.Name})";
        if (link.Points.Count < 2) return;
        var length = link.Axis().Length;
        if (link.Lanes.Count != 1) error(link.Id, $"{where}: a travessia deve ter 1 faixa (os veículos ficam em fila no convés).");
        var deck = f.LinearDeck;
        if (length < deck + 20)
            error(link.Id, f.DeckLanes > 1
                ? $"{where}: a travessia precisa ter pelo menos {deck + 20:0} m (na simulação as {f.DeckLanes} faixas do convés ficam em fila, somando {deck:0} m, mais 20 m)."
                : $"{where}: a travessia precisa ser pelo menos 20 m mais longa que o convés ({f.DeckLength:0} m).");
        if (net.Inputs.Any(i => i.LinkId == link.Id)) error(link.Id, $"{where}: não pode haver entrada de veículos na travessia; ponha a entrada na estrada até a rampa.");
        var arriving = net.Connectors.Where(c => c.ToLinkId == link.Id).ToList();
        var leaving = net.Connectors.Where(c => c.FromLinkId == link.Id).ToList();
        if (arriving.Count == 0) error(link.Id, $"{where}: ligue a rampa ao começo da travessia com um conector.");
        if (arriving.Any(c => c.ToS > 1)) error(link.Id, $"{where}: os conectores devem chegar no começo da travessia (é onde fica o convés).");
        if (leaving.Count == 0) warn(link.Id, $"{where}: nenhum conector sai do fim da travessia; os veículos saem da rede ao desembarcar.");
        if (leaving.Any(c => c.FromS < length - 1)) error(link.Id, $"{where}: os conectores devem sair do fim da travessia (onde se desembarca).");
    }

    /// <summary>
    /// Pátio de acúmulo: um link inteiro (sem conectores no meio), de onde se chega à travessia. As ligações faixa a
    /// faixa para dentro e para fora dele são completadas na exportação (todas as faixas recebem e todas saem).
    /// </summary>
    private static void CheckYard(Network net, Ferry f, int yardId, int? crossingId, string role, Action<int, string> error, Action<int, string> warn)
    {
        var where = $"{f.Name} (pátio da {role})";
        if (crossingId is null) { error(f.LinkId, $"{where}: só há pátio da volta quando há link de volta."); return; }
        if (net.FindLink(yardId) is not { } yard) { error(f.LinkId, $"{where}: o link do pátio não existe."); return; }
        if (net.FerryOf(yardId) is not null) { error(yard.Id, $"{where}: o pátio não pode ser uma travessia."); return; }
        if (yard.Points.Count < 2) return;
        var length = yard.Axis().Length;
        if (yard.Lanes.Count < 2) warn(yard.Id, $"{where}: {yard.Name} tem 1 faixa; um pátio de uma faixa é só uma fila mais longa.");
        if (net.Connectors.Any(c => c.FromLinkId == yardId && c.FromS < length - 1 || c.ToLinkId == yardId && c.ToS > 1))
            error(yard.Id, $"{where}: os conectores do pátio devem chegar no começo e sair do fim dele.");
        if (net.Inputs.Any(i => i.LinkId == yardId))
            warn(yard.Id, $"{where}: há entrada de veículos no próprio pátio; ponha-a na estrada antes dele.");
        // Do fim do pátio se chega à travessia (por até alguns links: a rampa).
        var reach = new HashSet<int> { yardId };
        var frontier = new List<int> { yardId };
        for (var hop = 0; hop < 4 && frontier.Count > 0; hop++)
        {
            frontier = [.. net.Connectors.Where(c => frontier.Contains(c.FromLinkId)).Select(c => c.ToLinkId).Where(reach.Add)];
            if (reach.Contains(crossingId.Value)) return;
        }
        error(yard.Id, $"{where}: do fim de {yard.Name} não se chega à travessia; ligue o pátio à rampa com um conector.");
    }
}
