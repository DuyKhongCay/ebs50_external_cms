# ==============================================================================
# Script: Build-Installer.ps1
# Description: Automates publishing self-contained win-x64 binaries and compiling
#              a Single Installer EXE using Inno Setup (ISCC.exe).
# ==============================================================================

[CmdletBinding()]
param (
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptDir
$publishScript = Join-Path $scriptDir "Publish-App.ps1"
$issScript = Join-Path $projectRoot "installer\ebs50_setup.iss"
$distDir = Join-Path $projectRoot "dist"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Building EBS-50 Single Installer EXE Package" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# Step 1: Run self-contained publish
Write-Host "`n>>> Step 1: Publishing self-contained $Runtime application..." -ForegroundColor Yellow
& $publishScript -Configuration $Configuration -Runtime $Runtime

if ($LASTEXITCODE -ne 0) {
    Write-Error "Publish step failed. Aborting installer compilation."
    exit $LASTEXITCODE
}

# Step 2: Locate Inno Setup Compiler (iscc.exe)
Write-Host "`n>>> Step 2: Locating Inno Setup Compiler (iscc.exe)..." -ForegroundColor Yellow

$isccPath = $null
$candidateIsccPaths = @(
    "iscc",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
)

foreach ($candidate in $candidateIsccPaths) {
    $cmd = Get-Command $candidate -ErrorAction SilentlyContinue
    if ($cmd) {
        $isccPath = $cmd.Source
        break
    }
    if (Test-Path $candidate) {
        $isccPath = (Resolve-Path $candidate).Path
        break
    }
}

if (-not $isccPath) {
    Write-Warning "Inno Setup Compiler (ISCC.exe) was not found on this machine."
    Write-Host ""
    Write-Host "To compile the Single Installer EXE, please install Inno Setup 6:" -ForegroundColor Cyan
    Write-Host "  Option 1 (via winget):  winget install JRSoftware.InnoSetup" -ForegroundColor White
    Write-Host "  Option 2 (via website): https://jrsoftware.org/isdl.php" -ForegroundColor White
    Write-Host ""
    Write-Host "After installing Inno Setup, run this script again or open:" -ForegroundColor White
    Write-Host "  $issScript" -ForegroundColor Cyan
    Write-Host "in Inno Setup Compiler IDE and press F9 (Compile)." -ForegroundColor White
    Write-Host ""
    Write-Host "[*] Published self-contained binaries are ready in: $projectRoot\publish\win-x64" -ForegroundColor Green
    exit 0
}

Write-Host "[+] Found Inno Setup Compiler at: $isccPath" -ForegroundColor Green

# Step 3: Ensure dist output folder exists
if (-not (Test-Path $distDir)) {
    New-Item -ItemType Directory -Path $distDir -Force | Out-Null
}

# Step 4: Compile Inno Setup Script
Write-Host "`n>>> Step 3: Compiling Installer with Inno Setup..." -ForegroundColor Yellow
Write-Host "[*] Script: $issScript" -ForegroundColor White

& $isccPath "`"$issScript`""

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "==========================================================" -ForegroundColor Green
    Write-Host "  SINGLE INSTALLER EXE CREATED SUCCESSFULLY!" -ForegroundColor Green
    Write-Host "  Output Directory: $distDir" -ForegroundColor White
    Get-ChildItem -Path $distDir -Filter "*.exe" | ForEach-Object {
        $sizeMB = [math]::Round($_.Length / 1MB, 2)
        Write-Host "  File: $($_.Name) ($sizeMB MB)" -ForegroundColor Cyan
    }
    Write-Host "==========================================================" -ForegroundColor Green
} else {
    Write-Error "Inno Setup compilation failed with exit code $LASTEXITCODE."
    exit $LASTEXITCODE
}

