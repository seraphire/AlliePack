param(
    [string]$OutputPath = '',
    [switch]$NoBuild
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding           = [System.Text.Encoding]::UTF8
chcp 65001 | Out-Null

$root   = Split-Path $PSScriptRoot -Parent
$exe    = Join-Path $root 'src\AlliePack\bin\Release\net481\AlliePack.exe'
$config = Join-Path $root 'docs\allie-pack.yaml'

if (-not $NoBuild) {
    & (Join-Path $PSScriptRoot 'Build-AlliePack.ps1') -NoTests
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host ""
}

if ($NoBuild -and -not (Test-Path $exe)) {
    Write-Host "AlliePack.exe not found at: $exe"
    Write-Host "Run without -NoBuild to compile first."
    exit 1
}

if (-not $OutputPath) {
    $OutputPath = Join-Path $root 'dist'
}

if (-not (Test-Path $OutputPath)) {
    New-Item -ItemType Directory -Path $OutputPath | Out-Null
}

$msiPath = Join-Path $OutputPath 'AlliePack.msi'

Write-Host "Building AlliePack installer..."
Write-Host "  Config : $config"
Write-Host "  Output : $msiPath"
Write-Host ""

& $exe $config --output $msiPath
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "Installer build failed (exit $LASTEXITCODE)"
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Done: $msiPath"
