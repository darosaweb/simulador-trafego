using Trafego.Core.Geometry;
using Trafego.Core.Model;
using Trafego.Core.Serialization;

namespace Trafego.Core.Tests;

public class NetworkTests
{
    /// <summary>Via principal de 3 faixas para leste e uma alça de 1 faixa saindo para o sul.</summary>
    private static (Network net, Link main, Link ramp) Diverge()
    {
        var net = new Network();
        var main = net.AddLink([new(0, 0), new(200, 0)], laneCount: 3);
        var ramp = net.AddLink([new(120, 20), new(140, 80)]);
        return (net, main, ramp);
    }

    [Fact]
    public void Faixas_se_distribuem_em_torno_do_eixo_com_a_zero_a_direita()
    {
        var (_, main, _) = Diverge();
        Assert.Equal(10.5, main.TotalWidth, 9);
        Assert.Equal(-5.25, main.LaneRightEdge(0), 9);
        Assert.Equal(-3.5, main.LaneCenter(0), 9);
        Assert.Equal(5.25, main.LaneLeftEdge(2), 9);
        Assert.Equal(0, main.LaneAt(-5));
        Assert.Equal(1, main.LaneAt(0));
        Assert.Equal(2, main.LaneAt(5));
        Assert.Null(main.LaneAt(6));
    }

    [Fact]
    public void HitTest_acha_link_e_faixa()
    {
        var (net, main, _) = Diverge();
        // Andando para leste, a direita é +Y: y = +4 cai na faixa 0.
        var hit = HitTest.Link(net, new Vec2(50, 4), tolerance: 0.5)!.Value;
        Assert.Same(main, hit.Link);
        Assert.Equal(0, hit.Lane);
        Assert.Equal(50, hit.Projection.S, 9);
        Assert.Equal(2, HitTest.Link(net, new Vec2(50, -4), 0.5)!.Value.Lane);
        Assert.Null(HitTest.Link(net, new Vec2(50, 30), 0.5));
    }

    [Fact]
    public void HitTest_nao_pega_link_pelo_prolongamento_do_eixo()
    {
        // Dois links em sequência: um clique no fim do primeiro não pode cair no segundo,
        // que está alinhado com ele (o lateral em relação ao segundo é ~0).
        var net = new Network();
        var first = net.AddLink([new(0, 0), new(100, 0)], laneCount: 2);
        net.AddLink([new(130, 0), new(250, 0)], laneCount: 2);
        var hit = HitTest.Link(net, new Vec2(98, 1.5), 0.5)!.Value;
        Assert.Same(first, hit.Link);
        Assert.Null(HitTest.Link(net, new Vec2(115, 0), 0.5));
    }

    [Fact]
    public void Conector_sai_da_faixa_certa_e_chega_no_inicio_do_destino()
    {
        var (net, main, ramp) = Diverge();
        var c = net.AddConnector(main, 0, 100, ramp, 0, 0);
        var g = net.GeometryOf(c)!;
        var start = g.Axis.At(0).Point;
        var end = g.Axis.At(g.Axis.Length).Point;
        Assert.Equal(new Vec2(100, 3.5), start); // centro da faixa 0 da principal
        Assert.Equal(new Vec2(120, 20), end);
        Assert.Equal([0.0], g.LaneOffsets);
    }

    [Fact]
    public void Conector_de_duas_faixas_usa_o_centro_do_grupo()
    {
        var net = new Network();
        var a = net.AddLink([new(0, 0), new(100, 0)], laneCount: 3);
        var b = net.AddLink([new(110, 0), new(200, 0)], laneCount: 2);
        var c = net.AddConnector(a, 1, 100, b, 0, 0, laneCount: 2);
        var g = net.GeometryOf(c)!;
        Assert.Equal(new Vec2(100, -1.75), g.Axis.At(0).Point); // faixas 1 e 2 de a
        Assert.Equal(new Vec2(110, 0), g.Axis.At(g.Axis.Length).Point); // as 2 faixas de b
        Assert.Equal([-1.75, 1.75], g.LaneOffsets);
    }

    [Fact]
    public void Conector_com_faixas_invalidas_nao_tem_geometria_e_falha_na_validacao()
    {
        var (net, main, ramp) = Diverge();
        var c = net.AddConnector(main, 0, 100, ramp, 0, 0, laneCount: 2); // a alça só tem 1 faixa
        Assert.Null(net.GeometryOf(c));
        Assert.Contains(Validation.Check(net), i => i.ObjectId == c.Id && i.Severity == Severity.Error);
    }

