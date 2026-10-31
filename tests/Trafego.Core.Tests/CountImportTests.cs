using Trafego.Core.Model;

namespace Trafego.Core.Tests;

public class CountImportTests
{
    // Como o Contador de Vias exporta: BOM, ponto e vírgula, via entre aspas.
    private const string Csv = "﻿data;hora_inicio;via;duracao_s;carros;caminhoes;total;veic_h;pct_pesados\n" +
                               "31/10/2026;07:10;\"Entrada oeste\";60;18;2;20;1200;10\n" +
                               "31/10/2026;07:40;\"Entrada oeste\";60;14;6;20;1200;30\n" +
                               "31/10/2026;08:05;\"Entrada Oeste\";120;30;0;30;900;0\n" +
                               "31/10/2026;07:20;\"Entrada sul\";60;5;0;5;300;0\n" +
                               "31/10/2026;07:25;\"Alça leste\";60;9;1;10;600;10\n";

    private static (Network Net, VehicleInput West, VehicleInput South) Net()
    {
        var net = new Network();
        var a = net.AddLink([new(0, 0), new(100, 0)]);
        var b = net.AddLink([new(0, 50), new(100, 50)]);
        var west = net.AddInput(a, 600);
        west.Name = "Entrada Oeste";
        var south = net.AddInput(b, 600);
        south.Name = "Entrada sul";
        return (net, west, south);
    }

    [Fact]
    public void Parse_reads_the_counter_csv()
    {
        var samples = CountImport.Parse(Csv);
        Assert.Equal(5, samples.Count);
        Assert.Equal("Alça leste", samples[4].Via);
        Assert.Equal(new DateTime(2026, 10, 31, 8, 5, 0), samples[2].Start);
        Assert.Equal(120, samples[2].DurationS);
        Assert.Equal(6, samples[1].Trucks);
    }

    [Fact]
    public void Parse_rejects_other_files()
    {
        Assert.Throws<FormatException>(() => CountImport.Parse("a;b;c\n1;2;3"));
    }

    [Fact]
    public void Creates_hourly_slices_and_sums_samples_in_each()
    {
        var (net, west, south) = Net();
        var result = CountImport.Apply(net, CountImport.Parse(Csv));

        Assert.Equal([0, 7 * 3600, 8 * 3600], net.Demand.SortedSlices());
        Assert.Equal(2, result.CreatedSlices);
        // 07h: 40 veículos em 120 s; 08h: 30 em 120 s.
        Assert.Equal(1200, west.VolumeAt(7 * 3600));
        Assert.Equal(900, west.VolumeAt(8 * 3600));
        Assert.Equal(600, west.VolumeAt(0));
        Assert.Equal(8.0 / 70, west.TruckShare, 6);
        Assert.Equal(300, south.VolumeAt(7 * 3600));
        Assert.Equal(0, south.TruckShare);
        Assert.Equal(["Alça leste"], result.Unmatched);
        Assert.Equal(2, result.Applied.Count);
    }

    [Fact]
    public void Uses_existing_slices_when_there_are_several()
    {
        var (net, west, _) = Net();
        net.Demand.SliceStarts = [7 * 3600, 7.5 * 3600];
        CountImport.Apply(net, CountImport.Parse(Csv));

        Assert.Equal([7 * 3600, 7.5 * 3600], net.Demand.SortedSlices());
        Assert.Equal(1200, west.VolumeAt(7 * 3600));          // 07:10
        Assert.Equal(1000, west.VolumeAt(7.5 * 3600));        // 07:40 e 08:05: 50 veículos em 180 s
    }

    [Fact]
    public void Names_match_without_accents_or_case()
    {
        Assert.Equal(CountImport.Key("Alça  Leste "), CountImport.Key("alca leste"));
    }
}
