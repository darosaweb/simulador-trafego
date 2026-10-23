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

public sealed class Project
{
    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;
    public Background? Background { get; set; }
    public Calibration? Calibration { get; set; }
    public Network Network { get; set; } = new();
    public SimulationSettings Simulation { get; set; } = new();

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
        Network.ScaleAbout(Background.Origin, factor);
        Background.MetersPerPixel = newMpp;
        Calibration = new Calibration { A = pixelA, B = pixelB, Meters = meters };
        return factor;
    }
}
