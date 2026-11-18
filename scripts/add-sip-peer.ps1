param(
    [Parameter(Mandatory = $true)]
    [string]$Peer,
    [string]$Root = 'C:\DWM157Gateway',
    [string]$ServiceName = 'DWM157Gateway',
    [string]$BindAddress = '100.64.0.12'
)

$ErrorActionPreference = 'Stop'

$parsedPeer = [Net.IPAddress]::Parse($Peer)
if ($parsedPeer.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
    throw 'Only an IPv4 SIP peer is supported.'
}

$configPath = Join-Path $Root 'data\config.json'
$logPath = Join-Path $Root 'logs\gateway-service.log'
$ruleNames = @('DWM157Gateway-SIP-UDP', 'DWM157Gateway-RTP-UDP')
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupPath = Join-Path $Root "data\config.pre-sip-peer-$stamp.json"
$tempPath = "$configPath.tmp"
$config = Get-Content $configPath -Raw | ConvertFrom-Json
$oldPeers = @($config.Sip.AllowedTailscaleIPs | ForEach-Object { [string]$_ })
$oldRuleAddresses = @{}

foreach ($ruleName in $ruleNames) {
    $rule = Get-NetFirewallRule -DisplayName $ruleName
    $oldRuleAddresses[$ruleName] = @(
        $rule | Get-NetFirewallAddressFilter | Select-Object -ExpandProperty RemoteAddress
    )
}

Copy-Item -LiteralPath $configPath -Destination $backupPath -Force

try {
    $newPeers = @($oldPeers + $Peer | Sort-Object -Unique)
    $config.Sip.AllowedTailscaleIPs = $newPeers
    $json = $config | ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText($tempPath, $json, (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $tempPath -Destination $configPath -Force

    foreach ($ruleName in $ruleNames) {
        Get-NetFirewallRule -DisplayName $ruleName |
            Get-NetFirewallAddressFilter |
            Set-NetFirewallAddressFilter -RemoteAddress $newPeers | Out-Null
    }

    Restart-Service -Name $ServiceName -Force
    $deadline = (Get-Date).AddSeconds(120)
    do {
        Start-Sleep -Seconds 2
        $service = Get-Service -Name $ServiceName
        $serviceProcess = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
        $endpoint = Get-NetUDPEndpoint -LocalAddress $BindAddress -LocalPort $config.Sip.SipPort `
            -ErrorAction SilentlyContinue
        $healthy = $service.Status -eq 'Running' -and
            $serviceProcess.ProcessId -gt 0 -and
            $endpoint.OwningProcess -eq $serviceProcess.ProcessId
    } until ($healthy -or (Get-Date) -ge $deadline)

    if (-not $healthy) {
        throw 'Gateway service did not own the SIP endpoint before the health deadline.'
    }

    $verifiedConfig = Get-Content $configPath -Raw | ConvertFrom-Json
    if ($Peer -notin @($verifiedConfig.Sip.AllowedTailscaleIPs)) {
        throw 'The SIP peer was not persisted in config.'
    }

    $verifiedRules = foreach ($ruleName in $ruleNames) {
        $rule = Get-NetFirewallRule -DisplayName $ruleName
        $addresses = @(
            $rule | Get-NetFirewallAddressFilter | Select-Object -ExpandProperty RemoteAddress
        )
        if ($Peer -notin $addresses) {
            throw "The SIP peer is absent from firewall rule $ruleName."
        }

        [pscustomobject]@{
            Rule = $ruleName
            RemoteAddress = $addresses
        }
    }

    [pscustomobject]@{
        Success = $true
        Backup = $backupPath
        AllowedPeers = @($verifiedConfig.Sip.AllowedTailscaleIPs)
        ServiceStatus = $service.Status.ToString()
        Pid = $serviceProcess.ProcessId
        SipOwner = $endpoint.OwningProcess
        Firewall = $verifiedRules
        RecentLog = @(
            Get-Content $logPath -Tail 20 | ForEach-Object { [string]$_ }
        )
    } | ConvertTo-Json -Depth 5
}
catch {
    if (Test-Path -LiteralPath $tempPath) {
        Remove-Item -LiteralPath $tempPath -Force
    }

    Copy-Item -LiteralPath $backupPath -Destination $configPath -Force
    foreach ($ruleName in $ruleNames) {
        Get-NetFirewallRule -DisplayName $ruleName |
            Get-NetFirewallAddressFilter |
            Set-NetFirewallAddressFilter -RemoteAddress $oldRuleAddresses[$ruleName] | Out-Null
    }

    Restart-Service -Name $ServiceName -Force
    throw
}
