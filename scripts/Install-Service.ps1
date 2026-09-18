# ==============================================================================
# Script: Install-Service.ps1
# Description: Installs and registers ebs50_backend as an automated Windows Service.
# Configures auto-recovery, opens firewall port 6789, and starts the service.
# ==============================================================================

[CmdletBinding()]
param (
    [string]$ServiceName = "Ebs50TagService",
    [string]$DisplayName = "EBS-50 E-Tag Management Service",
    [int]$Port = 6789,
    [string]$BinaryPath = ""
)

# NOTE: Require administrator privileges to manage Windows Services and Firewall rules
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script requires Administrator privileges. Please run PowerShell as Administrator."
    exit 1
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Installing $DisplayName as Windows Service" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# Locate ebs50_backend.exe
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptDir

if ([string]::IsNullOrWhiteSpace($BinaryPath)) {
    $candidatePaths = @(
        (Join-Path $scriptDir "ebs50_backend.exe"),
        (Join-Path $projectRoot "publish\win-x64\ebs50_backend.exe"),
        (Join-Path $projectRoot "bin\Release\net8.0\win-x64\publish\ebs50_backend.exe"),
        (Join-Path $projectRoot "bin\Release\net8.0\ebs50_backend.exe"),
        (Join-Path $projectRoot "bin\Debug\net8.0\ebs50_backend.exe")
    )

    foreach ($path in $candidatePaths) {
        if (Test-Path $path) {
            $BinaryPath = (Resolve-Path $path).Path
            break
        }
    }
}

if (-not (Test-Path $BinaryPath)) {
    Write-Error "Could not locate ebs50_backend.exe. Please build or publish the project first, or specify -BinaryPath."
    exit 1
}

Write-Host "[+] Using binary executable: $BinaryPath" -ForegroundColor Green
$workDir = Split-Path -Parent $BinaryPath

# Stop and remove existing service if already registered
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Host "[*] Existing service '$ServiceName' found. Stopping and removing..." -ForegroundColor Yellow
    if ($existingService.Status -eq 'Running') {
        & sc.exe stop $ServiceName | Out-Null
        Start-Sleep -Seconds 2
    }
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
    Write-Host "[+] Old service removed." -ForegroundColor Green
}

# Create Windows Service
Write-Host "[*] Creating Windows Service '$ServiceName'..." -ForegroundColor Yellow
$createOutput = & sc.exe create $ServiceName binPath= "`"$BinaryPath`"" start= auto DisplayName= "$DisplayName"
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to create Windows Service: $createOutput"
    exit $LASTEXITCODE
}
Write-Host "[+] Service created successfully." -ForegroundColor Green

# Set description
& sc.exe description $ServiceName "Opticon EBS-50 E-Tag Management & MES Dispatcher Service (Web UI & REST API on port $Port)." | Out-Null

# Configure failure recovery policy: Restart service on first, second, and subsequent failures after 60 seconds
Write-Host "[*] Configuring auto-recovery policy (Restart on failure)..." -ForegroundColor Yellow
& sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

# Configure Windows Firewall rule for HTTP port
$ruleName = "EBS-50 E-Tag Service (Port $Port)"
Write-Host "[*] Configuring Windows Firewall rule: $ruleName..." -ForegroundColor Yellow
& netsh advfirewall firewall delete rule name="$ruleName" 2>$null | Out-Null
& netsh advfirewall firewall add rule name="$ruleName" dir=in action=allow protocol=TCP localport=$Port profile=any | Out-Null
Write-Host "[+] Firewall port $Port opened." -ForegroundColor Green

# Start the service
Write-Host "[*] Starting Windows Service '$ServiceName'..." -ForegroundColor Yellow
& sc.exe start $ServiceName
if ($LASTEXITCODE -eq 0) {
    Write-Host "[+] Service started successfully!" -ForegroundColor Green
    Start-Sleep -Seconds 3

    Write-Host ""
    Write-Host "==========================================================" -ForegroundColor Cyan
    Write-Host "  INSTALLATION COMPLETED SUCCESSFULLY" -ForegroundColor Green
    Write-Host "  Service Name:    $ServiceName" -ForegroundColor White
    Write-Host "  Working Dir:     $workDir" -ForegroundColor White
    Write-Host "  Dashboard URL:   http://localhost:$Port" -ForegroundColor Cyan
    Write-Host "  Swagger API:     http://localhost:$Port/swagger" -ForegroundColor Cyan
    Write-Host "==========================================================" -ForegroundColor Cyan
} else {
    Write-Warning "Service created but could not start immediately. Check Windows Event Viewer (Application log) for details."
}

