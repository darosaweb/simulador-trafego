namespace Trafego.Core.Model;

/// <summary>Volume de uma entrada a partir do início de um intervalo de tempo.</summary>
public sealed class VolumeAt
{
    /// <summary>Início do intervalo, s desde 00:00 (um dos <see cref="DemandSettings.SliceStarts"/>).</summary>
    public double SliceStart { get; set; }
    public double Vph { get; set; }
}

/// <summary>Célula da matriz O/D: veículos por hora da entrada (origem) até a saída (destino) num intervalo.</summary>
public sealed class OdEntry
{
    /// <summary>Id da entrada de veículos (origem).</summary>
    public int OriginId { get; set; }
    /// <summary>Id do link por onde os veículos saem da rede (destino).</summary>
    public int DestinationLinkId { get; set; }
    public double SliceStart { get; set; }
    public double Vph { get; set; }
}

/// <summary>
/// Parte de um par O/D que vai por um caminho específico. O caminho é identificado pelos conectores
/// que usa, o que continua valendo quando a geometria dos links muda.
/// </summary>
public sealed class RouteChoice
{
    public int OriginId { get; set; }
    public int DestinationLinkId { get; set; }
    public List<int> ConnectorIds { get; set; } = [];
    /// <summary>Peso relativo (as rotas do par são normalizadas pela soma).</summary>
    public double Share { get; set; }
}

/// <summary>
/// Demanda da rede: intervalos de tempo do dia, matriz O/D e escolha de rotas.
/// Os intervalos valem para todo o dia e se repetem todos os dias; cada um vai do seu início até o
/// início do seguinte.
/// </summary>
public sealed class DemandSettings
{
    /// <summary>Inícios dos intervalos, s desde 00:00. Sempre há pelo menos um.</summary>
    public List<double> SliceStarts { get; set; } = [0];
    public List<OdEntry> Matrix { get; set; } = [];
    public List<RouteChoice> RouteChoices { get; set; } = [];

    public List<double> SortedSlices()
    {
        var s = SliceStarts.Distinct().Order().ToList();
        return s.Count == 0 ? [0] : s;
    }

    /// <summary>Intervalo em vigor num horário do dia (antes do primeiro, vale o último do dia anterior).</summary>
    public double SliceAt(double timeOfDay)
    {
        var slices = SortedSlices();
        var tod = ((timeOfDay % SignalController.Day) + SignalController.Day) % SignalController.Day;
        return slices.LastOrDefault(s => s <= tod + 1e-9, slices[^1]);
    }

    /// <summary>
    /// Janelas de tempo absoluto entre begin e end, cada uma com o intervalo do dia em vigor.
    /// </summary>
    public List<(double Begin, double End, double Slice)> Windows(double begin, double end)
    {
        var bounds = new SortedSet<double> { begin, end };
        var slices = SortedSlices();
        for (var day = Math.Floor(begin / SignalController.Day); day * SignalController.Day <= end; day++)
            foreach (var s in slices)
            {
                var t = day * SignalController.Day + s;
                if (t > begin && t < end) bounds.Add(t);
            }
        var list = bounds.ToList();
        var result = new List<(double, double, double)>();
        for (var i = 0; i < list.Count - 1; i++)
            if (list[i + 1] - list[i] > 1e-6) result.Add((list[i], list[i + 1], SliceAt(list[i])));
        return result;
    }

    public bool UsesMatrix(int inputId) => Matrix.Any(e => e.OriginId == inputId && e.Vph > 0);

    public double OdVolume(int originId, int destinationLinkId, double slice) =>
        Matrix.Find(e => e.OriginId == originId && e.DestinationLinkId == destinationLinkId && Math.Abs(e.SliceStart - slice) < 1e-6)?.Vph ?? 0;

    public void SetOdVolume(int originId, int destinationLinkId, double slice, double vph)
    {
        Matrix.RemoveAll(e => e.OriginId == originId && e.DestinationLinkId == destinationLinkId && Math.Abs(e.SliceStart - slice) < 1e-6);
        if (vph > 0) Matrix.Add(new OdEntry { OriginId = originId, DestinationLinkId = destinationLinkId, SliceStart = slice, Vph = vph });
    }

    /// <summary>Pares O/D com algum volume, em ordem.</summary>
    public List<(int Origin, int Destination)> Pairs() =>
        [.. Matrix.Where(e => e.Vph > 0).Select(e => (e.OriginId, e.DestinationLinkId)).Distinct().OrderBy(p => p.OriginId).ThenBy(p => p.DestinationLinkId)];

    public List<RouteChoice> ChoicesOf(int originId, int destinationLinkId) =>
        [.. RouteChoices.Where(r => r.OriginId == originId && r.DestinationLinkId == destinationLinkId)];

    /// <summary>Troca o início de um intervalo, levando junto os volumes que estavam nele.</summary>
    public void MoveSlice(double from, double to, Network net)
    {
        for (var i = 0; i < SliceStarts.Count; i++)
            if (Math.Abs(SliceStarts[i] - from) < 1e-6) SliceStarts[i] = to;
        foreach (var e in Matrix.Where(e => Math.Abs(e.SliceStart - from) < 1e-6)) e.SliceStart = to;
        foreach (var v in net.Inputs.SelectMany(i => i.Volumes).Where(v => Math.Abs(v.SliceStart - from) < 1e-6)) v.SliceStart = to;
    }

    /// <summary>Remove o intervalo e os volumes dele (o anterior passa a cobrir o período).</summary>
    public void RemoveSlice(double slice, Network net)
    {
        if (SliceStarts.Count <= 1) return;
        SliceStarts.RemoveAll(s => Math.Abs(s - slice) < 1e-6);
        Matrix.RemoveAll(e => Math.Abs(e.SliceStart - slice) < 1e-6);
        foreach (var i in net.Inputs) i.Volumes.RemoveAll(v => Math.Abs(v.SliceStart - slice) < 1e-6);
    }

    /// <summary>Tira da matriz e das rotas o que aponta para entradas ou links que não existem mais.</summary>
    public void Prune(Network net)
    {
        bool Valid(int origin, int dest) => net.FindInput(origin) is not null && net.FindLink(dest) is not null;
        Matrix.RemoveAll(e => !Valid(e.OriginId, e.DestinationLinkId));
        RouteChoices.RemoveAll(r => !Valid(r.OriginId, r.DestinationLinkId) || r.ConnectorIds.Any(id => net.FindConnector(id) is null));
    }
}
