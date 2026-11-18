param(
    [Parameter(Mandatory)]
    [string]$Executable,
    [Parameter(Mandatory)]
    [string]$BindAddress,
    [string]$TailscaleServiceName = 'Tailscale',
    [ValidateRange(5, 900)]
    [int]$MaxWaitSeconds = 300,
    [ValidateRange(1, 30)]
    [int]$PollSeconds = 5,
    [string]$LogPath = 'C:\DWM157Gateway\monitoring\logs\startup-wrapper.log'
)

$ErrorActionPreference = 'Stop'

function Write-StartupLog([string]$Message) {
    $directory = Split-Path -Parent $LogPath
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    Add-Content -LiteralPath $LogPath -Value "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $Message"
}

if (-not (Test-Path -LiteralPath $Executable)) {
    throw "Monitoring executable was not found: $Executable"
}

$deadline = (Get-Date).AddSeconds($MaxWaitSeconds)
do {
    $tailscaleRunning = (Get-Service -Name $TailscaleServiceName -ErrorAction SilentlyContinue).Status -eq 'Running'
    $addressReady = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -eq $BindAddress -and $_.AddressState -eq 'Preferred' } |
        Select-Object -First 1
    if ($tailscaleRunning -and $null -ne $addressReady) {
        Write-StartupLog "Tailscale and bind address $BindAddress are ready; starting Monitoring."
        $process = Start-Process -FilePath $Executable -WorkingDirectory (Split-Path -Parent $Executable) -PassThru -Wait
        Write-StartupLog "Monitoring exited with code $($process.ExitCode)."
        exit $process.ExitCode
    }
    Start-Sleep -Seconds $PollSeconds
} while ((Get-Date) -lt $deadline)

Write-StartupLog "Timed out after $MaxWaitSeconds seconds waiting for Tailscale and bind address $BindAddress."
exit 1
