param(
    [string]$AllowedPeer = "100.64.0.16",
    [string]$InterfaceAlias = "Tailscale"
)

$ErrorActionPreference = "Stop"

New-NetFirewallRule -DisplayName "DWM157Gateway-SIP-UDP" `
    -Direction Inbound -Action Allow -Protocol UDP -LocalPort 5060 `
    -RemoteAddress $AllowedPeer -InterfaceAlias $InterfaceAlias -Profile Any `
    -ErrorAction SilentlyContinue | Out-Null

New-NetFirewallRule -DisplayName "DWM157Gateway-RTP-UDP" `
    -Direction Inbound -Action Allow -Protocol UDP -LocalPort 40000-40020 `
    -RemoteAddress $AllowedPeer -InterfaceAlias $InterfaceAlias -Profile Any `
    -ErrorAction SilentlyContinue | Out-Null

Get-NetFirewallRule -DisplayName "DWM157Gateway-*" |
    Select-Object DisplayName, Enabled, Direction, Action, Profile
