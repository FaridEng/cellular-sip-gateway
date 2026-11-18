param(
    [string]$ReleasePath = 'C:\DWM157Gateway\releases\0.2.11',
    [string]$DataPath = 'C:\DWM157Gateway\data',
    [string]$LogPath = 'C:\DWM157Gateway\logs\gateway-service.log'
)

$ErrorActionPreference = 'Stop'
$serviceName = 'DWM157Gateway'
$exePath = Join-Path $ReleasePath 'service\DWM157Gateway.Service.exe'
$configPath = Join-Path $DataPath 'config.json'

if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Service executable is missing: $exePath"
}
if (-not (Test-Path -LiteralPath $configPath)) {
    throw "Runtime config is missing: $configPath"
}

New-Item -ItemType Directory -Path (Split-Path -Parent $LogPath) -Force | Out-Null
$binaryPath = ('"{0}" --config "{1}" --log "{2}"' -f $exePath, $configPath, $LogPath)
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    & sc.exe config $serviceName binPath= $binaryPath start= delayed-auto | Out-Null
}
else {
    & sc.exe create $serviceName binPath= $binaryPath start= delayed-auto DisplayName= 'DWM157 Gateway' | Out-Null
}

& sc.exe description $serviceName 'DWM-157 cellular voice to SIP gateway over Tailscale.' | Out-Null
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
& sc.exe failureflag $serviceName 1 | Out-Null
Start-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
Get-Service -Name $serviceName | Select-Object Name,Status,StartType
