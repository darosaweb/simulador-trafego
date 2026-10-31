using System.Globalization;
using System.Text;

namespace Trafego.Core.Model;

/// <summary>Uma contagem de campo: quantos carros e caminhões passaram numa via durante alguns segundos.</summary>
public sealed record CountSample(string Via, DateTime Start, double DurationS, int Cars, int Trucks);

/// <summary>O que a importação fez numa entrada: intervalos preenchidos, contagens usadas e % de pesados.</summary>
public sealed record CountApplied(VehicleInput Input, int Slices, int Samples, double TruckShare, bool UsesMatrix);

public sealed record CountImportResult(List<CountApplied> Applied, List<string> Unmatched, int CreatedSlices);

/// <summary>
/// Importa o CSV do Contador de Vias (contador/) para a demanda: cada via com o mesmo nome de uma entrada
/// vira o volume dessa entrada no intervalo em que a contagem foi feita. Várias contagens no mesmo
/// intervalo são somadas (total de veículos sobre o tempo total), como o próprio contador faz.
/// </summary>
public static class CountImport
{
    /// <summary>
    /// Lê o CSV (ponto e vírgula, cabeçalho
    /// <c>data;hora_inicio;via;duracao_s;carros;caminhoes;...</c>). Linhas que não dão para ler são ignoradas.
    /// </summary>
    public static List<CountSample> Parse(string text)
    {
        var lines = text.TrimStart('﻿').Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0) return [];
        var head = Split(lines[0]).Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string name) => head.IndexOf(name);
        int date = Col("data"), time = Col("hora_inicio"), via = Col("via"), dur = Col("duracao_s"), cars = Col("carros"), trucks = Col("caminhoes");
        if (new[] { date, time, via, dur, cars, trucks }.Any(i => i < 0))
            throw new FormatException("O arquivo não é um CSV do Contador de Vias (faltam colunas data, hora_inicio, via, duracao_s, carros ou caminhoes).");

        var result = new List<CountSample>();
        foreach (var line in lines.Skip(1))
        {
            var f = Split(line);
            if (f.Count < head.Count) continue;
            if (!DateTime.TryParseExact($"{f[date].Trim()} {f[time].Trim()}", ["dd/MM/yyyy HH:mm", "d/M/yyyy H:mm", "yyyy-MM-dd HH:mm"],
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)) continue;
            if (!double.TryParse(f[dur].Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) || s <= 0) continue;
            if (!int.TryParse(f[cars].Trim(), out var c) || !int.TryParse(f[trucks].Trim(), out var t)) continue;
            result.Add(new CountSample(f[via].Trim(), start, s, c, t));
        }
        return result;
    }

    /// <summary>
    /// Leva as contagens para as entradas da rede. Se a demanda só tem o intervalo padrão, cria um
    /// intervalo por hora contada; senão, usa os intervalos que já existem. O % de pesados da entrada
    /// passa a ser o de todas as contagens dela.
    /// </summary>
    public static CountImportResult Apply(Network net, IEnumerable<CountSample> samples)
    {
        var list = samples.ToList();
        var byName = net.Inputs.GroupBy(i => Key(i.Name)).ToDictionary(g => g.Key, g => g.First());
        var matched = list.Where(s => byName.ContainsKey(Key(s.Via))).ToList();
        var unmatched = list.Select(s => s.Via).Where(v => !byName.ContainsKey(Key(v)))
            .Distinct(StringComparer.CurrentCultureIgnoreCase).Order().ToList();

        var demand = net.Demand;
        var created = 0;
        if (demand.SortedSlices().Count == 1)
            foreach (var hour in matched.Select(s => (double)s.Start.Hour * 3600).Distinct().Order())
                if (!demand.SliceStarts.Any(x => Math.Abs(x - hour) < 1e-6))
                {
                    demand.SliceStarts.Add(hour);
                    created++;
                }

        var applied = new List<CountApplied>();
        foreach (var group in matched.GroupBy(s => byName[Key(s.Via)]))
        {
            var input = group.Key;
            var slices = group.GroupBy(s => demand.SliceAt(s.Start.TimeOfDay.TotalSeconds)).ToList();
            foreach (var g in slices)
                input.SetVolume(g.Key, Math.Round(g.Sum(s => s.Cars + s.Trucks) * 3600 / g.Sum(s => s.DurationS)));
            var vehicles = group.Sum(s => s.Cars + s.Trucks);
            if (vehicles > 0) input.TruckShare = (double)group.Sum(s => s.Trucks) / vehicles;
            applied.Add(new CountApplied(input, slices.Count, group.Count(), input.TruckShare, demand.UsesMatrix(input.Id)));
        }
        return new CountImportResult(applied, unmatched, created);
    }

    /// <summary>Nome para comparar: sem acentos, minúsculo e com os espaços normalizados.</summary>
    public static string Key(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(ch));
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Divide uma linha por ponto e vírgula, respeitando campos entre aspas.</summary>
    private static List<string> Split(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (ch == ';' && !quoted) { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
