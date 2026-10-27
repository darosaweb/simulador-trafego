using System.Globalization;
using System.Text.RegularExpressions;
using Trafego.Core.Model;

namespace Trafego.Sumo;

/// <summary>
/// Traduz os avisos do SUMO (que citam ids como 'L127_0_1' ou 'J140') para os nomes do editor e
/// diz a que objetos do mapa eles se referem. Os avisos mais comuns ganham uma explicação em português;
/// os outros ficam em inglês, só com os ids trocados pelos nomes.
/// </summary>
public static class SumoMessages
{
    /// <summary>Um id do SUMO lido de volta: o nome para mostrar e o objeto a destacar.</summary>
    private sealed record Ref(string Name, MapTarget? Target);

    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    public static MapNotice Explain(SumoExport export, Network net, string warning)
    {
        var text = Regex.Replace(warning.Trim(), @"^Warning:\s*", "", RegexOptions.IgnoreCase);
        var targets = new List<MapTarget>();
        Ref R(string id)
        {
            if (id.Contains("->")) // conexão faixa a faixa: "C20_0->L3_4_0"
            {
                var ends = id.Split("->").Select(R).ToList();
                return new(string.Join(" → ", ends.Select(e => e.Name)), ends.FirstOrDefault(e => e.Target is not null)?.Target);
            }
            var r = Resolve(export, net, id);
            if (r.Target is { } t && !targets.Contains(t)) targets.Add(t);
            return r;
        }

        Match m;
        if ((m = Regex.Match(text, @"^Lane '([^']+)' is not connected from any incoming edge at junction '([^']+)'\.?$")).Success)
        {
            var (lane, node) = (R(m.Groups[1].Value), R(m.Groups[2].Value));
            text = $"A {lane.Name} não recebe nenhum conector ({node.Name}): os veículos só chegam a ela trocando de faixa " +
                   "mais adiante. Se ela deveria receber tráfego, ligue um conector a essa faixa.";
        }
        else if ((m = Regex.Match(text, @"^Edge '([^']+)' is not connected to outgoing edges at junction '([^']+)'\.?$")).Success)
        {
            var (edge, node) = (R(m.Groups[1].Value), R(m.Groups[2].Value));
            text = $"{Cap(edge.Name)} não segue para lugar nenhum ({node.Name}): falta um conector ligando o fim dele à continuação.";
        }
        else if ((m = Regex.Match(text, @"^Found sharp turn with radius ([\d.]+) at the (start|end) of edge '([^']+)'\.?$")).Success)
        {
            var edge = R(m.Groups[3].Value);
            var where = m.Groups[2].Value == "start" ? "no início" : "no fim";
            text = $"Curva muito fechada {where} de {edge.Name} (raio de {Meters(m.Groups[1].Value)}): " +
                   "os veículos reduzem muito a velocidade ali. Suavize a curva ou o ponto onde o conector chega.";
        }
        else if ((m = Regex.Match(text, @"^Intersecting left turns at junction '([^']+)' from lane '([^']+)' and lane '([^']+)'")).Success)
        {
            var (node, a, b) = (R(m.Groups[1].Value), R(m.Groups[2].Value), R(m.Groups[3].Value));
            text = $"Conversões à esquerda que se cruzam ({node.Name}): {a.Name} e {b.Name}. " +
                   "Afaste os conectores ou dê mais espaço ao cruzamento.";
        }
        else if ((m = Regex.Match(text, @"^Speed of \w+ connection '([^']+)' reduced by ([\d.]+) due to turning radius of ([\d.]+)")).Success)
        {
            var conn = R(m.Groups[1].Value);
            var drop = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 3.6;
            text = $"Curva fechada na passagem {conn.Name} (raio de {Meters(m.Groups[3].Value)}): " +
                   $"a velocidade ali cai {drop.ToString("0", Pt)} km/h. Normal em alças e conversões; se não for o caso, suavize a curva.";
        }
        else if ((m = Regex.Match(text, @"^Could not avoid overlapping shape at node '([^']+)' for edge '([^']+)'\.?$")).Success)
        {
            var (node, edge) = (R(m.Groups[1].Value), R(m.Groups[2].Value));
            text = $"A forma de {edge.Name} se sobrepõe a outra ({node.Name}). Confira se os links e conectores ali não estão encavalados.";
        }
        else
        {
            // Sem tradução: troca só os ids citados entre aspas.
            text = Regex.Replace(text, "'([^']+)'", g =>
            {
                var r = R(g.Groups[1].Value);
                return r.Target is null ? g.Value : $"'{r.Name}'";
            });
        }
        return new MapNotice(text, targets);
    }

