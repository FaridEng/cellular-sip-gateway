param(
    [string]$Path = 'C:\DWM157Gateway\data\call-history.json',
    [ValidateRange(1, 500)]
    [int]$Count = 10
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Path)) {
    throw "Call history does not exist yet: $Path"
}

@(Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json) |
    Sort-Object RequestTimestamp -Descending |
    Select-Object -First $Count CorrelationId, Direction, RemoteNumber, SipPeer, Status,
        RequestTimestamp, ConnectTimestamp, EndTimestamp, EndOrigin, EndReason, Codec, Media
