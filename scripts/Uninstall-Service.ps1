# ==============================================================================
# Script: Uninstall-Service.ps1
# Description: Gracefully stops and unregisters the ebs50_backend Windows Service.
# Cleans up firewall rules while preserving the application database.
# ==============================================================================

[CmdletBinding()]
param (
    [string]$ServiceName = "Ebs50TagService",
    [int]$Port = 6789
)

# Require administrator privileges
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script requires Administrator privileges. Please run PowerShell as Administrator."
    exit 1
}

Write-Host "==========================================================" -ForegroundColor Yellow
Write-Host "  Uninstalling Windows Service: $ServiceName" -ForegroundColor Yellow
Write-Host "==========================================================" -ForegroundColor Yellow

# Check if service exists
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -eq 'Running') {
        Write-Host "[*] Stopping service '$ServiceName'..." -ForegroundColor Yellow
        & sc.exe stop $ServiceName | Out-Null
        
        # Wait for service to stop gracefully within 30 seconds
        $timeout = 30
        while ($timeout -gt 0) {
            $currentStatus = (Get-Service -Name $ServiceName).Status
            if ($currentStatus -eq 'Stopped') { break }
            Start-Sleep -Seconds 1
            $timeout--
        }
        Write-Host "[+] Service stopped." -ForegroundColor Green
    }

    Write-Host "[*] Deleting service registration..." -ForegroundColor Yellow
    & sc.exe delete $ServiceName | Out-Null
    Write-Host "[+] Service '$ServiceName' deleted from Windows SCM." -ForegroundColor Green
} else {
    Write-Host "[!] Service '$ServiceName' was not found on this system." -ForegroundColor Gray
}

# Remove Firewall rule
foreach ($networkRule in @('Ebs50-Web-Network-1', 'Ebs50-Web-Network-2')) {
    Get-NetFirewallRule -Name $networkRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
}
$ruleName = "EBS-50 E-Tag Service (Port $Port)"
Write-Host "[*] Removing Windows Firewall rule: $ruleName..." -ForegroundColor Yellow
& netsh advfirewall firewall delete rule name="$ruleName" 2>$null | Out-Null
Write-Host "[+] Firewall rule removed." -ForegroundColor Green

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "  UNINSTALLATION COMPLETED" -ForegroundColor Green
Write-Host "  Note: Database file (etag_database.db) has been preserved." -ForegroundColor White
Write-Host "==========================================================" -ForegroundColor Green