    [Fact]
    public void FitConnectorsTo_corrige_faixas_e_posicao_depois_de_editar_o_link()
    {
        var (net, main, ramp) = Diverge();
        var c = net.AddConnector(main, 2, 190, ramp, 0, 0);
        main.Lanes.RemoveAt(2);
        main.Points[1] = new Vec2(150, 0);
        net.FitConnectorsTo(main);
        Assert.Equal(1, c.FromLane);
        Assert.Equal(150, c.FromS, 9);
        Assert.Empty(Validation.Check(net));
    }

    [Fact]
    public void Remover_link_remove_os_conectores_dele()
    {
        var (net, main, ramp) = Diverge();
        net.AddConnector(main, 0, 100, ramp, 0, 0);
        net.RemoveLink(ramp.Id);
        Assert.Empty(net.Connectors);
        Assert.Single(net.Links);
    }

    [Fact]
    public void Calibrar_define_escala_e_reescala_a_rede_junto_com_a_foto()
    {
        var project = new Project { Background = new Background { ImagePath = "foto.png", MetersPerPixel = 1 } };
        var link = project.Network.AddLink([new(100, 0), new(300, 0)]);
        var factor = project.Calibrate(new Vec2(0, 0), new Vec2(200, 0), 100); // 0,5 m/px
        Assert.Equal(0.5, factor, 9);
        Assert.Equal(0.5, project.Background.MetersPerPixel, 9);
        Assert.Equal(new Vec2(50, 0), link.Points[0]);
        Assert.Equal(100, link.Axis().Length, 9);

        // Régua de 370,20 m da foto do spike: ~0,624 m/px.
        project.Calibrate(new Vec2(746, 160), new Vec2(161, 259), 370.2);
        Assert.InRange(project.Background.MetersPerPixel, 0.62, 0.63);
    }

    [Fact]
    public void Calibracao_invalida_e_rejeitada()
    {
        var project = new Project { Background = new Background() };
        Assert.Throws<ArgumentException>(() => project.Calibrate(new(1, 1), new(1, 1), 10));
        Assert.Throws<ArgumentException>(() => project.Calibrate(new(0, 0), new(1, 0), 0));
        Assert.Throws<ArgumentException>(() => project.Calibrate(new(0, 0), new(1, 0), double.NaN));
        Assert.Throws<InvalidOperationException>(() => new Project().Calibrate(new(0, 0), new(1, 0), 1));
    }

    [Fact]
    public void Projeto_ida_e_volta_em_json_preserva_tudo()
    {
        var (net, main, ramp) = Diverge();
        main.Smooth = true;
        main.SpeedKmh = 100;
        main.Lanes[1].Width = 3.2;
        var c = net.AddConnector(main, 0, 100, ramp, 0, 0);
        c.Points.Add(new Vec2(115, 10));
        var project = new Project
        {
            Background = new Background { ImagePath = "foto.png", MetersPerPixel = 0.6, Origin = new(1, 2) },
            Calibration = new Calibration { A = new(1, 1), B = new(5, 5), Meters = 3 },
            Network = net,
        };

        var back = ProjectFile.FromJson(ProjectFile.ToJson(project));
        Assert.Equal(ProjectFile.ToJson(project), ProjectFile.ToJson(back));
        Assert.Equal(new Vec2(1, 2), back.Background!.Origin);
        Assert.Equal(3.2, back.Network.FindLink(main.Id)!.Lanes[1].Width);
        Assert.Equal(new Vec2(115, 10), back.Network.Connectors[0].Points[0]);
        Assert.Equal(net.NextId, back.Network.NextId);
    }

    [Fact]
    public void Salvar_grava_foto_com_caminho_relativo_e_abrir_volta_absoluto()
    {
        var dir = Directory.CreateTempSubdirectory("trafego-test").FullName;
        try
        {
            var image = Path.Combine(dir, "fotos", "local.png");
            var project = new Project { Background = new Background { ImagePath = image } };
            var file = Path.Combine(dir, "cenario" + ProjectFile.Extension);
            ProjectFile.Save(project, file);

            Assert.Contains("fotos", File.ReadAllText(file));
            Assert.DoesNotContain(dir.Replace("\\", "\\\\"), File.ReadAllText(file));
            Assert.Equal(image, ProjectFile.Load(file).Background!.ImagePath);
            Assert.Equal(image, project.Background.ImagePath); // o projeto em memória não muda
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Formato_mais_novo_que_o_suportado_e_rejeitado()
    {
        Assert.Throws<InvalidDataException>(() => ProjectFile.FromJson("""{"format": 99}"""));
    }
}
