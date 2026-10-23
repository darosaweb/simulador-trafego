using System.Text.Json.Serialization;

namespace Trafego.Core.Model;

/// <summary>Grupo semafórico: movimentos que recebem verde juntos.</summary>
public sealed class SignalGroup
{
    /// <summary>Id dentro do controlador (1, 2, 3…).</summary>
    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Verde permitido: o movimento anda no verde mas cede a quem tem conflito (ex.: conversão à esquerda).</summary>
    public bool Permissive { get; set; }
}

/// <summary>Tempos de um grupo num plano, em segundos dentro do ciclo.</summary>
public sealed class GroupTiming
{
    public int GroupId { get; set; }
    public double GreenStart { get; set; }
    public double GreenEnd { get; set; }
    public double Amber { get; set; } = 3;
}

public enum SignalState { Red, Amber, Green }

/// <summary>Plano de tempo fixo: ciclo, defasagem e o verde de cada grupo.</summary>
public sealed class SignalPlan
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public double Cycle { get; set; } = 90;
    /// <summary>Defasagem: o instante (no relógio, mod ciclo) em que o ciclo começa, s.</summary>
    public double Offset { get; set; }
    public List<GroupTiming> Timings { get; set; } = [];

    public GroupTiming? TimingOf(int groupId) => Timings.Find(t => t.GroupId == groupId);

    /// <summary>Estado do grupo no instante tc do ciclo (0 ≤ tc &lt; ciclo). Intervalos podem dar a volta no ciclo.</summary>
    public SignalState StateAt(int groupId, double tc)
    {
        if (TimingOf(groupId) is not { } t || Cycle <= 0) return SignalState.Red;
        if (InWindow(tc, t.GreenStart, t.GreenEnd)) return SignalState.Green;
        if (t.Amber > 0 && InWindow(tc, t.GreenEnd, t.GreenEnd + t.Amber)) return SignalState.Amber;
        return SignalState.Red;
    }

    private bool InWindow(double tc, double start, double end)
    {
        var raw = end - start;
        if (raw >= Cycle) return true; // verde o ciclo todo
        var len = Mod(raw, Cycle);
        return len > 0 && Mod(tc - start, Cycle) < len;
    }

    public double Mod(double x, double m) => ((x % m) + m) % m;

    /// <summary>
    /// Fases do ciclo para o SUMO: cada mudança de cor de qualquer grupo abre uma fase nova.
    /// Os caracteres seguem a ordem dos grupos: G verde, g verde permitido, y amarelo, r vermelho.
    /// </summary>
    public List<(double Duration, string State)> Phases(IReadOnlyList<SignalGroup> groups)
    {
        var cuts = new SortedSet<double> { 0 };
        foreach (var t in Timings)
            foreach (var x in new[] { t.GreenStart, t.GreenEnd, t.GreenEnd + t.Amber })
                cuts.Add(Math.Round(Mod(x, Cycle), 1));
        var times = cuts.Where(x => x < Cycle).ToList();
        times.Add(Cycle);

        var phases = new List<(double Duration, string State)>();
        for (var i = 0; i < times.Count - 1; i++)
        {
            var dur = times[i + 1] - times[i];
            if (dur < 0.05) continue;
            var mid = (times[i] + times[i + 1]) / 2;
            var state = new string([.. groups.Select(g => StateAt(g.Id, mid) switch
            {
                SignalState.Green => g.Permissive ? 'g' : 'G',
                SignalState.Amber => 'y',
                _ => 'r',
            })]);
            if (phases.Count > 0 && phases[^1].State == state) phases[^1] = (phases[^1].Duration + dur, state);
            else phases.Add((dur, state));
        }
        return phases;
    }
}

/// <summary>A partir deste horário do dia, vale o plano indicado.</summary>
public sealed class PlanSwitch
{
    /// <summary>Segundos desde 00:00.</summary>
    public double TimeOfDay { get; set; }
    public int PlanId { get; set; }
}

/// <summary>Controlador semafórico de tempo fixo, com troca de plano por horário.</summary>
public sealed class SignalController
{
    public const double Day = 24 * 3600;

    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<SignalGroup> Groups { get; set; } = [];
    public List<SignalPlan> Plans { get; set; } = [];
    public List<PlanSwitch> Schedule { get; set; } = [];

    public SignalGroup? FindGroup(int id) => Groups.Find(g => g.Id == id);
    public SignalPlan? FindPlan(int id) => Plans.Find(p => p.Id == id);

    [JsonIgnore] public int NextGroupId => Groups.Count == 0 ? 1 : Groups.Max(g => g.Id) + 1;
    [JsonIgnore] public int NextPlanId => Plans.Count == 0 ? 1 : Plans.Max(p => p.Id) + 1;

    /// <summary>Plano em vigor no horário do dia (s desde 00:00). Sem tabela de horários, vale o primeiro plano.</summary>
    public SignalPlan? PlanAt(double timeOfDay)
    {
        var valid = Schedule.Where(s => FindPlan(s.PlanId) is not null).OrderBy(s => s.TimeOfDay).ToList();
        if (valid.Count == 0) return Plans.FirstOrDefault();
        var tod = ((timeOfDay % Day) + Day) % Day;
        var current = valid.LastOrDefault(s => s.TimeOfDay <= tod) ?? valid[^1]; // antes do 1º horário: vale o último do dia anterior
        return FindPlan(current.PlanId);
    }

    /// <summary>Trocas de plano entre begin e end (tempo absoluto, s), já resolvidas por dia.</summary>
    public List<(double Time, SignalPlan Plan)> SwitchesBetween(double begin, double end)
    {
        var result = new List<(double, SignalPlan)>();
        var valid = Schedule.Where(s => FindPlan(s.PlanId) is not null).OrderBy(s => s.TimeOfDay).ToList();
        for (var day = Math.Floor(begin / Day); day * Day <= end; day++)
            foreach (var s in valid)
            {
                var t = day * Day + s.TimeOfDay;
                if (t > begin && t <= end) result.Add((t, FindPlan(s.PlanId)!));
            }
        return result;
    }

    /// <summary>Controlador novo com dois grupos e um plano de 90 s dividido entre eles.</summary>
    public static SignalController CreateDefault(int id) => new()
    {
        Id = id,
        Name = $"Semáforo {id}",
        Groups = [new SignalGroup { Id = 1, Name = "Grupo 1" }, new SignalGroup { Id = 2, Name = "Grupo 2" }],
        Plans =
        [
            new SignalPlan
            {
                Id = 1, Name = "Plano 1", Cycle = 90,
                Timings =
                [
                    new GroupTiming { GroupId = 1, GreenStart = 0, GreenEnd = 40, Amber = 3 },
                    new GroupTiming { GroupId = 2, GreenStart = 45, GreenEnd = 85, Amber = 3 },
                ],
            },
        ],
    };
}

/// <summary>Cabeça semafórica: o conector é controlado por um grupo de um controlador.</summary>
public sealed class SignalRef
{
    public int ControllerId { get; set; }
    public int GroupId { get; set; }
}
