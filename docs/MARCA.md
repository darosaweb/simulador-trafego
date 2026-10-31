# Marca — daRosa Tráfego

O produto se chama **daRosa Tráfego**: a marca daRosa na frente e o nome do produto depois. O símbolo é a
**rosa daRosa**, com as pétalas recortadas, o contorno escuro e a faixa de bolinhas.

![Logo para fundo claro](../assets/brand/logo-horizontal.svg)

![Logo para fundo escuro](../assets/brand/logo-horizontal-escuro.svg)

## Nome

- Escreva sempre **daRosa Tráfego**: "da" minúsculo e grudado, "R" maiúsculo, com o acento em "Tráfego".
- Em texto corrido, depois da primeira menção, pode ser só "o Tráfego".
- Frase de apoio: *Simulação de tráfego para projetos viários.*

## Cores

| Cor | Hex | Onde usar |
|---|---|---|
| Framboesa | `#CC3467` | cor principal: destaque da interface (botões principais, ferramenta ativa), "Tráfego" no logo em fundo claro |
| Rosa | `#FF8CA1` | pétalas de cima e faixa; "Tráfego" no logo em fundo escuro |
| Rosa claro | `#FECCCB` | pétalas; fundos suaves |
| Tinta | `#231F20` | contorno da rosa, "daRosa" no logo em fundo claro, texto |
| Papel | `#FFF7F6` | fundo das telas de marca (abertura, Sobre) |

No mapa, as cores continuam funcionais e **não** são da marca: azul na seleção, magenta nos avisos em
destaque, amarelo na comparação de cenários, verde e vermelho nos conflitos e semáforos. Assim o rosa nunca
parece um aviso.

## Tipografia

**Inter** (SIL Open Font License), embutida no programa:

- "daRosa" em Regular (400), "Tráfego" em ExtraBold (800);
- interface e textos em Regular, títulos em SemiBold.

Nos SVGs do logo, o texto usa Inter quando ela está instalada. Para material impresso, converta o texto em
curvas ou instale a Inter.

## Rosa e ícone

A rosa é a **original** da daRosa (`G:\Meu Drive\ArteDaRosa\Marca\favIcon\ms-icon-310x310.png`, 310 px).
Não existe vetor dela. Os arquivos abaixo são o original sem o fundo branco e sem a moldura cinza.

| Arquivo | Uso |
|---|---|
| `assets/brand/rosa-original.png` | o original como veio, com fundo branco (referência) |
| `assets/brand/rosa-original-transparente.png` | o original com fundo transparente, para fundo claro |
| `assets/brand/rosa-original-halo.png` | com um halo branco por fora, para fundo escuro ou foto |
| `assets/brand/icone.ico` | ícone do Windows (16 a 256 px, com halo), o mesmo de `src/Trafego.App/Assets/app.ico` |
| `assets/brand/icone-256.png`, `icone-32.png` | o ícone em PNG |
| `assets/brand/logo-horizontal.svg` / `-escuro.svg` | rosa + nome + frase |

Regras:

- Mantenha em volta da rosa uma área livre de pelo menos 1/4 da largura dela.
- Não gire, não distorça, não troque as cores das pétalas e não tire o contorno.
- Em fundo escuro ou foto, use a versão com halo.
- Como o original tem 310 px, não use a rosa maior que isso (cerca de 8 cm a 100 dpi). Para impressão ou
  telas grandes, será preciso vetorizar o original.

## No programa

- Ícone do executável, das janelas e da barra de tarefas: `app.ico`.
- Tela de abertura (1,5 s) e **Ajuda → Sobre**: fundo papel, faixa framboesa, rosa e logotipo.
- Títulos das janelas: "… — daRosa Tráfego".
- Destaque do tema (Fluent) em framboesa.

As cores ficam em `src/Trafego.App/App.axaml` (recursos `Framboesa`, `Rosa`, `RosaClaro`, `Tinta`) e em
`src/Trafego.App/Brand.cs`.
