<#
.SYNOPSIS
    Finds a block/release event in a capture and reports what got through.

.DESCRIPTION
    Built for the experiment "toggle a limiter module on, hold, toggle off, capture throughout".
    Produces the three things that matter:

      1. A per-second UL/DL timeline, so the block window is visible.
      2. The packet lengths seen in steady state vs during the block. If a limiter is doing
         selective pass-through, these sets are disjoint - that difference IS the signature.
      3. Millisecond detail of the release burst, showing whether the drain is paced or dumped.

    Direction is inferred from the remote port, so it works for IPv4 and IPv6 alike:
    dstport == Port means UL (you -> peer), srcport == Port means DL.

.PARAMETER Label
    Capture label from Start-Capture.ps1.

.PARAMETER Port
    Remote port to analyse. 3074, 7500, 30000, or a 27k port.

.PARAMETER Quiet
    Suppress the timeline bars; just report the phases.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Label,
    [Parameter(Mandatory = $true)][int]$Port,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
# Force invariant culture: under a locale that uses a comma as the decimal separator the default
# formats decimals as '12,629', which is unreadable next to byte counts and breaks copy-paste.
[System.Threading.Thread]::CurrentThread.CurrentCulture = [System.Globalization.CultureInfo]::InvariantCulture
$tshark = 'C:\Program Files\Wireshark\tshark.exe'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$pcap = Join-Path $repo "captures\$Label.pcapng"
if (-not (Test-Path $pcap)) { throw "No capture at $pcap" }

$rows = & $tshark -r $pcap -Y "udp.port==$Port || tcp.port==$Port" `
    -T fields -e frame.time_relative -e udp.srcport -e udp.dstport `
    -e tcp.srcport -e tcp.dstport -e frame.len -e data.len 2>$null

$pkts = foreach ($line in $rows) {
    $f = $line -split "`t"
    if ($f[1]) { $sp = [int]$f[1]; $dp = [int]$f[2] } elseif ($f[3]) { $sp = [int]$f[3]; $dp = [int]$f[4] } else { continue }
    # Len is the transport PAYLOAD, not the frame. Frame length includes a link-layer header
    # that differs between a Raw IP tunnel and Ethernet, so a frame-based signature would break
    # when the VPN is toggled. Payload is what WinDivert reports to a module, and it is invariant.
    [pscustomobject]@{
        T = [double]$f[0]
        Dir = $(if ($dp -eq $Port) { 'UL' } else { 'DL' })
        Len = $(if ($f[6]) { [int]$f[6] } else { 0 })
        Frame = [int]$f[5]
    }
}
if (-not $pkts) { throw "No traffic on port $Port in $Label" }

# --- per-second timeline ------------------------------------------------------
$maxSec = [int][math]::Floor(($pkts | Measure-Object T -Maximum).Maximum)
$sec = @{}
foreach ($p in $pkts) {
    $s = [int][math]::Floor($p.T)
    if (-not $sec.ContainsKey($s)) { $sec[$s] = @{ UL = 0; DL = 0 } }
    $sec[$s][$p.Dir]++
}

# Baseline = median UL/s over the first quarter of the capture.
$early = 0..([int]($maxSec / 4)) | ForEach-Object { if ($sec.ContainsKey($_)) { $sec[$_].UL } else { 0 } }
$baseline = ($early | Sort-Object)[[int]($early.Count / 2)]
if ($baseline -lt 1) { $baseline = 1 }

if (-not $Quiet) {
    Write-Host "`n=== port $Port timeline (baseline ~$baseline UL/s) ===" -ForegroundColor Cyan
    foreach ($s in 0..$maxSec) {
        $u = 0; $d = 0
        if ($sec.ContainsKey($s)) { $u = $sec[$s].UL; $d = $sec[$s].DL }
        $bar = '#' * [math]::Min(40, [int]($u * 20 / $baseline))
        $note = ''
        if ($u -eq 0) { $note = '  <== no uplink' }
        elseif ($u -gt $baseline * 2) { $note = '  <== BURST' }
        '{0,3}s  UL{1,5} DL{2,5}  {3,-40}{4}' -f $s, $u, $d, $bar, $note
    }
}

# --- locate the block window --------------------------------------------------
$blocked = $pkts | Where-Object { $_.Dir -eq 'UL' } | Group-Object { [int][math]::Floor($_.T) }
$zeroSecs = 0..$maxSec | Where-Object { -not $sec.ContainsKey($_) -or $sec[$_].UL -eq 0 }
if (-not $zeroSecs) { Write-Host "`nNo uplink gap found - was a module actually toggled?" -ForegroundColor Yellow; return }

$blockStart = ($zeroSecs | Measure-Object -Minimum).Minimum
$blockEnd = ($zeroSecs | Measure-Object -Maximum).Maximum + 1
Write-Host "`n=== block window: s$blockStart - s$blockEnd ($($blockEnd - $blockStart)s) ===" -ForegroundColor Cyan

$steady = $pkts | Where-Object { $_.Dir -eq 'UL' -and $_.T -lt $blockStart }
$during = $pkts | Where-Object { $_.Dir -eq 'UL' -and $_.T -ge $blockStart -and $_.T -le $blockEnd }

$steadyLens = $steady.Len | Sort-Object -Unique
$duringLens = $during.Len | Sort-Object -Unique
$overlap = $duringLens | Where-Object { $steadyLens -contains $_ }

Write-Host "steady-state UL payloads : $($steadyLens -join ', ')"
Write-Host "escaped during block     : $($duringLens -join ', ')" -ForegroundColor Green
if ($overlap) {
    Write-Host "OVERLAP                 : $($overlap -join ', ')" -ForegroundColor Yellow
    Write-Host "  Overlapping lengths mean the block is not purely selective."
} else {
    Write-Host "NO OVERLAP - selective pass-through confirmed." -ForegroundColor Green
    Write-Host "  These payload sizes are this machine's signature for port $Port."
}

Write-Host "`nescape detail:"
foreach ($p in ($during | Sort-Object T)) { '  t={0,8:N3}  payload={1,-6} frame={2}' -f $p.T, $p.Len, $p.Frame }

# --- release burst ------------------------------------------------------------
$after = $pkts | Where-Object { $_.Dir -eq 'UL' -and $_.T -gt $blockEnd - 1 } | Sort-Object T | Select-Object -First 80
if ($after) {
    Write-Host "`n=== release burst ===" -ForegroundColor Cyan
    $prev = $null; $sameMs = 0; $first = $after[0].T
    foreach ($p in $after) {
        $gap = 0.0
        if ($null -ne $prev) { $gap = ($p.T - $prev) * 1000 }
        if ($gap -lt 1.0) { $sameMs++ }
        $prev = $p.T
    }
    Write-Host ("{0} packets injected within 1 ms of a neighbour, starting t={1:N3}" -f $sameMs, $first)
    if ($sameMs -gt 10) {
        Write-Host "Drain is a HARD DUMP, not paced." -ForegroundColor Green
    } else {
        Write-Host "Drain appears paced - check gaps below." -ForegroundColor Yellow
    }
    $prev = $null
    foreach ($p in ($after | Select-Object -First 12)) {
        $gap = 0.0; if ($null -ne $prev) { $gap = ($p.T - $prev) * 1000 }
        '  t={0,8:N3}  payload={1,-6} gap={2,7:N1} ms' -f $p.T, $p.Len, $gap
        $prev = $p.T
    }
}
