# ==============================================================================
# Script: Publish-App.ps1
# Description: Publishes ebs50_backend as a standalone self-contained win-x64 package.
# Includes .NET 8 runtime, SkiaSharp native binaries, SQLite native engine,
# templates, and web static assets.
# ==============================================================================

<#
.SYNOPSIS
Cleans the dedicated publish directory and publishes current application source.
.PARAMETER Configuration
Release (default) or Debug.
.PARAMETER Runtime
Target runtime; this installer supports win-x64 only.
.PARAMETER OutputDir
Optional explicit path to this project's publish\win-x64 directory.
Other destinations and paths containing junctions or symbolic links are rejected.
.EXAMPLE
.\scripts\Publish-App.ps1 -WhatIf
.EXAMPLE
.\scripts\Publish-App.ps1 -Configuration Release
#>
[CmdletBinding(SupportsShouldProcess)]
param (
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = "Release",
    [ValidateSet('win-x64')]
    [string]$Runtime = "win-x64",
    [string]$OutputDir = ""
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptDir
$csprojPath = Join-Path $projectRoot "ebs50_backend.csproj"

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $projectRoot "publish\win-x64"
}

# Restrict destructive cleanup to the dedicated publish output, including custom input.
$expectedOutput = [IO.Path]::GetFullPath((Join-Path $projectRoot 'publish\win-x64'))
$OutputDir = [IO.Path]::GetFullPath($OutputDir).TrimEnd('\', '/')
if (-not $OutputDir.Equals($expectedOutput, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDir must be the dedicated publish directory: $expectedOutput"
}
$directory = $OutputDir
while ($directory) {
    if (Test-Path -LiteralPath $directory) {
        $item = Get-Item -LiteralPath $directory -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing publish through a junction or symbolic link: $directory"
        }
    }
    $directory = Split-Path -Parent $directory
}
$null = Get-Command dotnet -CommandType Application -ErrorAction Stop
if (-not $PSCmdlet.ShouldProcess($OutputDir, 'Delete publish directory and publish current source')) {
    return
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Publishing ebs50_backend (Self-Contained $Runtime)" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "[*] Project: $csprojPath" -ForegroundColor White
Write-Host "[*] Output:  $OutputDir" -ForegroundColor White

if (Test-Path $OutputDir) {
    Write-Host "[*] Cleaning old publish artifacts..." -ForegroundColor Yellow
    # Reject nested links before recursively removing the verified directory.
    $links = Get-ChildItem -LiteralPath $OutputDir -Recurse -Force |
        Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }
    if ($links) { throw 'Publish output contains a junction or symbolic link; cleanup aborted.' }
    Remove-Item -LiteralPath $OutputDir -Recurse -Force -ErrorAction Stop
}

# Run dotnet publish
Write-Host "[*] Executing dotnet publish..." -ForegroundColor Yellow
$publishArgs = @(
    "publish",
    $csprojPath,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-o", $OutputDir
)

& dotnet @publishArgs

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
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
Copy-Item -LiteralPath (Join-Path $projectRoot 'scripts') -Destination $OutputDir -Recurse -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $OutputDir -Recurse -Force
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
