using System.Text.Json.Serialization;
using Trafego.Core.Geometry;

namespace Trafego.Core.Model;

/// <summary>Foto aérea de fundo. Um pixel (px, py) fica no mundo em Origin + (px, py) · MetersPerPixel.</summary>
public sealed class Background
{
    /// <summary>Caminho da imagem; relativo à pasta do arquivo de projeto quando possível.</summary>
    public string ImagePath { get; set; } = "";
    public double MetersPerPixel { get; set; } = 1;
    public Vec2 Origin { get; set; }

    public Vec2 ToWorld(Vec2 pixel) => Origin + pixel * MetersPerPixel;
    public Vec2 ToPixel(Vec2 world) => (world - Origin) / MetersPerPixel;
}

/// <summary>Régua usada para calibrar a foto: dois pontos da imagem (px) e a distância real entre eles.</summary>
public sealed class Calibration
{
    public Vec2 A { get; set; }
    public Vec2 B { get; set; }
    public double Meters { get; set; }
}

/// <summary>Como as redes importadas de outro projeto ficaram em relação à foto deste.</summary>
public enum ImportAlignment
{
    /// <summary>Mesma foto (ou nenhuma nos dois): as redes caem no mesmo lugar.</summary>
    SameImage,
    /// <summary>Este projeto não tinha foto e passou a usar a do outro.</summary>
    AdoptedImage,
    /// <summary>Fotos diferentes: as coordenadas foram mantidas e podem não bater com a foto.</summary>
    DifferentImage,
}

public sealed record ImportResult(IReadOnlyList<Scenario> Added, ImportAlignment Alignment, bool ReplacedEmpty);

/// <summary>Período simulado, no relógio: começa num horário do dia e dura alguns minutos.</summary>
public sealed class SimulationSettings
{
    /// <summary>Horário de início, s desde 00:00.</summary>
    public double StartTimeOfDay { get; set; } = 7 * 3600;
    public double DurationMinutes { get; set; } = 60;

    /// <summary>Duração dos intervalos dos resultados por tempo (medições), min.</summary>
    public double ResultIntervalMinutes { get; set; } = 15;
    /// <summary>Quantas replicações (sementes diferentes) a avaliação roda.</summary>
    public int Replications { get; set; } = 3;
    /// <summary>Semente da primeira replicação; as seguintes somam 1, 2, 3…</summary>
    public int Seed { get; set; } = 1;

    public double Begin => StartTimeOfDay;
    public double End => StartTimeOfDay + DurationMinutes * 60;

    /// <summary>"07:05:30" para um instante absoluto da simulação (s).</summary>
    public static string Clock(double t)
    {
        var s = (long)Math.Floor(t);
        var day = s / 86400;
        s %= 86400;
        var text = $"{s / 3600:00}:{s % 3600 / 60:00}:{s % 60:00}";
        return day > 0 ? $"{text} (+{day}d)" : text;
    }
}

/// <summary>
/// Uma alternativa do projeto (ex.: a situação atual e uma proposta): a rede inteira, com semáforos,
/// demanda e medições próprias. A foto, a calibração e o período simulado são do projeto.
/// </summary>
public sealed class Scenario
{
    public string Name { get; set; } = "Atual";
    public Network Network { get; set; } = new();
}

public sealed class Project
{
    /// <summary>2: cenários (o formato 1 tinha uma rede só, lida como o cenário "Atual").</summary>
    public const int CurrentFormat = 2;

    public int Format { get; set; } = CurrentFormat;
    public Background? Background { get; set; }
    public Calibration? Calibration { get; set; }
    public List<Scenario> Scenarios { get; set; } = [new()];
    /// <summary>Índice do cenário em edição.</summary>
    public int ActiveScenario { get; set; }
    public SimulationSettings Simulation { get; set; } = new();

    [JsonIgnore]
    public Scenario Active
    {
        get
        {
            if (Scenarios.Count == 0) Scenarios.Add(new Scenario());
            return Scenarios[Math.Clamp(ActiveScenario, 0, Scenarios.Count - 1)];
        }
    }

    /// <summary>Rede do cenário em edição.</summary>
    [JsonIgnore]
    public Network Network
    {
        get => Active.Network;
        set => Active.Network = value;
    }

    /// <summary>Nome que ainda não é usado por nenhum cenário: o próprio, ou com " (2)", " (3)"…</summary>
    public string UniqueScenarioName(string name) => UniqueScenarioNameFor(name, -1);

    /// <summary>Como <see cref="UniqueScenarioName"/>, ignorando o próprio cenário (para renomear).</summary>
    public string UniqueScenarioNameFor(string name, int index)
    {
        name = string.IsNullOrWhiteSpace(name) ? "Cenário" : name.Trim();
        var candidate = name;
        for (var i = 2; Scenarios.Where((_, k) => k != index).Any(s => string.Equals(s.Name, candidate, StringComparison.OrdinalIgnoreCase)); i++)
            candidate = $"{name} ({i})";
        return candidate;
    }

