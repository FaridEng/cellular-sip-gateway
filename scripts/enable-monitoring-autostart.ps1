param(
    [string]$TaskName = 'DWM157MonitorHost-0.1.0',
    [string]$BackupDirectory = 'C:\DWM157Gateway\monitoring\data',
    [string]$WrapperPath = 'C:\DWM157Gateway\monitoring\scripts\start-monitoring-after-tailscale.ps1',
    [ValidateRange(0, 3600)]
    [int]$DelaySeconds = 60,
    [ValidateRange(5, 900)]
    [int]$TailscaleWaitSeconds = 300,
    [switch]$StartNow
)

$ErrorActionPreference = 'Stop'
$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop
New-Item -ItemType Directory -Path $BackupDirectory -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupPath = Join-Path $BackupDirectory "task-before-autostart-$stamp.xml"
Export-ScheduledTask -TaskName $TaskName |
    Set-Content -LiteralPath $backupPath -Encoding Unicode

$currentAction = @($task.Actions)[0]
$executable = $currentAction.Execute.Trim('"')
if (-not (Test-Path -LiteralPath $executable)) { throw "Monitoring executable was not found: $executable" }
$configPath = Join-Path (Split-Path -Parent $executable) 'appsettings.json'
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$bindAddress = [string]$config.Monitoring.BindAddress
if (-not $bindAddress -or $bindAddress -eq '0.0.0.0' -or $bindAddress -eq '::') {
    throw "Monitoring bind address is unsafe or missing in $configPath"
}
if (-not (Test-Path -LiteralPath $WrapperPath)) { throw "Monitoring startup wrapper was not found: $WrapperPath" }

$trigger = New-ScheduledTaskTrigger -AtStartup
if ($DelaySeconds -gt 0) {
    $trigger.Delay = "PT${DelaySeconds}S"
}
$actionArguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$WrapperPath`" -Executable `"$executable`" -BindAddress $bindAddress -MaxWaitSeconds $TailscaleWaitSeconds"
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $actionArguments -WorkingDirectory (Split-Path -Parent $WrapperPath)
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
if ($task.State -eq 'Running') { Stop-ScheduledTask -TaskName $TaskName }
Set-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings | Out-Null
if ($StartNow -and $task.State -ne 'Running') {
    Start-ScheduledTask -TaskName $TaskName
}

$updated = Get-ScheduledTask -TaskName $TaskName
[pscustomobject]@{
    TaskName = $TaskName
    State = $updated.State
    StartupDelaySeconds = $DelaySeconds
    TailscaleWaitSeconds = $TailscaleWaitSeconds
    WrapperPath = $WrapperPath
    BackupPath = $backupPath
}
