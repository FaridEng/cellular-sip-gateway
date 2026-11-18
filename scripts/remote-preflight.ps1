param(
    [string]$HostName = "dwm157-dest"
)

$ErrorActionPreference = "Stop"

$remoteCommand = @'
Write-Host '== Host =='
hostname
Write-Host '== D-Link PnP devices =='
Get-PnpDevice | Where-Object { $_.FriendlyName -like '*D-Link*' -or $_.InstanceId -like 'USB\VID_2001&PID_7600*' } | Select-Object Status,Class,FriendlyName,InstanceId | Format-Table -AutoSize
Write-Host '== COM ports =='
Get-CimInstance Win32_PnPEntity | Where-Object { $_.Name -match 'D-Link HSPADatacard|D-Link HSPA|COM\d+' } | Select-Object Name,PNPDeviceID | Format-Table -AutoSize
Write-Host '== D-Link processes =='
Get-Process | Where-Object { $_.ProcessName -match 'D-Link|Connection|UIMain|WirelessCard' } | Select-Object ProcessName,Id,Path | Format-Table -AutoSize
Write-Host '== Tailscale =='
tailscale ip -4
tailscale status --self
Write-Host '== .NET =='
dotnet --info
'@

$encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($remoteCommand))
ssh $HostName "powershell -NoProfile -ExecutionPolicy Bypass -EncodedCommand $encodedCommand"
