# Gera docs/Manual-daRosa-Trafego.pdf a partir do manual (docs/MANUAL.md), com o mesmo visual da Ajuda.
# Precisa do app compilado (dotnet build) e do Microsoft Edge.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$exe = Join-Path $root 'src\Trafego.App\bin\Debug\net8.0\Trafego.exe'
if (-not (Test-Path $exe)) { throw "Compile antes: dotnet build (não achei $exe)" }
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge não encontrado.' }

$html = Join-Path $env:TEMP 'darosa-trafego-ajuda-pdf'
Start-Process -FilePath $exe -ArgumentList '--gerar-ajuda', "`"$html`"" -Wait   # é um app de janela: espera terminar
$index = Join-Path $html 'index.html'
if (-not (Test-Path $index)) { throw 'A Ajuda não foi gerada.' }

$pdf = Join-Path $root 'docs\Manual-daRosa-Trafego.pdf'
if (Test-Path $pdf) { Remove-Item $pdf }
$ErrorActionPreference = 'Continue'   # o Edge escreve avisos no stderr
& $edge --headless=new --disable-gpu --no-pdf-header-footer --run-all-compositor-stages-before-draw `
    --user-data-dir="$env:TEMP\darosa-edge-pdf" --print-to-pdf="$pdf" ('file:///' + $index.Replace('\', '/')) 2>$null | Out-Null
for ($i = 0; $i -lt 60 -and -not (Test-Path $pdf); $i++) { Start-Sleep -Milliseconds 500 }
if (-not (Test-Path $pdf)) { throw 'O Edge não gerou o PDF.' }
"PDF: $pdf ($([math]::Round((Get-Item $pdf).Length / 1MB, 1)) MB)"
