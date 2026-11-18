param([Parameter(Mandatory)][string]$ConfigPath)
$ErrorActionPreference = 'Stop'
$settings = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$endpoint = [uri]$settings.ObserverUrl
$ip = $null
$private = [Net.IPAddress]::TryParse($endpoint.Host, [ref]$ip) -and
    ($endpoint.Host -match '^(100\.(6[4-9]|[7-9][0-9]|1[01][0-9]|12[0-7])\.|10\.|192\.168\.|127\.)')
if ($endpoint.Scheme -ne 'https' -and !($endpoint.Scheme -eq 'http' -and $private)) { throw 'Observer URL must use HTTPS or a private/Tailscale IP' }
if ([string]::IsNullOrWhiteSpace($settings.Token)) { throw 'Reporter token is missing' }
$version = & 'C:\Program Files\Tailscale\tailscale.exe' version --json --daemon | Out-String | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Tailscale version query failed' }
$body = @{ name=$env:COMPUTERNAME; installedVersion=$version.short; runningVersion=$version.daemonLong } | ConvertTo-Json -Compress
Invoke-RestMethod -Method Post -Uri ($settings.ObserverUrl.TrimEnd('/') + '/api/v1/versions/report') `
    -Headers @{'X-Reporter-Token'=[string]$settings.Token} -Body $body -ContentType 'application/json' `
    -MaximumRedirection 0 -TimeoutSec 10 | Select-Object recorded
