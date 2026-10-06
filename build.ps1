<#
  Gera a versão Release do G-HOSP Panel Watchdog.

  Saída em .\dist\:
    G-HOSP Panel Watchdog.exe            executável único, self-contained (não exige .NET instalado)
    G-HOSP Panel Watchdog Setup.exe      instalador por usuário (se o Inno Setup 6 estiver instalado)

  Uso:  powershell -ExecutionPolicy Bypass -File .\build.ps1
#>
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'src\GHospPanelWatchdog\GHospPanelWatchdog.csproj'
$dist = Join-Path $root 'dist'
$publish = Join-Path $dist 'app'

$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force $publish | Out-Null

Write-Host '==> Publicando (Release, win-x64, self-contained, arquivo único)...'
& $dotnet publish $proj -c Release -r win-x64 --self-contained true -o $publish `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=false `
    -p:DebugType=none -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Falha no dotnet publish' }

Copy-Item (Join-Path $publish 'G-HOSP Panel Watchdog.exe') $dist
Copy-Item (Join-Path $root 'README.md') $publish
New-Item -ItemType Directory -Force (Join-Path $publish 'docs') | Out-Null
Copy-Item (Join-Path $root 'docs\*.md') (Join-Path $publish 'docs')

$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($iscc) {
    Write-Host '==> Gerando instalador (Inno Setup)...'
    & $iscc /Q "/O$dist" (Join-Path $root 'installer\GHospPanelWatchdog.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Falha no Inno Setup' }
} else {
    Write-Warning 'Inno Setup 6 não encontrado: instalador não gerado (o .exe em dist\ funciona sozinho).'
}

Write-Host ''
Get-ChildItem $dist -File | ForEach-Object { '{0,-40} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
