using Trafego.Core.Geometry;

namespace Trafego.Core.Tests;

public class GeometryTests
{
    [Fact]
    public void Polyline_At_interpola_por_comprimento_de_arco()
    {
        var p = new Polyline([new(0, 0), new(30, 0), new(30, 40)]);
        Assert.Equal(70, p.Length);
        Assert.Equal(new Vec2(15, 0), p.At(15).Point);
        var turn = p.At(50);
        Assert.Equal(new Vec2(30, 20), turn.Point);
        Assert.Equal(new Vec2(0, 1), turn.Tangent);
        Assert.Equal(new Vec2(0, 0), p.At(-5).Point);
        Assert.Equal(new Vec2(30, 40), p.At(999).Point);
    }

    [Fact]
    public void Polyline_At_ignora_segmento_de_comprimento_zero()
    {
        var p = new Polyline([new(0, 0), new(0, 0), new(10, 0)]);
        Assert.Equal(new Vec2(1, 0), p.At(0).Tangent);
    }

    [Fact]
    public void LeftNormal_com_Y_para_baixo_aponta_para_o_norte_quando_se_anda_para_leste()
    {
        Assert.Equal(new Vec2(0, -1), new Vec2(5, 0).LeftNormal());
    }

    [Fact]
    public void Project_da_lado_com_sinal_e_posicao_ao_longo()
    {
        var p = new Polyline([new(0, 0), new(10, 0), new(10, 10)]);
        var r = p.Project(new Vec2(12, 6))!.Value;
        Assert.Equal(1, r.Segment);
        Assert.Equal(2, r.Distance, 9);
        Assert.Equal(16, r.S, 9);
        // Descendo (sentido +Y, que é "sul" na tela), a esquerda é +X.
        Assert.Equal(2, r.Lateral, 9);

        var acima = p.Project(new Vec2(5, -3))!.Value;
        Assert.Equal(3, acima.Lateral, 9); // andando para leste, Y negativo é a esquerda
    }

    [Fact]
    public void Offset_de_reta_e_paralela_e_canto_em_esquadro_mantem_distancia()
    {
        var reta = Polyline.Offset([new(0, 0), new(10, 0)], 2);
        Assert.Equal([new Vec2(0, -2), new Vec2(10, -2)], reta);

        // Curva de 90°: o vértice deslocado fica a 2 m de cada um dos dois segmentos.
        var canto = Polyline.Offset([new(0, 0), new(10, 0), new(10, 10)], -2);
        Assert.Equal(8, canto[1].X, 9);
        Assert.Equal(2, canto[1].Y, 9);
    }

    [Fact]
    public void CatmullRom_passa_pelos_pontos_de_controle()
    {
        Vec2[] ctrl = [new(0, 0), new(100, 40), new(180, -30), new(300, 10)];
        var pts = Curves.CatmullRom(ctrl);
        foreach (var c in ctrl)
            Assert.Contains(pts, p => Vec2.Distance(p, c) < 1e-9);
        Assert.Equal(ctrl[^1], pts[^1]);
        // Amostragem de ~1 m: nenhum passo muito maior que isso.
        for (var i = 1; i < pts.Count; i++) Assert.True(Vec2.Distance(pts[i - 1], pts[i]) < 2.5);
    }

    [Fact]
    public void TangentBezier_sai_e_chega_nas_direcoes_dadas()
    {
        var pts = Curves.TangentBezier(new(0, 0), new(1, 0), new(50, 50), new(0, 1));
        Assert.Equal(new Vec2(0, 0), pts[0]);
        Assert.Equal(new Vec2(50, 50), pts[^1]);
        Assert.True((pts[1] - pts[0]).Normalized().Dot(new Vec2(1, 0)) > 0.99);
        Assert.True((pts[^1] - pts[^2]).Normalized().Dot(new Vec2(0, 1)) > 0.99);
    }
}
