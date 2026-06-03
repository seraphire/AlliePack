param(
    [string]$Configuration = 'Release',
    [switch]$NoTests
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding           = [System.Text.Encoding]::UTF8
chcp 65001 | Out-Null

$root = Split-Path $PSScriptRoot -Parent
$sln  = Join-Path $root 'src\AlliePack.sln'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host 'dotnet not found on PATH. Install the .NET SDK and try again.'
    exit 1
}

Write-Host "Building AlliePack ($Configuration)..."
dotnet build $sln -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed (exit $LASTEXITCODE)"
    exit $LASTEXITCODE
}

if (-not $NoTests) {
    Write-Host ""
    Write-Host "Running tests..."
    dotnet test $sln -c $Configuration --nologo --no-build
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Tests failed (exit $LASTEXITCODE)"
        exit $LASTEXITCODE
    }
}

$exe = Join-Path $root "src\AlliePack\bin\$Configuration\net481\AlliePack.exe"
Write-Host ""
Write-Host "Done."
Write-Host "  Output: $exe"
