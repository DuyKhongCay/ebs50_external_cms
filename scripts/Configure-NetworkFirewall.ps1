# Manages only EBS web rules. Does not change IP, DNS, routes or forwarding.
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot '..\appsettings.json'),
    [string[]]$InterfaceAliases,
    [string[]]$RemoteSubnets,
    [ValidateRange(1, 65535)][int]$Port,
    [string]$BackupPath = (Join-Path $PSScriptRoot 'network-firewall-backup.clixml'),
    [switch]$Restore
)
$ErrorActionPreference = 'Stop'
$managedNames = @('Ebs50-Web-Network-1', 'Ebs50-Web-Network-2')
function Read-RuleDefinition($rule) {
    $ports = $rule | Get-NetFirewallPortFilter
    $addresses = $rule | Get-NetFirewallAddressFilter
    $interfaces = $rule | Get-NetFirewallInterfaceFilter
    $application = $rule | Get-NetFirewallApplicationFilter
    $service = $rule | Get-NetFirewallServiceFilter
    return @{
        Name = $rule.Name; DisplayName = $rule.DisplayName; Description = $rule.Description
        Enabled = [string]$rule.Enabled; Profile = [string]$rule.Profile
        Direction = [string]$rule.Direction; Action = [string]$rule.Action
        Protocol = [string]$ports.Protocol; LocalPort = $ports.LocalPort; RemotePort = $ports.RemotePort
        LocalAddress = $addresses.LocalAddress; RemoteAddress = $addresses.RemoteAddress
        InterfaceAlias = $interfaces.InterfaceAlias; Program = $application.Program; Service = $service.Service
        EdgeTraversalPolicy = [string]$rule.EdgeTraversalPolicy
    }
}
if ($Restore) {
    $backup = Import-Clixml -LiteralPath $BackupPath
    if ($backup.Version -ne 1) { throw 'Unsupported backup format.' }
    # Prevent an unrelated backup from replacing arbitrary firewall rules.
    foreach ($definition in $backup.Rules) {
        if ($definition.Name -notin $managedNames -and $definition.DisplayName -notmatch '^EBS-50 E-Tag Service \(Port \d+\)$') {
            throw 'Backup contains an unrelated rule.'
        }
    }
    if ($PSCmdlet.ShouldProcess('EBS web firewall rules', "Restore from $BackupPath")) {
        foreach ($name in $managedNames) {
            Get-NetFirewallRule -PolicyStore PersistentStore -Name $name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        }
        foreach ($definition in $backup.Rules) {
            Get-NetFirewallRule -PolicyStore PersistentStore -Name $definition.Name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
            New-NetFirewallRule @definition | Out-Null
        }
    }
    return
}
if ($null -ne $InterfaceAliases -and $InterfaceAliases.Count -eq 1 -and $InterfaceAliases[0].Contains(',')) {
    $InterfaceAliases = @($InterfaceAliases[0].Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}
if ($null -ne $RemoteSubnets -and $RemoteSubnets.Count -eq 1 -and $RemoteSubnets[0].Contains(',')) {
    $RemoteSubnets = @($RemoteSubnets[0].Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}
if ($InterfaceAliases.Count -ne 2 -or $RemoteSubnets.Count -ne 2) {
    throw 'Supply exactly two InterfaceAliases and two matching IPv4 RemoteSubnets.'
}
if ($InterfaceAliases[0] -eq $InterfaceAliases[1]) { throw 'Choose two distinct interfaces.' }
foreach ($alias in $InterfaceAliases) {
    if ($alias.IndexOfAny([char[]]'*?[]') -ge 0) { throw 'Interface wildcards are not allowed.' }
    Get-NetAdapter -Name $alias -ErrorAction Stop | Out-Null
}
foreach ($subnet in $RemoteSubnets) {
    $parts = $subnet.Split('/')
    $address = $null
    $prefix = 0
    if ($parts.Count -ne 2 -or -not [Net.IPAddress]::TryParse($parts[0], [ref]$address) -or
        $address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or
        -not [int]::TryParse($parts[1], [ref]$prefix) -or $prefix -lt 1 -or $prefix -gt 32) {
        throw "Invalid IPv4 subnet: $subnet (prefix must be 1..32)."
    }
}
$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$configuredPort = 6789
if ($null -ne $config.ServiceSettings.Port) { $configuredPort = [int]$config.ServiceSettings.Port }
if ($configuredPort -lt 1 -or $configuredPort -gt 65535) { throw 'Invalid configured port.' }
if ($PSBoundParameters.ContainsKey('Port') -and $Port -ne $configuredPort) {
    throw "Port $Port differs from ServiceSettings:Port ($configuredPort)."
}
$Port = $configuredPort
$rules = @(Get-NetFirewallRule -PolicyStore PersistentStore | Where-Object {
    $_.Name -in $managedNames -or $_.DisplayName -match '^EBS-50 E-Tag Service \(Port \d+\)$'
})
$description = "Allow TCP $Port on $($InterfaceAliases[0]) from $($RemoteSubnets[0]), and $($InterfaceAliases[1]) from $($RemoteSubnets[1]); replace existing EBS web rules"
if ($PSCmdlet.ShouldProcess('EBS web firewall rules', $description)) {
    if (Test-Path -LiteralPath $BackupPath) { throw 'Backup already exists. Choose a new BackupPath to preserve rollback history.' }
    $definitions = @($rules | ForEach-Object { Read-RuleDefinition $_ })
    @{ Version = 1; Rules = $definitions } | Export-Clixml -LiteralPath $BackupPath
    try {
        foreach ($rule in $rules) { $rule | Remove-NetFirewallRule }
        for ($i = 0; $i -lt 2; $i++) {
            New-NetFirewallRule -Name $managedNames[$i] -DisplayName $managedNames[$i] `
                -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port `
                -InterfaceAlias $InterfaceAliases[$i] -RemoteAddress $RemoteSubnets[$i] `
                -Profile Any -EdgeTraversalPolicy Block | Out-Null
        }
    } catch {
        $failure = $_
        foreach ($name in $managedNames) {
            Get-NetFirewallRule -PolicyStore PersistentStore -Name $name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        }
        foreach ($definition in $definitions) {
            Get-NetFirewallRule -PolicyStore PersistentStore -Name $definition.Name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
            New-NetFirewallRule @definition | Out-Null
        }
        throw $failure
    }
    Write-Host "Applied. Rollback backup: $BackupPath"
}