    /// <summary>Avisos da exportação (do editor e do netconvert), sem repetir, já traduzidos.</summary>
    public static List<MapNotice> ExplainAll(SumoExport export, Network net) =>
        [.. export.Warnings.Distinct().Where(w => !IsExpected(export, net, w)).Select(w =>
        {
            var n = Explain(export, net, w);
            if (w.StartsWith("Warning", StringComparison.OrdinalIgnoreCase)) return n;
            // Avisos do próprio editor citam os objetos pelo nome.
            return n with { Targets = [.. MapNotice.FindByName(net, w).Concat(n.Targets).Distinct()] };
        })];

    /// <summary>
    /// Avisos que o modelo link/conector gera sem haver problema. Hoje: a faixa "sem entrada" logo depois de
    /// um nó, quando um conector chega nela mais adiante no mesmo link — o link é cortado onde o conector
    /// chega, e o trecho antes do corte fica com a faixa vazia, o que é o esperado.
    /// </summary>
    public static bool IsExpected(SumoExport export, Network net, string warning)
    {
        var m = Regex.Match(warning, @"Lane 'L(\d+)_(\d+)_(\d+)' is not connected from any incoming edge");
        if (!m.Success) return false;
        var (linkId, piece, lane) = (Int(m, 1), Int(m, 2), Int(m, 3));
        return net.Connectors.Any(c =>
            c.ToLinkId == linkId && lane >= c.ToLane && lane < c.ToLane + c.LaneCount &&
            export.Topology.Covers(c) && export.Topology.ToNode(c).Cut > piece);
    }

    /// <summary>Lê um id do SUMO: faixa ou trecho de link, conector, nó de cruzamento ou ponto de corte.</summary>
    private static Ref Resolve(SumoExport export, Network net, string id)
    {
        Match m;
        if ((m = Regex.Match(id, @"^:?(J\d+|n\d+_\d+)_\d+(_\d+)?$")).Success && !id.StartsWith('n')) id = m.Groups[1].Value; // faixa interna do nó

        if ((m = Regex.Match(id, @"^L(\d+)_(\d+)(?:_(\d+))?$")).Success && net.FindLink(Int(m, 1)) is { } link)
        {
            var piece = Int(m, 2);
            var pieces = export.Topology.Has(link.Id) ? export.Topology.PieceCount(link.Id) : 1;
            var where = pieces > 1 ? $" (trecho {piece + 1} de {pieces})" : "";
            if (m.Groups[3].Success)
            {
                var lane = Int(m, 3);
                return new($"faixa {lane + 1} de {link.Name}{where}", new(MapTargetKind.Link, link.Id, piece, lane));
            }
            return new($"{link.Name}{where}", new(MapTargetKind.Link, link.Id, piece));
        }
        if ((m = Regex.Match(id, @"^C(\d+)(?:_(\d+))?$")).Success && net.FindConnector(Int(m, 1)) is { } c)
            return m.Groups[2].Success
                ? new($"faixa {Int(m, 2) + 1} de {c.Name}", new(MapTargetKind.Connector, c.Id, Lane: Int(m, 2)))
                : new(c.Name, new(MapTargetKind.Connector, c.Id));
        if ((m = Regex.Match(id, @"^J(\d+)$")).Success && export.Analysis.FindJunction(Int(m, 1)) is { } j)
            return new($"no Nó {j.Key}", new(MapTargetKind.Junction, j.Key));
        if ((m = Regex.Match(id, @"^n(\d+)_(\d+)$")).Success && net.FindLink(Int(m, 1)) is { } at)
        {
            var cut = Int(m, 2);
            var cuts = export.Topology.Has(at.Id) ? export.Topology.Cuts(at.Id) : Array.Empty<double>();
            var s = cut < cuts.Count ? cuts[cut] : 0;
            var piece = Math.Min(cut, Math.Max(cuts.Count - 2, 0));
            return new($"em {at.Name}, a {s.ToString("0", Pt)} m do início", new(MapTargetKind.Link, at.Id, piece));
        }
        return new(id, null);
    }

    private static int Int(Match m, int group) => int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);

    private static string Meters(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? $"{v.ToString("0.#", Pt)} m" : value + " m";

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0], Pt) + s[1..];
}
