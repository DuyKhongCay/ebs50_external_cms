# ==============================================================================
# Script: Publish-App.ps1
# Description: Publishes ebs50_backend as a standalone self-contained win-x64 package.
# Includes .NET 8 runtime, SkiaSharp native binaries, SQLite native engine,
# templates, and web static assets.
# ==============================================================================

[CmdletBinding()]
param (
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = ""
)

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptDir
$csprojPath = Join-Path $projectRoot "ebs50_backend.csproj"

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $projectRoot "publish\win-x64"
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Publishing ebs50_backend (Self-Contained $Runtime)" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "[*] Project: $csprojPath" -ForegroundColor White
Write-Host "[*] Output:  $OutputDir" -ForegroundColor White

if (Test-Path $OutputDir) {
    Write-Host "[*] Cleaning old publish artifacts..." -ForegroundColor Yellow
    Remove-Item -Path "$OutputDir\*" -Recurse -Force -ErrorAction SilentlyContinue
}

# Run dotnet publish
Write-Host "[*] Executing dotnet publish..." -ForegroundColor Yellow
$publishArgs = @(
    "publish",
    "`"$csprojPath`"",
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-o", "`"$OutputDir`""
)

& dotnet @publishArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE."
    exit $LASTEXITCODE
}

# Ensure Assets folder exists in publish directory
$assetsSource = Join-Path $projectRoot "Assets"
$assetsDest = Join-Path $OutputDir "Assets"
if ((Test-Path $assetsSource) -and -not (Test-Path $assetsDest)) {
    Write-Host "[+] Copying Assets folder to publish directory..." -ForegroundColor Green
    Copy-Item -Path $assetsSource -Destination $assetsDest -Recurse -Force
}

# Ensure wwwroot folder exists in publish directory
$wwwrootSource = Join-Path $projectRoot "wwwroot"
$wwwrootDest = Join-Path $OutputDir "wwwroot"
if ((Test-Path $wwwrootSource) -and -not (Test-Path $wwwrootDest)) {
    Write-Host "[+] Copying wwwroot folder to publish directory..." -ForegroundColor Green
    Copy-Item -Path $wwwrootSource -Destination $wwwrootDest -Recurse -Force
}

# Copy default database template if available
$dbSource = Join-Path $projectRoot "etag_database.db"
$dbDest = Join-Path $OutputDir "etag_database.db"
if ((Test-Path $dbSource) -and -not (Test-Path $dbDest)) {
    Write-Host "[+] Seeding initial database to publish directory..." -ForegroundColor Green
    Copy-Item -Path $dbSource -Destination $dbDest -Force
}

# Copy application icon
$icoSource = Join-Path $projectRoot "demonstration.ico"
$icoDest = Join-Path $OutputDir "demonstration.ico"
if (Test-Path $icoSource) {
    Copy-Item -Path $icoSource -Destination $icoDest -Force
}

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "  PUBLISH COMPLETED SUCCESSFULLY" -ForegroundColor Green
Write-Host "  Binary output ready at: $OutputDir" -ForegroundColor White
Write-Host "==========================================================" -ForegroundColor Green

