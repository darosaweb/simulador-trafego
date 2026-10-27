using System.Text.RegularExpressions;

namespace Trafego.Core.Model;

public enum MapTargetKind { Link, Connector, Input, Measurement, Junction }

/// <summary>Objeto do mapa citado num aviso, para destacar e enquadrar ao clicar.</summary>
/// <param name="Id">Id do objeto, ou a chave do nó (<see cref="MapTargetKind.Junction"/>).</param>
/// <param name="Piece">Trecho do link (entre dois cortes), quando o aviso fala de um trecho só.</param>
/// <param name="Lane">Faixa do link ou do conector, quando o aviso fala de uma faixa só.</param>
public sealed record MapTarget(MapTargetKind Kind, int Id, int? Piece = null, int? Lane = null);

/// <summary>Aviso com os objetos do mapa a que ele se refere.</summary>
public sealed record MapNotice(string Text, IReadOnlyList<MapTarget> Targets)
{
    /// <summary>
    /// Objetos citados pelo nome no texto (ex.: "Conector 5: sai do início de Link 3"), na ordem em que
    /// aparecem. Nomes que são prefixo de outros ("Link 1" × "Link 12") não se confundem.
    /// </summary>
    public static List<MapTarget> FindByName(Network net, string text)
    {
        IEnumerable<(string Name, MapTarget Target)> named =
        [
            .. net.Links.Select(l => (l.Name, new MapTarget(MapTargetKind.Link, l.Id))),
            .. net.Connectors.Select(c => (c.Name, new MapTarget(MapTargetKind.Connector, c.Id))),
            .. net.Inputs.Select(i => (i.Name, new MapTarget(MapTargetKind.Input, i.Id))),
            .. net.Measurements.Select(m => (m.Name, new MapTarget(MapTargetKind.Measurement, m.Id))),
        ];
        var taken = new bool[text.Length];
        var found = new List<(int At, MapTarget Target)>();
        foreach (var (name, target) in named.Where(n => n.Name.Trim().Length > 0).OrderByDescending(n => n.Name.Length))
        {
            foreach (Match m in Regex.Matches(text, $@"(?<![\w]){Regex.Escape(name)}(?![\w])"))
            {
                if (Enumerable.Range(m.Index, m.Length).Any(i => taken[i])) continue;
                for (var i = m.Index; i < m.Index + m.Length; i++) taken[i] = true;
                if (!found.Any(f => f.Target == target)) found.Add((m.Index, target));
            }
        }
        return [.. found.OrderBy(f => f.At).Select(f => f.Target)];
    }

    /// <summary>Aviso da verificação da rede: o objeto do aviso primeiro, depois os outros citados.</summary>
    public static MapNotice Of(Network net, ValidationIssue issue)
    {
        var targets = FindByName(net, issue.Message);
        if (TargetOf(net, issue.ObjectId) is { } own)
        {
            targets.Remove(own);
            targets.Insert(0, own);
        }
        return new MapNotice(issue.Message, targets);
    }

    public static MapTarget? TargetOf(Network net, int objectId) =>
        net.FindLink(objectId) is not null ? new(MapTargetKind.Link, objectId)
        : net.FindConnector(objectId) is not null ? new(MapTargetKind.Connector, objectId)
        : net.FindInput(objectId) is not null ? new(MapTargetKind.Input, objectId)
        : net.FindMeasurement(objectId) is not null ? new(MapTargetKind.Measurement, objectId)
        : null;
}
