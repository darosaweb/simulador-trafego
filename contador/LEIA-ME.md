# Contador de Vias

Aplicativo de celular para **contar veículos em campo**: você escolhe a via, toca em Iniciar e, durante 1, 2
ou 5 minutos, toca em **Carro** ou **Caminhão** a cada veículo que passa. No fim, ele estima o fluxo em
veículos por hora e o % de pesados. O CSV que ele gera entra direto na demanda do **daRosa Tráfego**.

É um PWA: uma página web que se instala no celular como um aplicativo e funciona sem internet.

## Usar

1. Abra o endereço do contador no celular (veja [Publicar](#publicar)).
2. Instale o app. No Android (Chrome), use **Instalar app** ou **Adicionar à tela inicial**. No iPhone (Safari),
   use **Compartilhar → Adicionar à Tela de Início**.
3. Em **Resultados → Vias**, cadastre as vias, uma por sentido de tráfego. **Use os mesmos nomes das entradas do
   projeto** no simulador (ex.: "Entrada oeste").
4. Em **Contar**, escolha a via e o tempo, toque em **Iniciar** e conte. Ao terminar, o contador já sugere a
   próxima via.
5. Em **Resultados**, toque em **Baixar CSV**. No celular abre o menu de compartilhar (mande por e-mail,
   WhatsApp, Drive…); no computador, o arquivo é baixado.
6. No daRosa Tráfego: **Rede → Demanda… → Importar contagens…** e escolha o CSV.

O que a importação faz:

- **Nomes iguais:** cada via com o mesmo nome de uma entrada (sem diferenciar maiúsculas e acentos) vira o
  volume dessa entrada.
- **Agrupamento:** as contagens são agrupadas pelo intervalo da demanda em que foram feitas. Se o projeto
  só tem o intervalo padrão, é criado um intervalo por hora contada. Várias contagens no mesmo intervalo são
  somadas: total de veículos sobre o tempo total.
- **Pesados:** o % de pesados da entrada passa a ser o de todas as contagens dela.
- **Aviso:** as vias sem entrada correspondente são listadas na tela.

Os dados ficam **só no aparelho** (armazenamento do navegador). Baixe o CSV ao fim de cada dia de contagem:
desinstalar o app ou limpar os dados do navegador apaga as contagens.

## Publicar

Como o repositório é público, o GitHub Pages publica o contador de graça:

1. No GitHub, abra **Settings → Pages**.
2. Em **Build and deployment**, escolha **Deploy from a branch**, branch `main`, pasta `/ (root)`, e salve.
3. Em um ou dois minutos o contador fica em `https://darosaweb.github.io/simulador-trafego/contador/`.

Qualquer servidor de arquivos estáticos com **HTTPS** serve (o service worker e a instalação exigem HTTPS;
`localhost` também vale para testar). Para testar no computador:

```
python -m http.server 8765
```

Rode o comando na raiz do repositório e abra `http://localhost:8765/contador/`.

## Arquivos

| Arquivo | Para quê |
|---|---|
| `index.html` | O aplicativo inteiro: HTML, CSS e JavaScript num arquivo só, sem dependências nem build |
| `manifest.webmanifest` | Nome, ícones e cores para instalar no celular |
| `sw.js` | Service worker: guarda os arquivos no aparelho para abrir offline |
| `icones/` | Rosa daRosa em 192 e 512 px, versão "maskable" (com margem para o recorte do Android) e ícone do iPhone |
| `fontes/` | Barlow e Barlow Condensed (licença SIL OFL, em `fontes/OFL.txt`), só o alfabeto latino |

**Ao mudar qualquer arquivo, aumente `VERSAO` em `sw.js`** (ex.: `contador-v2`). Senão, os celulares
que já instalaram continuam com os arquivos antigos guardados. A página em si sempre busca a versão nova
quando há internet.

## Formato do CSV

Ponto e vírgula, com BOM UTF-8 para o Excel em português abrir com os acentos certos. Uma linha por contagem:

```
data;hora_inicio;via;duracao_s;carros;caminhoes;total;veic_h;pct_pesados
31/10/2026;07:10;"Entrada oeste";60;18;2;20;1200;10
```

O simulador usa `data`, `hora_inicio`, `via`, `duracao_s`, `carros` e `caminhoes`; as outras colunas são para
quem abre no Excel. A leitura fica em `src/Trafego.Core/Model/CountImport.cs`, com testes em
`tests/Trafego.Core.Tests/CountImportTests.cs`. Se mudar o formato, mude os dois lados.

## Para outra IA (ou pessoa) adaptar ou refazer

O `index.html` é a referência: leia-o inteiro antes de mudar. As decisões que fazem o contador funcionar
bem em campo, e que valem preservar:

**Uso com uma mão, sem olhar**

- Dois botões enormes ocupam a metade de baixo da tela: Carro (azul) e Caminhão (amarelo). As cores são
  funcionais e não seguem a marca, para não confundir as duas classes.
- O toque conta no `pointerdown`, sem esperar o dedo levantar. Cada toque vibra (`navigator.vibrate`) e o
  botão pisca.
- **Desfazer último** corrige toque duplo.
- No computador, as teclas C e T, ou as setas ← e →, também contam.

**Contagem por amostra cronometrada**

- A duração é fixa (1, 2 ou 5 min), com bipe e vibração no início e no fim. O cronômetro usa
  `performance.now()`, não o relógio do sistema.
- A tela não apaga durante a contagem (`navigator.wakeLock`), e o bloqueio é pedido de novo quando o app
  volta ao primeiro plano.
- Encerrar antes do tempo salva o que foi contado. Com menos de 15 s, descarta.
- Cada contagem guarda os toques com o instante de cada um (`toques`), para análises futuras.

**Classes simples**

- Só duas classes: "Carro" inclui moto e van; "Caminhão" inclui ônibus. Isso bate com o modelo do simulador,
  que tem leves e pesados.

**Resultados sem conta de cabeça**

- **Fluxo:** veíc/h = veículos × 3600 / segundos. Várias amostras da mesma via na mesma hora são somadas
  antes de dividir. Isso dá peso pelo tempo, sem tirar média de médias.
- **Copiar:** "Copiar resumo" gera um texto curto e "Copiar CSV" copia a tabela. Os dois têm uma alternativa
  para quando a área de transferência é bloqueada: o texto fica selecionado na tela para copiar à mão.

**Dados e interface**

- **Armazenamento:** `localStorage` com chaves `cv.vias`, `cv.sessoes` e `cv.pref`, e
  `navigator.storage.persist()` para o navegador não apagar.
- **Rodando como artefato do Claude:** quando existe `window.claude`, a página usa o banco compartilhado (`db`) e o
  download do Claude. Então o mesmo arquivo funciona como artefato e como PWA.
- **Remoção:** duas etapas ("Remover" → "Confirmar?"), sem caixas de diálogo.
- **Aparência:** tema claro e escuro, que segue o sistema, e respeita as áreas seguras do celular (`env(safe-area-inset-*)`).
- **Marca:** neutros "papel" (#FFF7F6) e "tinta" (#231F20) e destaque framboesa (#CC3467), ou rosa
  (#FF8CA1) no escuro. Veja `docs/MARCA.md`.

Ideias que ficaram de fora e caberiam aqui:

- mais classes (moto, ônibus) com a importação somando em leves e pesados;
- contagem por movimento (esquerda, frente, direita) para alimentar o percentual dos conectores;
- exportar e importar um backup completo (JSON) para trocar de aparelho.
