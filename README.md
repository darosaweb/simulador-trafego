<img src="assets/brand/logo-horizontal.svg" alt="daRosa Tráfego" width="480">

# daRosa Tráfego

Editor de redes viárias no estilo do PTV Vissim, sobre foto aérea, com o SUMO como motor de simulação. Projeto de aprendizado e pesquisa. Desktop Windows, C#/.NET 8 + Avalonia 11.

## Rodar

```
dotnet run --project src/Trafego.App
dotnet test
```

O app abre com um projeto vazio. O exemplo `samples/exemplo.trafego.json` (uma foto já calibrada pela
régua de 370,20 m) é aberto por Arquivo → Abrir.

**Como usar: veja o [Manual](docs/MANUAL.md)**, com um passo a passo do primeiro cenário (cruzamento com
semáforo) e todas as ferramentas.

Planejamento da distribuição pública (instalador, licenças): [docs/DISTRIBUICAO.md](docs/DISTRIBUICAO.md).
Marca (logo, cores, ícone): [docs/MARCA.md](docs/MARCA.md).

## Modelo de rede

- **Link**: trecho com N faixas no mesmo sentido. A geometria é o eixo central (polilinha ou curva
  suave). A faixa 0 é a da direita no sentido do tráfego (mesma convenção do SUMO e do Vissim).
- **Conector**: liga um grupo de faixas contíguas de um link a um grupo de mesmo tamanho em outro,
  saindo e chegando em qualquer posição ao longo deles. É assim que se fazem conversões, alças e rotatórias.
- Coordenadas do mundo em metros, com Y para baixo (como na imagem). A foto é posicionada por
  `origin + pixel × metersPerPixel`. Recalibrar escala a rede junto, para continuar alinhada.

## Editor

| Ferramenta | Uso |
|---|---|
| Selecionar (V) | Clique seleciona; arraste pontos ou o link inteiro; duplo clique insere ponto; botão direito apaga ponto. Nas pontas do conector, arrastar move a saída/chegada ao longo do link. |
| Link (L) | Clique marca o eixo no sentido do tráfego; duplo clique ou Enter termina; botão direito desfaz o último ponto. |
| Conector (C) | Clique na faixa de origem e depois na de destino. Perto da ponta do link, gruda nela. |
| Entrada (E) | Clique num link para criar uma entrada de veículos; volume (veíc/h) e % de pesados no painel. |
| Calibrar (K) | Clique nas duas pontas de uma distância conhecida e informe os metros. |

Roda do mouse: zoom. Botão do meio ou Espaço + arrastar: mover a vista. F: ajustar à tela.
Esc cancela, Delete exclui, Ctrl+Z / Ctrl+Y desfaz e refaz, Ctrl+S salva.

## Simulação

**▶ Simular** exporta a rede para o SUMO, roda o `netconvert` e simula dentro do próprio app pelo
libsumo, animando os veículos sobre a foto (cor = velocidade). Velocidade 1× a 10× ou máxima; duração em minutos.
Durante a simulação a rede fica só para leitura; **■ Parar** volta à edição.

Os arquivos do SUMO ficam na pasta `<projeto>.sumo/` ao lado do projeto salvo (ou em `%TEMP%\trafego`):
`rede.nod/edg/con.xml` (entrada do netconvert), `rede.net.xml`, `rotas.rou.xml` e `cenario.sumocfg`, que abre
no `sumo-gui` ou no `netedit` para comparar.

Tradução para o SUMO: cada link é cortado onde conectores saem ou chegam (cada pedaço é uma edge).
Conectores que se cruzam formam um **nó** (interseção): os pontos de saída e chegada deles viram uma junction
do SUMO e, dentro dela, cada conector vira uma conexão faixa a faixa com a forma desenhada. Os demais conectores
viram edges próprias; divergências e convergências viram nós simples. Sem rotas definidas, em cada escolha
(seguir no link ou pegar um conector) as opções têm a mesma chance. Chegadas de Poisson.

## Conflitos e nós

O editor encontra os conflitos pela geometria e os mostra como no Vissim: **verde** tem preferência,
**vermelho** espera uma brecha, **amarelo** o SUMO decide (links acima de conectores, depois a geometria),
**cinza** sem conflito. Clique no marcador do conflito para escolher:

- *Automático*, *A tem preferência* ou *B tem preferência* (vira `<prohibition>` no SUMO);
- *Sem conflito (níveis diferentes)* para viadutos e passagens inferiores: os conectores não formam nó.

Tipos de conflito: cruzamento de conectores, convergência (conector entrando no fluxo principal de um link,
ou dois conectores chegando nas mesmas faixas) e **link cruzando link/conector no meio do caminho**. Este último
ainda não vira interseção no SUMO: fica em laranja com um aviso. Se for no mesmo nível, termine os links antes do
cruzamento e ligue os movimentos por conectores (o jeito usual no Vissim); se for viaduto, marque níveis diferentes.

Clique no rótulo "Nó N" para escolher o controle do nó: via preferencial (padrão), preferência de quem vem
pela direita (CTB, art. 29) ou parada obrigatória em todas. O nó é identificado pelo menor id de conector dentro dele.

Requer o Eclipse SUMO com `SUMO_HOME` definido **na hora de compilar** (os bindings C# do libsumo vêm de
`SUMO_HOME\include` e a DLL nativa de `SUMO_HOME\bin`).

## Semáforos

**Rede → Semáforos…** abre o editor de controladores (tempo fixo):

- **Grupos semafóricos**: movimentos que abrem juntos. *Verde permitido* anda no verde mas cede a passagem a
  quem cruza (conversão à esquerda sem fase exclusiva).
