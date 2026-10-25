using Trafego.Sumo;

namespace Trafego.Core.Tests;

/// <summary>Usam o FFmpeg de verdade; sem ele instalado, passam sem fazer nada.</summary>
public class VideoTests
{
    [Fact]
    public void Gera_um_mp4_a_partir_de_quadros_brutos()
    {
        if (FfmpegEncoder.Locate() is null) return;
        var dir = Directory.CreateTempSubdirectory("trafego-video").FullName;
        try
        {
            var file = Path.Combine(dir, "teste.mp4");
            using (var enc = new FfmpegEncoder(file, 161, 91, 30)) // ímpares: arredonda para 160 × 90
            {
                Assert.Equal((160, 90), (enc.Width, enc.Height));
                var frame = new byte[enc.FrameBytes];
                for (var f = 0; f < 45; f++)
                {
                    // Uma faixa clara que anda da esquerda para a direita.
                    Array.Clear(frame);
                    for (var y = 0; y < enc.Height; y++)
                    for (var x = f * 3; x < Math.Min(enc.Width, f * 3 + 10); x++)
                    {
                        var i = (y * enc.Width + x) * 4;
                        frame[i] = 60; frame[i + 1] = 200; frame[i + 2] = 80; frame[i + 3] = 255;
                    }
                    enc.Write(frame);
                }
                enc.Finish();
            }
            var bytes = File.ReadAllBytes(file);
            Assert.True(bytes.Length > 1000);
            Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(bytes, 4, 4)); // cabeçalho de MP4
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Cancelar_apaga_o_arquivo_incompleto()
    {
        if (FfmpegEncoder.Locate() is null) return;
        var dir = Directory.CreateTempSubdirectory("trafego-video").FullName;
        try
        {
            var file = Path.Combine(dir, "cancelado.mp4");
            using (var enc = new FfmpegEncoder(file, 64, 48, 30))
                enc.Write(new byte[enc.FrameBytes]);
            Assert.False(File.Exists(file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Quadro_do_tamanho_errado_e_recusado()
    {
        if (FfmpegEncoder.Locate() is null) return;
        var dir = Directory.CreateTempSubdirectory("trafego-video").FullName;
        try
        {
            using var enc = new FfmpegEncoder(Path.Combine(dir, "x.mp4"), 64, 48, 30);
            Assert.Throws<ArgumentException>(() => enc.Write(new byte[10]));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
