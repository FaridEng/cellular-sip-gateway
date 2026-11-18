param(
    [string]$Root = 'C:\DWM157Gateway',
    [Parameter(Mandatory)]
    [string]$ExpectedZipHash,
    [string]$Version = '0.2.11',
    [string]$ArchiveName = 'DWM157Gateway-0.2.11.zip',
    [string]$RollbackVersion = '0.2.10',
    [string[]]$AllowedPeers = @('100.64.0.16', '100.64.0.14', '100.64.0.18', '100.64.0.11')
)

$ErrorActionPreference = 'Stop'
$zipPath = Join-Path $Root $ArchiveName
$releasePath = Join-Path $Root "releases\$Version"
$rollbackRelease = Join-Path $Root "releases\$RollbackVersion"
$dataPath = Join-Path $Root 'data'
$serviceConfig = Join-Path $dataPath 'config.json'
$logPath = Join-Path $Root 'logs\gateway-service.log'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupPath = Join-Path $Root "rollback-phase2-$stamp"
$serviceName = 'DWM157Gateway'

if (-not (Test-Path -LiteralPath $zipPath)) {
    throw "Candidate archive is missing: $zipPath"
}
$actualHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
if ($actualHash -ne $ExpectedZipHash) {
    throw "Candidate archive hash mismatch: $actualHash"
}

New-Item -ItemType Directory -Path $backupPath -Force | Out-Null
New-Item -ItemType Directory -Path $dataPath -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $logPath) -Force | Out-Null

