param(
    [Parameter(Mandatory)]
    [string]$ArchivePath,
    [string]$MonitoringRoot = 'C:\DWM157Gateway\monitoring',
    [string]$TaskName = 'DWM157MonitorHost-0.1.0',
    [string]$ExpectedArchiveSha256 = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function New-MonitoringTaskAction {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [AllowEmptyString()][string]$Arguments = ''
    )
    if ([string]::IsNullOrWhiteSpace($Arguments)) {
        return New-ScheduledTaskAction -Execute $Executable -WorkingDirectory $WorkingDirectory
    }
    return New-ScheduledTaskAction -Execute $Executable -Argument $Arguments -WorkingDirectory $WorkingDirectory
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this deployment script from an elevated PowerShell window on Gateway PC.'
}

$resolvedArchive = (Resolve-Path -LiteralPath $ArchivePath).Path
if ([IO.Path]::GetExtension($resolvedArchive) -ne '.zip') {
    throw 'ArchivePath must point to the Monitoring 0.4.0 zip file.'
}

$archiveHash = (Get-FileHash -LiteralPath $resolvedArchive -Algorithm SHA256).Hash
if ($ExpectedArchiveSha256 -and $archiveHash -ne $ExpectedArchiveSha256.ToUpperInvariant()) {
    throw "Archive SHA256 mismatch. Actual: $archiveHash"
}

$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop
$oldAction = @($task.Actions)[0]
if (-not $oldAction.Execute) {
    throw 'The current Monitoring task has no executable action.'
}

$oldExecutable = $oldAction.Execute.Trim('"')
$oldArguments = if ($null -eq $oldAction.Arguments) { '' } else { [string]$oldAction.Arguments }
$oldDirectory = if ($oldAction.WorkingDirectory) {
    $oldAction.WorkingDirectory
} else {
    Split-Path -Parent $oldExecutable
}
$oldConfig = Join-Path $oldDirectory 'appsettings.json'
if (-not (Test-Path -LiteralPath $oldConfig)) {
    throw "Current Monitoring configuration was not found at $oldConfig"
}

$config = Get-Content -LiteralPath $oldConfig -Raw | ConvertFrom-Json
$port = [int]$config.Monitoring.Port
$expectedBindAddress = [string]$config.Monitoring.BindAddress
if (-not $expectedBindAddress -or $expectedBindAddress -eq '0.0.0.0' -or $expectedBindAddress -eq '::') {
    throw 'The retained Monitoring configuration has an unsafe bind address.'
}

$releaseRoot = Join-Path $MonitoringRoot 'releases'
$releasePath = Join-Path $releaseRoot '0.4.0'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stagingPath = Join-Path $MonitoringRoot "staging-0.4.0-$stamp"
$backupRoot = Join-Path $MonitoringRoot 'rollback'
$taskBackup = Join-Path $backupRoot "task-before-0.4.0-$stamp.xml"
$existingReleaseBackup = Join-Path $backupRoot "release-0.4.0-before-$stamp"

New-Item -ItemType Directory -Path $releaseRoot, $backupRoot -Force | Out-Null
Export-ScheduledTask -TaskName $TaskName | Set-Content -LiteralPath $taskBackup -Encoding Unicode
New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null
Expand-Archive -LiteralPath $resolvedArchive -DestinationPath $stagingPath -Force

$newExecutable = Join-Path $stagingPath 'DWM157Gateway.Monitoring.exe'
if (-not (Test-Path -LiteralPath $newExecutable)) {
    throw 'The archive does not contain DWM157Gateway.Monitoring.exe at its root.'
}
Copy-Item -LiteralPath $oldConfig -Destination (Join-Path $stagingPath 'appsettings.json') -Force

$oldTaskState = $task.State
try {
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    $deadline = (Get-Date).AddSeconds(20)
    do {
        $oldProcesses = Get-CimInstance Win32_Process | Where-Object {
            $_.Name -eq 'DWM157Gateway.Monitoring.exe' -and $_.ExecutablePath -eq $oldExecutable
        }
        if (-not $oldProcesses) { break }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    if ($oldProcesses) {
        throw 'The previous Monitoring process did not stop cleanly; deployment was not changed.'
    }

    if (Test-Path -LiteralPath $releasePath) {
        Move-Item -LiteralPath $releasePath -Destination $existingReleaseBackup
    }
    Move-Item -LiteralPath $stagingPath -Destination $releasePath
    $newExecutable = Join-Path $releasePath 'DWM157Gateway.Monitoring.exe'
    $newAction = New-MonitoringTaskAction -Executable $newExecutable -Arguments $oldArguments -WorkingDirectory $releasePath
    Set-ScheduledTask -TaskName $TaskName -Action $newAction | Out-Null
    Start-ScheduledTask -TaskName $TaskName

    $ready = $false
    $newProcess = $null
    $listener = $null
    $deadline = (Get-Date).AddSeconds(60)
    do {
        Start-Sleep -Seconds 1
        $newProcess = Get-CimInstance Win32_Process | Where-Object {
            $_.Name -eq 'DWM157Gateway.Monitoring.exe' -and $_.ExecutablePath -eq $newExecutable
        } | Select-Object -First 1
        if ($newProcess) {
            $listener = Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue |
                Where-Object { $_.OwningProcess -eq $newProcess.ProcessId -and $_.LocalAddress -eq $expectedBindAddress } |
                Select-Object -First 1
        }
        $ready = $null -ne $newProcess -and $null -ne $listener
    } while (-not $ready -and (Get-Date) -lt $deadline)

    if (-not $ready) {
        throw "Monitoring 0.4.0 did not own $expectedBindAddress`:$port within 60 seconds."
    }

    $historyPath = if ($config.Monitoring.PSObject.Properties.Name -contains 'HistoryDatabasePath') {
        [string]$config.Monitoring.HistoryDatabasePath
    } else {
        'C:\DWM157Gateway\data\monitoring\history.db'
    }
    [pscustomobject]@{
        Version = '0.4.0'
        TaskName = $TaskName
        TaskState = (Get-ScheduledTask -TaskName $TaskName).State
        ProcessId = $newProcess.ProcessId
        Listener = "$expectedBindAddress`:$port"
        ReleasePath = $releasePath
        ExecutableSha256 = (Get-FileHash -LiteralPath $newExecutable -Algorithm SHA256).Hash
        ArchiveSha256 = $archiveHash
        RetainedConfiguration = $oldConfig
        HistoryDatabasePath = $historyPath
        TaskBackup = $taskBackup
        PreviousRelease = $oldDirectory
        Existing040Backup = if (Test-Path -LiteralPath $existingReleaseBackup) { $existingReleaseBackup } else { $null }
    }
} catch {
    $failure = $_
    $rollbackAction = New-MonitoringTaskAction -Executable $oldExecutable -Arguments $oldArguments -WorkingDirectory $oldDirectory
    Set-ScheduledTask -TaskName $TaskName -Action $rollbackAction | Out-Null
    if ($oldTaskState -eq 'Running' -or $oldTaskState -eq 'Ready') {
        Start-ScheduledTask -TaskName $TaskName
    }
    throw "Monitoring deployment rolled back to $oldDirectory. $($failure.Exception.Message)"
}
