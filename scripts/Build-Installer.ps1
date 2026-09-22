<#
.SYNOPSIS
Publishes current source and builds the Windows installer.
.PARAMETER Configuration
Build configuration (Release by default).
.PARAMETER Runtime
Installer architecture; only win-x64 is supported.
.EXAMPLE
.\scripts\Build-Installer.ps1
#>
[CmdletBinding(SupportsShouldProcess)]
param (
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$publishScript = Join-Path $PSScriptRoot 'Publish-App.ps1'
$issScript = Join-Path $projectRoot 'installer\ebs50_setup.iss'

try {
    $null = Get-Command dotnet -CommandType Application -ErrorAction Stop
    $isccPath = $null
    foreach ($candidate in @('ISCC.exe',
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe',
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'))) {
        $compiler = Get-Command $candidate -CommandType Application -ErrorAction SilentlyContinue
        if ($compiler) {
            $isccPath = $compiler.Source
            break
        }
    }
    if (-not $isccPath) {
        throw 'Inno Setup 6 compiler (ISCC.exe) is required. Install it before building.'
    }
    if (-not $PSCmdlet.ShouldProcess($projectRoot, 'Clean publish output, publish application and build installer')) {
        return
    }
    & $publishScript -Configuration $Configuration -Runtime $Runtime -Confirm:$false
    # Only this successful publish path may bypass the ISS precompile publish.
    & $isccPath '/DAppPublished=1' $issScript
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
    }
    Write-Host "Installer created in: $projectRoot\dist" -ForegroundColor Green
} catch {
    $PSCmdlet.ThrowTerminatingError($_)
}