# Run elevated on Gateway PC. Preserves the Tailscale-aware startup wrapper and prior release.
param(
    [Parameter(Mandatory)][string]$ArchivePath,
    [Parameter(Mandatory)][string]$ExpectedArchiveSha256,
    [string]$ObserverAddress = '100.64.0.13',
    [string]$Root = 'C:\DWM157Gateway\monitoring',
    [string]$TaskName = 'DWM157MonitorHost-0.1.0'
)
$ErrorActionPreference = 'Stop'
if ((Get-FileHash -LiteralPath $ArchivePath).Hash -ne $ExpectedArchiveSha256) { throw 'Archive hash mismatch' }
if ($ObserverAddress -notmatch '^100\.(6[4-9]|[7-9][0-9]|1[01][0-9]|12[0-7])\.\d+\.\d+$') { throw 'Observer must be an explicit Tailscale IPv4 address' }
$task = Get-ScheduledTask -TaskName $TaskName
$oldAction = $task.Actions[0]
$oldArgs = [string]$oldAction.Arguments
if ($oldArgs -match '-Executable\s+"([^"]+)"') { $oldExecutable = $Matches[1] }
elseif ($oldAction.Execute -like '*DWM157Gateway.Monitoring.exe') { $oldExecutable = $oldAction.Execute.Trim('"') }
else { throw 'Unrecognized monitoring task action; refusing to replace its startup behavior' }
$oldConfig = Join-Path (Split-Path -Parent $oldExecutable) 'appsettings.json'
$config = Get-Content -LiteralPath $oldConfig -Raw | ConvertFrom-Json
$peers = @($config.Monitoring.AllowedRemoteAddresses)
if ($ObserverAddress -notin $peers) { $config.Monitoring.AllowedRemoteAddresses = @($peers + $ObserverAddress) }
$release = Join-Path $Root 'releases\0.5.0'
if (Test-Path -LiteralPath $release) { throw 'Release 0.5.0 already exists; inspect it before deploying' }
$backup = Join-Path $Root ('rollback\before-0.5.0-' + (Get-Date -Format yyyyMMdd-HHmmss))
New-Item -ItemType Directory -Path $backup -Force | Out-Null
Export-ScheduledTask -TaskName $TaskName | Set-Content -LiteralPath (Join-Path $backup 'task.xml') -Encoding Unicode
Copy-Item -LiteralPath $oldConfig -Destination (Join-Path $backup 'appsettings.json')
$firewall = @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq 'DWM157Monitor-HTTP-Tailscale' -or $_.DisplayName -eq 'DWM157Monitor-HTTP-Tailscale' })
$oldRemote = @()
if ($firewall.Count -eq 1) { $oldRemote = @($firewall[0] | Get-NetFirewallAddressFilter | Select-Object -ExpandProperty RemoteAddress) }
$oldRemote | ConvertTo-Json | Set-Content (Join-Path $backup 'firewall-remote-addresses.json')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ArchivePath).Path)
try {
    $entry = $zip.GetEntry('release-base.json')
    if ($entry) {
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $deltaBase = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    } else { $deltaBase = $null }
} finally { $zip.Dispose() }
if ($deltaBase) {
    $oldDirectory = Split-Path -Parent $oldExecutable
    foreach ($required in $deltaBase.requiredFiles) {
        if ($required.name -notmatch '^[A-Za-z0-9._-]+$') { throw 'Invalid delta base filename' }
        if ((Get-FileHash -LiteralPath (Join-Path $oldDirectory $required.name)).Hash -ne $required.sha256) {
            throw "Delta runtime base differs: $($required.name)"
        }
    }
    Copy-Item -LiteralPath $oldDirectory -Destination $release -Recurse
    Expand-Archive -LiteralPath $ArchivePath -DestinationPath $release -Force
} else { Expand-Archive -LiteralPath $ArchivePath -DestinationPath $release }
$newExecutable = Join-Path $release 'DWM157Gateway.Monitoring.exe'
if (!(Test-Path $newExecutable)) { throw 'Release archive is missing its executable' }
$config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $release 'appsettings.json') -Encoding UTF8
$previousState = $task.State
try {
    Stop-ScheduledTask -TaskName $TaskName
    Start-Sleep -Seconds 2
    Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $oldExecutable } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
    if ($oldArgs -match '-Executable\s+"') {
        $newArgs = $oldArgs.Replace($oldExecutable, $newExecutable)
        $action = New-ScheduledTaskAction -Execute $oldAction.Execute -Argument $newArgs -WorkingDirectory $oldAction.WorkingDirectory
    } else {
        $action = New-ScheduledTaskAction -Execute $newExecutable -WorkingDirectory $release
    }
    if ($firewall.Count -eq 1 -and $ObserverAddress -notin $oldRemote) {
        $firewall[0] | Get-NetFirewallAddressFilter | Set-NetFirewallAddressFilter -RemoteAddress @($oldRemote + $ObserverAddress)
    }
    Set-ScheduledTask -TaskName $TaskName -Action $action | Out-Null
    Start-ScheduledTask -TaskName $TaskName
    $ready = $false
    for ($i=0; $i -lt 45; $i++) {
        Start-Sleep -Seconds 1
        $hostProcess = Get-CimInstance Win32_Process | Where-Object ExecutablePath -eq $newExecutable | Select-Object -First 1
        if ($hostProcess) {
            $listener = Get-NetTCPConnection -State Listen -LocalPort $config.Monitoring.Port -ErrorAction SilentlyContinue |
                Where-Object { $_.OwningProcess -eq $hostProcess.ProcessId -and $_.LocalAddress -eq $config.Monitoring.BindAddress }
            if ($listener) { $ready=$true; break }
        }
    }
    if (!$ready) { throw 'Candidate monitoring host did not own its restricted listener' }
    [pscustomobject]@{Version='0.5.0';ProcessId=$hostProcess.ProcessId;Backup=$backup;OldExecutable=$oldExecutable;Gateway=(Get-Service DWM157Gateway).Status.ToString()} | ConvertTo-Json -Compress
} catch {
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    Get-CimInstance Win32_Process | Where-Object ExecutablePath -eq $newExecutable | ForEach-Object {Stop-Process -Id $_.ProcessId -Force}
    if ($oldArgs) { $rollbackAction=New-ScheduledTaskAction -Execute $oldAction.Execute -Argument $oldArgs -WorkingDirectory $oldAction.WorkingDirectory }
    else { $rollbackAction=New-ScheduledTaskAction -Execute $oldExecutable -WorkingDirectory (Split-Path -Parent $oldExecutable) }
    Set-ScheduledTask -TaskName $TaskName -Action $rollbackAction | Out-Null
    if ($firewall.Count -eq 1 -and $oldRemote.Count -gt 0) { $firewall[0] | Get-NetFirewallAddressFilter | Set-NetFirewallAddressFilter -RemoteAddress $oldRemote }
    if ($previousState -eq 'Running' -or $previousState -eq 'Ready') { Start-ScheduledTask -TaskName $TaskName }
    throw
}