    /// <summary>Cópia independente do cenário, logo depois dele, já em edição.</summary>
    public Scenario DuplicateScenario(int index, string? name = null)
    {
        var source = Scenarios[index];
        var copy = new Scenario
        {
            Name = UniqueScenarioName(name ?? source.Name + " (cópia)"),
            Network = Serialization.ProjectFile.CloneNetwork(source.Network),
        };
        Scenarios.Insert(index + 1, copy);
        ActiveScenario = index + 1;
        return copy;
    }

    /// <summary>Remove o cenário (sempre fica pelo menos um).</summary>
    public void RemoveScenario(int index)
    {
        if (Scenarios.Count <= 1 || index < 0 || index >= Scenarios.Count) return;
        Scenarios.RemoveAt(index);
        if (ActiveScenario > index || ActiveScenario >= Scenarios.Count) ActiveScenario = Math.Max(0, ActiveScenario - 1);
    }

    /// <summary>
    /// Traz os cenários de outro projeto. As redes são levadas para a foto deste: se as duas usam a mesma
    /// imagem (pelo nome do arquivo), cada ponto da foto continua no mesmo ponto, mesmo com calibrações
    /// diferentes. Se este projeto ainda não tem foto, adota a do outro. Se este projeto está vazio
    /// (um cenário sem nada desenhado), o cenário vazio dá lugar aos importados.
    /// </summary>
    /// <param name="sourceName">Nome do arquivo de origem, usado para nomear os cenários.</param>
    public ImportResult ImportScenarios(Project other, string sourceName)
    {
        var aligned = ImportAlignment.SameImage;
        Func<Vec2, Vec2>? map = null;
        var factor = 1.0;
        if (other.Background is { } ob && Background is { } bg)
        {
            if (string.Equals(Path.GetFileName(ob.ImagePath), Path.GetFileName(bg.ImagePath), StringComparison.OrdinalIgnoreCase))
            {
                factor = bg.MetersPerPixel / ob.MetersPerPixel;
                if (Math.Abs(factor - 1) > 1e-12 || Vec2.Distance(ob.Origin, bg.Origin) > 1e-9)
                    map = p => bg.ToWorld(ob.ToPixel(p));
            }
            else aligned = ImportAlignment.DifferentImage;
        }
        else if (other.Background is { } adopt && Background is null)
        {
            Background = new Background { ImagePath = adopt.ImagePath, MetersPerPixel = adopt.MetersPerPixel, Origin = adopt.Origin };
            Calibration = other.Calibration;
            aligned = ImportAlignment.AdoptedImage;
        }
        else if (other.Background is not null || Background is not null) aligned = ImportAlignment.DifferentImage;

        var replaceEmpty = Scenarios.Count == 1 && IsEmpty(Scenarios[0].Network);
        if (replaceEmpty)
        {
            Scenarios.Clear();
            Simulation = other.Simulation;
        }

        var added = new List<Scenario>();
        foreach (var s in other.Scenarios)
        {
            var net = Serialization.ProjectFile.CloneNetwork(s.Network);
            if (map is not null) net.Transform(map, factor);
            // Um cenário só (ou o "Atual" de um arquivo antigo) leva o nome do arquivo; vários, "arquivo · cenário".
            var name = other.Scenarios.Count == 1 ? sourceName : $"{sourceName} · {s.Name}";
            var scenario = new Scenario { Name = UniqueScenarioName(name), Network = net };
            Scenarios.Add(scenario);
            added.Add(scenario);
        }
        ActiveScenario = Scenarios.IndexOf(added[0]);
        return new ImportResult(added, aligned, replaceEmpty);
    }

    private static bool IsEmpty(Network n) => n.Links.Count == 0 && n.Connectors.Count == 0 && n.Inputs.Count == 0;

    /// <summary>
    /// Calibra a foto pela régua. Se a escala muda, a rede é escalada junto em torno da
    /// origem da foto, para continuar alinhada com o que foi desenhado sobre ela.
    /// </summary>
    /// <returns>Fator aplicado à rede (1 se nada mudou).</returns>
    public double Calibrate(Vec2 pixelA, Vec2 pixelB, double meters)
    {
        if (Background is null) throw new InvalidOperationException("Abra uma foto antes de calibrar.");
        var px = Vec2.Distance(pixelA, pixelB);
        if (!(px > 0)) throw new ArgumentException("Calibração inválida: os dois pontos coincidem.");
        if (!(meters > 0) || !double.IsFinite(meters))
            throw new ArgumentException("Calibração inválida: a distância real deve ser maior que zero.");

        var newMpp = meters / px;
        var factor = newMpp / Background.MetersPerPixel;
        foreach (var s in Scenarios) s.Network.ScaleAbout(Background.Origin, factor);
        Background.MetersPerPixel = newMpp;
        Calibration = new Calibration { A = pixelA, B = pixelB, Meters = meters };
        return factor;
    }
}