- **Planos**: ciclo, defasagem e, por grupo, início e fim do verde e amarelo (s dentro do ciclo), com diagrama
  de tempos. Se o fim do verde é menor que o início, o verde atravessa o fim do ciclo.
- **Tabela de horários**: a partir de cada horário vale um plano, todos os dias (vira um WAUT no SUMO).

A **cabeça semafórica** é o conector ligado a um grupo: selecione o conector e escolha controlador e grupo no
painel. O semáforo fica no ponto onde o conector começa (no nó, se ele é interno a um). Num nó com semáforo,
movimentos sem cabeça passam sem cor, pelas regras de preferência (ex.: conversão livre à direita).
A verificação avisa quando dois grupos que se cruzam ficam verdes (ou amarelos) ao mesmo tempo.

A simulação começa no horário do campo **Início** (o relógio aparece no painel) — é ele que decide o plano em
vigor. Na animação, as cabeças mudam de cor com o semáforo.

No SUMO: cada controlador vira um `tlLogic` por plano em `rede.tll.xml`, com um caractere de estado por grupo
(todas as conexões do grupo compartilham o mesmo `linkIndex`); a troca por horário vai em `semaforos.add.xml`.

## Demanda

**Rede → Demanda…** abre o editor de demanda:

- **Intervalos de tempo** (ex.: 07:00, 07:15, 07:30…): cada um vai até o início do seguinte, todos os dias.
  A simulação usa o intervalo em vigor no relógio.
- **Volume por entrada**, em cada intervalo. Vale para entradas sem matriz: em cada escolha (seguir no link
  ou pegar um conector), as opções têm a mesma chance.
- **Matriz O/D** por intervalo: origens são as entradas; destinos são as **saídas** da rede (links que terminam
  sem conector, marcados com um quadrado rosa no mapa). Uma entrada com algum valor na matriz manda cada
  veículo para o seu destino.
- **Rotas**: para cada par O/D, as até 3 rotas mais rápidas em fluxo livre, com a participação de cada uma
  (sem participação definida, todos vão pela mais rápida). "Ver" destaca a rota no mapa. A rota é guardada
  pelos conectores que usa, então continua valendo se a geometria dos links mudar.

No SUMO, cada entrada (ou par O/D) vira um `<flow>` por janela de tempo, com chegadas de Poisson, e as rotas
viram uma `routeDistribution`.

## Resultados e comparação de cenários

**Medição (M)** coloca medições no mapa (escolha o tipo ao lado da ferramenta):

| Tipo | Onde | Mede | No SUMO |
|---|---|---|---|
| Contagem | um ponto do link | volume (veíc/h), velocidade | laço indutivo (E1) por faixa |
| Fila | linha de retenção | fila média e máxima (m) na faixa mais carregada, paradas | detector de área (E2) a montante, dentro do trecho do link |
| Tempo de viagem | início e fim | tempo de viagem e atraso de quem passa pelos dois pontos | detector de entrada/saída (E3) |

Arraste as barras para mover. Os resultados vêm por intervalo (padrão 15 min) e no período todo.

**Avaliar** roda o período N vezes (replicações, sementes diferentes) em processos `sumo` paralelos, sem animação,
e salva o resultado em `<projeto>.resultados/`. A simulação animada também gera resultados quando você clica em
Parar (uma replicação, marcada como parcial se foi interrompida).

**Resultados…** mostra:
- **Rede**: veículos atendidos, sem conseguir entrar e ainda na rede, tempo de viagem, atraso, tempo parado,
  paradas, velocidade média, atraso total, teleportes e colisões — média, desvio-padrão e cada semente;
- **Medições**: tabela por intervalo de cada medição;
- **Links**: volume, velocidade, atraso e densidade de cada link, e o botão que **colore o mapa pela velocidade**;
- **Comparar**: dois ou mais resultados lado a lado, com a diferença (Δ e %) em relação ao primeiro, em verde
  quando melhora e vermelho quando piora. Medições com o mesmo nome se comparam entre si. "Abrir de outro
  projeto…" traz resultados de outro arquivo — é assim que se compara, por exemplo, rotatória × DDI.
- **Exportar CSV** (ponto e vírgula, vírgula decimal, abre direto no Excel).

## Estrutura

- `src/Trafego.Core`: modelo, geometria, validação e arquivo de projeto (sem interface, testável)
- `src/Trafego.Sumo`: exportação para o SUMO, netconvert e sessão libsumo
- `src/Trafego.App`: interface Avalonia (`Editor/NetworkCanvas.cs` é a área de desenho)
- `tests/Trafego.Core.Tests`: testes xUnit (os de integração rodam o SUMO de verdade quando ele está instalado)

## Fases

1. **Base** (feita): foto e calibração, links multifaixa, conectores, salvar/abrir, desfazer.
2. **SUMO mínimo** (feita): exportar, `netconvert`, entradas de veículos, simular e animar via libsumo.
3. **Prioridade e conflitos** (feita): nós automáticos, áreas de conflito, preferências, controle do nó.
4. **Semáforos** (feita): controladores de tempo fixo, grupos, planos, tabela de horários, cores na simulação.
5. **Demanda** (feita): intervalos de tempo, volume por entrada e intervalo, matriz O/D, rotas com participação.
6. **Resultados e comparação** (feita): medições, indicadores da rede, links, replicações, comparação entre cenários.

Avalonia 12 exige o compilador do SDK .NET 9+/10; com o SDK 8 instalado, fica na 11.3.