if (-not (Test-Path -LiteralPath $serviceConfig)) {
    throw "Runtime config is missing: $serviceConfig"
}
if (Test-Path -LiteralPath (Join-Path $dataPath 'config.json')) {
    Copy-Item -LiteralPath (Join-Path $dataPath 'config.json') -Destination (Join-Path $backupPath 'service-config.json') -Force
}
$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existingService) {
    & sc.exe qc $serviceName | Set-Content -LiteralPath (Join-Path $backupPath 'service-qc.txt')
    if ($existingService.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        $existingService.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
}

Get-CimInstance Win32_Process |
    Where-Object {
        $_.Name -in @('DWM157Gateway.Service.exe', 'DWM157Gateway.WinForms.exe') -and
        $_.ExecutablePath -like "$Root\*"
    } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 3

try {
    if (Test-Path -LiteralPath $releasePath) {
        $existingCandidate = Join-Path $backupPath "existing-$Version"
        Move-Item -LiteralPath $releasePath -Destination $existingCandidate
    }
    New-Item -ItemType Directory -Path $releasePath -Force | Out-Null
    Expand-Archive -LiteralPath $zipPath -DestinationPath $releasePath -Force

    $config = Get-Content -LiteralPath $serviceConfig -Raw | ConvertFrom-Json
    $config.Version = $Version
    if ($config.DialPolicy.PSObject.Properties.Name -contains 'AllowUnlistedNumbers') {
        $config.DialPolicy.AllowUnlistedNumbers = $true
    }
    else {
        $config.DialPolicy | Add-Member -NotePropertyName AllowUnlistedNumbers -NotePropertyValue $true
    }
    $effectivePeers = @($config.Sip.AllowedTailscaleIPs + $AllowedPeers | ForEach-Object { [string]$_ } | Sort-Object -Unique)
    $config.Sip.AllowedTailscaleIPs = $effectivePeers
    if ($config.Sip.PSObject.Properties.Name -contains 'AuthenticatedPeerContactLifetimeSeconds') {
        $config.Sip.AuthenticatedPeerContactLifetimeSeconds = 86400
    }
    else {
        $config.Sip | Add-Member -NotePropertyName AuthenticatedPeerContactLifetimeSeconds -NotePropertyValue 86400
    }
    if ($config.Sip.PSObject.Properties.Name -contains 'ContactCacheFile') {
        $config.Sip.ContactCacheFile = 'sip-contacts.json'
    }
    else {
        $config.Sip | Add-Member -NotePropertyName ContactCacheFile -NotePropertyValue 'sip-contacts.json'
    }
    $incoming = [pscustomobject]@{
        Enabled = $true
        PollIntervalMilliseconds = 1500
        CallerIdWaitMilliseconds = 1000
        SipContactWaitSeconds = 20
        SipRingTimeoutSeconds = 35
        CellularAnswerTimeoutSeconds = 15
    }
    if (-not ($config.PSObject.Properties.Name -contains 'Logging')) {
        $config | Add-Member -NotePropertyName Logging -NotePropertyValue ([pscustomobject]@{})
    }
    if ($config.Logging.PSObject.Properties.Name -contains 'CallHistoryFile') {
        $config.Logging.CallHistoryFile = 'call-history.json'
    }
    else {
        $config.Logging | Add-Member -NotePropertyName CallHistoryFile -NotePropertyValue 'call-history.json'
    }
    if ($config.Logging.PSObject.Properties.Name -contains 'MaxCallHistoryRecords') {
        $config.Logging.MaxCallHistoryRecords = 500
    }
    else {
        $config.Logging | Add-Member -NotePropertyName MaxCallHistoryRecords -NotePropertyValue 500
    }
    if ($config.PSObject.Properties.Name -contains 'IncomingCalls') {
        $config.IncomingCalls = $incoming
    }
    else {
        $config | Add-Member -NotePropertyName IncomingCalls -NotePropertyValue $incoming
    }
    $json = $config | ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText($serviceConfig, $json, (New-Object Text.UTF8Encoding($false)))

    foreach ($ruleName in @('DWM157Gateway-SIP-UDP', 'DWM157Gateway-RTP-UDP')) {
        $rule = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction Stop
        $rule | Get-NetFirewallAddressFilter | Set-NetFirewallAddressFilter -RemoteAddress $effectivePeers | Out-Null
    }

    & (Join-Path $Root 'install-windows-service.ps1') -ReleasePath $releasePath -DataPath $dataPath -LogPath $logPath
    $healthDeadline = (Get-Date).AddMinutes(3)
    do {
        Start-Sleep -Seconds 5
        $service = Get-Service -Name $serviceName
        $tail = @(Get-Content -LiteralPath $logPath -Tail 300)
        $healthyLog = ($tail -match "DWM157Gateway $([regex]::Escape($Version)) starting") `
            -and ($tail -match 'Modem preflight: AT=True, SIM=True, registered=True, DTMF=True') `
            -and ($tail -match 'SIP UDP listening on 100.64.0.12:5060')
    } while ($service.Status -eq 'Running' -and -not $healthyLog -and (Get-Date) -lt $healthDeadline)

    if ($service.Status -ne 'Running') {
        throw "Service status is $($service.Status)."
    }
    $process = Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq (Join-Path $releasePath 'service\DWM157Gateway.Service.exe') }
    if (-not $process) {
        throw 'The Phase 2 service process is not running from the expected release.'
    }
    $tail = @(Get-Content -LiteralPath $logPath -Tail 300)
    if (-not ($tail -match "DWM157Gateway $([regex]::Escape($Version)) starting")) {
        throw 'The service did not log the expected Phase 2 version.'
    }
    if (-not ($tail -match 'Modem preflight: AT=True, SIM=True, registered=True, DTMF=True')) {
        throw 'The service did not pass modem preflight.'
    }
    if (-not ($tail -match 'SIP UDP listening on 100.64.0.12:5060')) {
        throw 'The service did not start the SIP listener.'
    }
    $endpoint = Get-NetUDPEndpoint -LocalAddress '100.64.0.12' -LocalPort 5060 -ErrorAction Stop
    if ($endpoint.OwningProcess -ne $process.ProcessId) {
        throw 'The service process does not own the expected SIP endpoint.'
    }

    [pscustomobject]@{
        Release = $releasePath
        Backup = $backupPath
        ServiceStatus = $service.Status.ToString()
        StartType = $service.StartType.ToString()
        Pid = $process.ProcessId
        SipEndpointOwner = $endpoint.OwningProcess
        IncomingEnabled = $config.IncomingCalls.Enabled
        AllowedPeers = @($config.Sip.AllowedTailscaleIPs)
        ArchiveHash = $actualHash
    } | ConvertTo-Json -Compress
}
catch {
    $failure = $_.Exception.Message
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath (Join-Path $backupPath 'service-config.json')) {
        Copy-Item -LiteralPath (Join-Path $backupPath 'service-config.json') -Destination (Join-Path $dataPath 'config.json') -Force
    }
    if (Test-Path -LiteralPath (Join-Path $rollbackRelease 'service\DWM157Gateway.Service.exe')) {
        & (Join-Path $Root 'install-windows-service.ps1') -ReleasePath $rollbackRelease -DataPath $dataPath -LogPath $logPath
    }
    else {
        throw "Phase 2 deployment failed and rollback release '$rollbackRelease' is missing. Original reason: $failure"
    }
    throw "Phase 2 deployment failed; the previous release was restarted. Reason: $failure"
}
