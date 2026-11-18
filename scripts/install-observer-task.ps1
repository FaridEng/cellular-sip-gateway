param(
    [Parameter(Mandatory)][string]$Executable,
    [string]$ConfigDirectory = (Split-Path -Parent $Executable),
    [string]$TaskName = 'DWM157-IndependentObserver',
    [switch]$CurrentUserOnly
)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $Executable) -or !(Test-Path -LiteralPath (Join-Path $ConfigDirectory 'observer.json'))) {
    throw 'Build and configure the observer before installing its task'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$admin = ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (!$admin -and !$CurrentUserOnly) { throw 'Run as Administrator for a SYSTEM startup task, or explicitly choose -CurrentUserOnly (requires an interactive user session)' }
$existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($existing) { throw 'Observer task already exists; inspect/export it before an update' }
$observer = (Get-Content -LiteralPath (Join-Path $ConfigDirectory 'observer.json') -Raw | ConvertFrom-Json).Observer
$allowedPeers = @($observer.AllowedRemoteAddresses + $observer.ReporterAddresses | Select-Object -Unique)
foreach ($address in @($observer.BindAddress) + $allowedPeers) {
    $parsedAddress = $null
    if (![Net.IPAddress]::TryParse($address, [ref]$parsedAddress) -or $address -notmatch '^100\.(6[4-9]|[7-9][0-9]|1[01][0-9]|12[0-7])\.\d+\.\d+$') {
        throw 'Observer startup/firewall installation requires explicit Tailscale IPv4 addresses'
    }
}
$createdRule = $false
try {
if ($admin) {
    $ruleName = 'DWM157Observer-HTTP-Tailscale'
    if (Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue) { throw 'Observer firewall rule already exists; inspect it before installing' }
    New-NetFirewallRule -Name $ruleName -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP `
        -LocalAddress $observer.BindAddress -LocalPort $observer.Port -RemoteAddress $allowedPeers `
        -Program $Executable -Profile Any | Out-Null
    $createdRule = $true
}
$action = New-ScheduledTaskAction -Execute $Executable -WorkingDirectory $ConfigDirectory
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
if ($CurrentUserOnly) {
    $principal = New-ScheduledTaskPrincipal -UserId $identity.Name -LogonType Interactive -RunLevel Limited
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity.Name
} else {
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $trigger = New-ScheduledTaskTrigger -AtStartup
    $trigger.Delay = 'PT60S'
}
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal | Out-Null
Start-ScheduledTask -TaskName $TaskName
Get-ScheduledTask -TaskName $TaskName | Select-Object TaskName,State
} catch {
    $newTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($newTask) {
        Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    }
    if ($createdRule) { Remove-NetFirewallRule -Name 'DWM157Observer-HTTP-Tailscale' -ErrorAction SilentlyContinue }
    throw
}
