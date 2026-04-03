[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding           = [System.Text.Encoding]::UTF8
chcp 65001 | Out-Null

<#
.SYNOPSIS
    Build an AlliePack MSI and verify it installs and uninstalls correctly.

.DESCRIPTION
    Two test modes:

    -AdminInstall (default)
        Extracts the MSI contents to a staging folder using msiexec /a.
        Does not register the product with Add/Remove Programs.
        Verifies that the expected files were extracted to the correct paths.
        Suitable for CI or environments where a full install is not desired.

    -FullInstall
        Performs a real silent install, verifies files on disk and the
        product entry in Add/Remove Programs, then silently uninstalls.
        Requires the script to be run as Administrator.

.PARAMETER TestDir
    Path to the test directory containing allie-pack.yaml.
    Defaults to the base test: <repo>\test\base

.PARAMETER AlliePack
    Path to AlliePack.exe.
    Defaults to <repo>\src\AlliePack\bin\Release\net481\AlliePack.exe

.PARAMETER OutputDir
    Directory where the built MSI and logs are written.
    Defaults to <TestDir>\output

.PARAMETER FullInstall
    Run a real install/uninstall cycle instead of an admin (extraction) install.
    Requires elevation.

.PARAMETER KeepOutput
    Do not delete the output directory after a successful test.

.EXAMPLE
    # Admin install (default, no elevation required)
    .\Run-Test.ps1

.EXAMPLE
    # Admin install against a specific test config
    .\Run-Test.ps1 -TestDir .\solution

.EXAMPLE
    # Full install/uninstall cycle (run as Administrator)
    .\Run-Test.ps1 -FullInstall

.EXAMPLE
    # Full install with custom AlliePack path
    .\Run-Test.ps1 -FullInstall -AlliePack "C:\tools\AlliePack.exe"
#>

param(
    [string] $TestDir   = (Join-Path $PSScriptRoot "base"),
    [string] $AlliePack = (Join-Path $PSScriptRoot "..\src\AlliePack\bin\Release\net481\AlliePack.exe"),
    [string] $OutputDir = "",
    [switch] $FullInstall,
    [switch] $KeepOutput
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function Write-Step([string]$msg) {
    Write-Host ""
    Write-Host "==> $msg" -ForegroundColor Cyan
}

function Write-Pass([string]$msg) {
    Write-Host "    [PASS] $msg" -ForegroundColor Green
}

function Write-Fail([string]$msg) {
    Write-Host "    [FAIL] $msg" -ForegroundColor Red
    $script:failures++
}

function Assert-FileExists([string]$path, [string]$label) {
    if (Test-Path $path) {
        Write-Pass "$label exists: $path"
    } else {
        Write-Fail "$label not found: $path"
    }
}

function Assert-FileNotExists([string]$path, [string]$label) {
    if (-not (Test-Path $path)) {
        Write-Pass "$label removed: $path"
    } else {
        Write-Fail "$label still exists: $path"
    }
}

# ---------------------------------------------------------------------------
# Resolve paths
# ---------------------------------------------------------------------------

$TestDir    = Resolve-Path $TestDir
$AlliePack  = Resolve-Path $AlliePack
$ConfigFile = Join-Path $TestDir "allie-pack.yaml"

if ($OutputDir -eq "") {
    $OutputDir = Join-Path $TestDir "output"
}

if (-not (Test-Path $ConfigFile)) {
    Write-Error "Config not found: $ConfigFile"
    exit 1
}

if (-not (Test-Path $AlliePack)) {
    Write-Error "AlliePack.exe not found: $AlliePack"
    exit 1
}

$script:failures = 0

# ---------------------------------------------------------------------------
# Read product info from YAML (minimal parse for UpgradeCode and Name)
# ---------------------------------------------------------------------------

$yamlText    = Get-Content $ConfigFile -Raw
$productName = if ($yamlText -match 'name:\s+"?([^"\r\n]+)"?') { $Matches[1].Trim() } else { "Unknown" }
$upgradeCode = if ($yamlText -match 'upgradeCode:\s+"?([^"\r\n]+)"?') { $Matches[1].Trim() } else { "" }

Write-Host ""
Write-Host "AlliePack Test Runner" -ForegroundColor White
Write-Host "  Test dir   : $TestDir"
Write-Host "  Config     : $ConfigFile"
Write-Host "  Product    : $productName"
Write-Host "  Mode       : $(if ($FullInstall) { 'Full install/uninstall' } else { 'Admin install (extraction)' })"
Write-Host "  Output dir : $OutputDir"

# ---------------------------------------------------------------------------
# Step 1: Build MSI
# ---------------------------------------------------------------------------

Write-Step "Building MSI"

if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDir | Out-Null

$msiPath  = Join-Path $OutputDir "$productName.msi"
$buildLog = Join-Path $OutputDir "build.log"

$buildArgs = @($ConfigFile, "--output", $msiPath, "--verbose")
& $AlliePack @buildArgs 2>&1 | Tee-Object -FilePath $buildLog

if ($LASTEXITCODE -ne 0) {
    Write-Fail "AlliePack exited with code $LASTEXITCODE -- see $buildLog"
    exit 1
}

Assert-FileExists $msiPath "MSI output"

if ($script:failures -gt 0) { exit 1 }

# ---------------------------------------------------------------------------
# Step 2: Install
# ---------------------------------------------------------------------------

if ($FullInstall) {

    Write-Step "Installing MSI (silent)"

    $installLog = Join-Path $OutputDir "install.log"
    $result = Start-Process msiexec -ArgumentList "/i `"$msiPath`" /qn /norestart /l*v `"$installLog`"" `
              -Wait -PassThru -Verb RunAs
    if ($result.ExitCode -ne 0) {
        Write-Fail "msiexec /i exited with code $($result.ExitCode) -- see $installLog"
        exit 1
    }
    Write-Pass "Silent install completed (exit 0)"

} else {

    Write-Step "Extracting MSI (admin install -- no system changes)"

    $stagingDir  = Join-Path $OutputDir "staging"
    New-Item -ItemType Directory -Path $stagingDir | Out-Null
    $extractLog = Join-Path $OutputDir "extract.log"

    $result = Start-Process msiexec `
              -ArgumentList "/a `"$msiPath`" TARGETDIR=`"$stagingDir`" /qn /l*v `"$extractLog`"" `
              -Wait -PassThru
    if ($result.ExitCode -ne 0) {
        Write-Fail "msiexec /a exited with code $($result.ExitCode) -- see $extractLog"
        exit 1
    }
    Write-Pass "Admin install (extraction) completed (exit 0)"

}

# ---------------------------------------------------------------------------
# Step 3: Verify installed files
# ---------------------------------------------------------------------------

Write-Step "Verifying installed files"

if ($FullInstall) {

    # Resolve actual install path from registry
    $uninstallKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*"
    $wow64Key     = "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*"

    $entry = Get-ItemProperty $uninstallKey, $wow64Key -ErrorAction SilentlyContinue |
             Where-Object { $_.DisplayName -eq $productName } |
             Select-Object -First 1

    if ($null -eq $entry) {
        Write-Fail "Product '$productName' not found in Add/Remove Programs"
    } else {
        Write-Pass "Product found in Add/Remove Programs: $($entry.DisplayName) $($entry.DisplayVersion)"
        $installLocation = $entry.InstallLocation
    }

    # Expected files (customize per test)
    Assert-FileExists (Join-Path $installLocation "App\README.txt") "README.txt"

} else {

    # Verify extracted staging tree
    $appDir = Join-Path $stagingDir "App"

    # Expected files (customize per test)
    Assert-FileExists (Join-Path $appDir "README.txt") "README.txt"

    # Generic check: staging directory is non-empty
    $extracted = Get-ChildItem $stagingDir -Recurse -File
    if ($extracted.Count -gt 0) {
        Write-Pass "Staging directory contains $($extracted.Count) file(s)"
        $extracted | ForEach-Object { Write-Host "      $($_.FullName)" }
    } else {
        Write-Fail "Staging directory is empty -- no files were extracted"
    }

}

# ---------------------------------------------------------------------------
# Step 4: Uninstall (full install mode only)
# ---------------------------------------------------------------------------

if ($FullInstall) {

    Write-Step "Uninstalling MSI (silent)"

    $uninstallLog = Join-Path $OutputDir "uninstall.log"

    if ($upgradeCode -ne "") {
        $result = Start-Process msiexec `
                  -ArgumentList "/x `"$msiPath`" /qn /norestart /l*v `"$uninstallLog`"" `
                  -Wait -PassThru -Verb RunAs
    } else {
        Write-Fail "No upgradeCode found in config -- cannot uninstall by product code"
        $result = [PSCustomObject]@{ ExitCode = 1 }
    }

    if ($result.ExitCode -ne 0) {
        Write-Fail "msiexec /x exited with code $($result.ExitCode) -- see $uninstallLog"
    } else {
        Write-Pass "Silent uninstall completed (exit 0)"
    }

    Write-Step "Verifying removal"

    $entry = Get-ItemProperty $uninstallKey, $wow64Key -ErrorAction SilentlyContinue |
             Where-Object { $_.DisplayName -eq $productName } |
             Select-Object -First 1

    if ($null -eq $entry) {
        Write-Pass "Product removed from Add/Remove Programs"
    } else {
        Write-Fail "Product still present in Add/Remove Programs after uninstall"
    }

    # Expected removals (customize per test)
    Assert-FileNotExists (Join-Path $installLocation "App\README.txt") "README.txt"

}

# ---------------------------------------------------------------------------
# Cleanup and result
# ---------------------------------------------------------------------------

if (-not $KeepOutput -and $script:failures -eq 0) {
    Remove-Item $OutputDir -Recurse -Force
}

Write-Host ""
if ($script:failures -eq 0) {
    Write-Host "All checks passed." -ForegroundColor Green
    exit 0
} else {
    Write-Host "$($script:failures) check(s) failed. Output preserved at: $OutputDir" -ForegroundColor Red
    exit 1
}
