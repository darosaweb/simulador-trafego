# Distribuição pública (planejamento)

> Ainda não implementado. Este documento registra o que foi decidido/levantado para quando o
> app estiver pronto para ser distribuído. Não é parecer jurídico — para uso comercial, vale
> confirmar com um advogado.

## Modelo de distribuição

- O usuário final recebe um **instalador para Windows** (`Trafego-Setup.exe`), não o código.
- O instalador contém o app compilado em modo **self-contained** (`dotnet publish -r win-x64
  --self-contained`), então não exige o .NET instalado.
- O **código-fonte pode continuar privado** no GitHub (`darosaweb/simulador-trafego`).
- Hospedagem do instalador:
  - **GitHub Releases** de um repositório *público* só de releases (ex.:
    `simulador-trafego-releases`) — releases de repositório privado também são privadas; ou
  - site próprio / Google Drive / outro serviço.

## Licenças

| Componente | Licença | Como é usado | Obrigação |
|---|---|---|---|
| Código do Tráfego | a definir (hoje: nenhuma = todos os direitos reservados) | — | Adicionar `LICENSE` (ex.: proprietária, MIT ou GPL) |
| Avalonia | MIT | Biblioteca (NuGet) | Incluir o aviso de licença |
| SUMO | EPL-2.0 (secundária GPL-2.0+) | Programas externos (`sumo`, `netconvert`) + `libsumocs.dll` | Incluir a licença; se embutido, distribuir junto com o aviso |
| Bindings C# do libsumo | EPL-2.0 | Compilados dentro de `Trafego.Sumo.dll` | Incluir o aviso da EPL-2.0 e indicar onde está o fonte (repositório do SUMO). Não obriga a abrir o código do Tráfego |
| FFmpeg | LGPL/GPL (build com x264) | Processo externo (pipe) | Não afeta o código do Tráfego. Se for embutido no instalador: incluir a licença e oferecer o fonte do FFmpeg |

Arquivos a criar: `LICENSE` e `THIRD-PARTY-NOTICES.md` (copiado também para a pasta instalada).

## Marcas

Não usar "Vissim" nem "PTV" no nome ou na divulgação. Pode-se dizer "usa o SUMO como motor de
simulação", sem sugerir vínculo ou endosso do DLR/Eclipse.

## SUMO no instalador — opções (decidir depois)

1. **Exigir o SUMO instalado:** o instalador verifica `SUMO_HOME` e, se não achar, mostra o link
   de download. Instalador pequeno.
2. **Embutir o SUMO:** mais cômodo para o usuário; instalador passa de 100 MB. Levar a licença
   EPL-2.0 junto e apontar `SUMO_HOME` para a cópia embutida.

O FFmpeg é opcional (só para exportar vídeo): o instalador pode apenas avisar se não encontrá-lo.

## Pontos técnicos

- Fazer o build de release numa máquina **com o SUMO instalado**: sem `SUMO_HOME`, a constante
  `LIBSUMO` não é definida e a animação ao vivo fica de fora (ver `Trafego.Sumo.csproj`).
- A `libsumocs.dll` em uso precisa ser da **mesma versão** do SUMO cujos bindings foram compilados.
- Instalador sugerido: **Inno Setup** (gratuito), com atalho no menu Iniciar e associação opcional
  da extensão `.trafego.json`.
- **Assinatura de código** (opcional): sem ela, o Windows SmartScreen mostra "editor
  desconhecido" na primeira execução.

## Checklist para quando for distribuir

- [ ] Escolher a licença e criar `LICENSE`
- [ ] Criar `THIRD-PARTY-NOTICES.md` (Avalonia, SUMO/libsumo, FFmpeg)
- [ ] Script de build (`dotnet publish` self-contained win-x64)
- [ ] Decidir SUMO embutido × exigido
- [ ] Script do Inno Setup (detecção do SUMO e do FFmpeg)
- [ ] Repositório público de releases (ou outro local de download)
- [ ] (Opcional) Certificado de assinatura de código
