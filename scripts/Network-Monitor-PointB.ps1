$ErrorActionPreference = 'Stop'

$targets = @(
    [pscustomobject]@{ Name = 'Point B'; IP = '100.64.0.12' },
    [pscustomobject]@{ Name = 'Galaxy A07'; IP = '100.64.0.17' }
)

$timeoutMilliseconds = 1500
$intervalMilliseconds = 700
$buffer = New-Object byte[] 32
$pingOptions = New-Object System.Net.NetworkInformation.PingOptions(64, $false)
$pingClients = @($targets | ForEach-Object { New-Object System.Net.NetworkInformation.Ping })
$statistics = @(
    [pscustomobject]@{ Sent = 0; Lost = 0 },
    [pscustomobject]@{ Sent = 0; Lost = 0 }
)

function Get-DisplayResult {
    param(
        [System.Net.NetworkInformation.PingReply]$Reply,
        [pscustomobject]$Stats
    )

    $Stats.Sent++
    if ($null -ne $Reply -and $Reply.Status -eq [System.Net.NetworkInformation.IPStatus]::Success) {
        $result = '{0,4} ms' -f $Reply.RoundtripTime
        $color = if ($Reply.RoundtripTime -lt 150) { 'Green' } elseif ($Reply.RoundtripTime -lt 400) { 'Yellow' } else { 'Red' }
    }
    else {
        $Stats.Lost++
        $result = if ($null -eq $Reply) { 'ERROR' } else { $Reply.Status.ToString().ToUpperInvariant() }
        $color = 'Red'
    }

    $lossPercent = if ($Stats.Sent -eq 0) { 0 } else { 100.0 * $Stats.Lost / $Stats.Sent }
    [pscustomobject]@{
        Text = '{0,-13} loss {1,5:N1}% ({2}/{3})' -f $result, $lossPercent, $Stats.Lost, $Stats.Sent
        Color = $color
    }
}

function Write-Header {
    Write-Host ''
    Write-Host ('{0,-22}{1,-43}{2}' -f 'Time', 'Point B (100.64.0.12)', 'Galaxy A07 (100.64.0.17)') -ForegroundColor Cyan
    Write-Host ('-' * 106) -ForegroundColor DarkGray
}

$Host.UI.RawUI.WindowTitle = 'Point B and Galaxy A07 Network Monitor'
Clear-Host
Write-Host 'Continuous network monitor - press Ctrl+C to stop.' -ForegroundColor White
Write-Host 'Both targets are pinged concurrently; loss values are cumulative.' -ForegroundColor DarkGray
Write-Header

$row = 0
try {
    while ($true) {
        $tasks = @(
            $pingClients[0].SendPingAsync($targets[0].IP, $timeoutMilliseconds, $buffer, $pingOptions),
            $pingClients[1].SendPingAsync($targets[1].IP, $timeoutMilliseconds, $buffer, $pingOptions)
        )

        try {
            [System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]$tasks)
        }
        catch {
            # Individual faulted tasks are reported as ERROR below.
        }

        $results = for ($index = 0; $index -lt $tasks.Count; $index++) {
            $reply = if ($tasks[$index].Status -eq [System.Threading.Tasks.TaskStatus]::RanToCompletion) {
                $tasks[$index].Result
            }
            else {
                $null
            }
            Get-DisplayResult -Reply $reply -Stats $statistics[$index]
        }

        Write-Host ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss').PadRight(22)) -NoNewline
        Write-Host ($results[0].Text.PadRight(43)) -ForegroundColor $results[0].Color -NoNewline
        Write-Host $results[1].Text -ForegroundColor $results[1].Color

        $row++
        if (($row % 25) -eq 0) {
            Write-Header
        }

        Start-Sleep -Milliseconds $intervalMilliseconds
    }
}
finally {
    $pingClients | ForEach-Object { $_.Dispose() }
}
