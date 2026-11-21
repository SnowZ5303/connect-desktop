param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Repo
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$appProject = Join-Path $root 'src\ConnectDesktop.App'
$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts 'win-x64'
$issScript = Join-Path $root 'installer\ConnectDesktop.iss'
$setupExe = Join-Path $artifacts 'ConnectDesktop-Setup-win-x64.exe'

Write-Host "Publicando Connect Desktop $Version ($Repo) para win-x64..."
dotnet publish $appProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$Version -p:GitHubRepo=$Repo -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou (codigo $LASTEXITCODE)." }

$programFilesX86 = ${env:ProgramFiles(x86)}
$candidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "$programFilesX86\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)
$iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
}
if (-not $iscc) { throw 'ISCC.exe (Inno Setup 6) nao encontrado. Instale o Inno Setup 6.' }

$sourceAbs = (Resolve-Path $publishDir).Path
$artifactsAbs = (Resolve-Path $artifacts).Path

Write-Host "Gerando instalador com $iscc..."
& $iscc "/DMyAppVersion=$Version" "/DSourceDir=$sourceAbs" "/O$artifactsAbs" $issScript
if ($LASTEXITCODE -ne 0) { throw "ISCC falhou (codigo $LASTEXITCODE)." }

if (-not (Test-Path $setupExe)) { throw "Instalador nao gerado: $setupExe" }
Write-Host "Pronto: $setupExe"
