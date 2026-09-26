<#
.SYNOPSIS
    Summarises a capture: which remote ports carried traffic, in which direction, owned by whom.

.DESCRIPTION
    Joins <label>.pcapng against <label>.sockets to attribute each conversation to a process.
    Direction is expressed from your machine's point of view:
        UL = uplink   = packets you sent
        DL = downlink = packets you received

.PARAMETER Label
    The capture label passed to Start-Capture.ps1.

.PARAMETER Process
    Only show ports owned by this process. Default 'destiny2'. Use '*' for everything.

.PARAMETER Lengths
    Also print a packet-length histogram per port. Length is the strongest fingerprint we have
    for identifying protocol messages, since the payload is encrypted.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Label,
    [string]$Process = 'destiny2',
    [switch]$Lengths
)

$ErrorActionPreference = 'Stop'

$tshark = 'C:\Program Files\Wireshark\tshark.exe'
if (-not (Test-Path $tshark)) { throw "tshark not found at $tshark" }

$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$outDir = Join-Path $repo 'captures'
$pcap = Join-Path $outDir "$Label.pcapng"
$sock = Join-Path $outDir "$Label.sockets"
if (-not (Test-Path $pcap)) { throw "No capture at $pcap" }

# socket ownership lookup: "UDP 49713" -> "destiny2"
$owner = @{}
if (Test-Path $sock) {
    foreach ($line in (Get-Content $sock | Select-Object -Skip 1)) {
        $f = $line -split "`t"
        if ($f.Count -ge 3) { $owner["$($f[0]) $($f[1])"] = $f[2] }
    }
} else {
    Write-Warning "No socket snapshot at $sock - ports cannot be attributed to a process."
}

$fields = @('-T','fields','-e','ip.src','-e','ip.dst','-e','udp.srcport','-e','udp.dstport',
            '-e','tcp.srcport','-e','tcp.dstport','-e','frame.len')
$raw = & $tshark -r $pcap @fields 2>$null

# The local IP is whichever address appears in essentially every packet.
$ipTally = @{}
foreach ($line in $raw) {
    $f = $line -split "`t"
    foreach ($ip in @($f[0], $f[1])) { if ($ip) { $ipTally[$ip] = [int]$ipTally[$ip] + 1 } }
}
$localIp = ($ipTally.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1).Key
Write-Host "Local address detected as $localIp" -ForegroundColor Cyan

$agg = @{}
foreach ($line in $raw) {
    $f = $line -split "`t"
    if ($f[2]) { $proto = 'UDP'; $sp = $f[2]; $dp = $f[3] }
    elseif ($f[4]) { $proto = 'TCP'; $sp = $f[4]; $dp = $f[5] }
    else { continue }
    $len = [int]$f[6]

    if ($f[0] -eq $localIp) { $dir = 'UL'; $lport = $sp; $rport = $dp; $rip = $f[1] }
    else                    { $dir = 'DL'; $lport = $dp; $rport = $sp; $rip = $f[0] }

    $own = $owner["$proto $lport"]
    if (-not $own) { $own = '(unknown)' }

    $key = "$proto|$rport|$own"
    if (-not $agg.ContainsKey($key)) {
        $agg[$key] = [pscustomobject]@{
            Proto = $proto; Port = [int]$rport; Process = $own
            Packets = 0; Bytes = 0; UL = 0; DL = 0
            Peers = (New-Object System.Collections.Generic.HashSet[string])
            Lens = @{}
        }
    }
    $a = $agg[$key]
    $a.Packets++; $a.Bytes += $len
    if ($dir -eq 'UL') { $a.UL++ } else { $a.DL++ }
    [void]$a.Peers.Add($rip)
    $a.Lens["$dir $len"] = [int]$a.Lens["$dir $len"] + 1
}

$rows = $agg.Values
if ($Process -ne '*') { $rows = $rows | Where-Object { $_.Process -like $Process } }
$rows = $rows | Sort-Object Packets -Descending

$rows | Format-Table Proto, Port, Process, Packets, Bytes, UL, DL,
    @{ n = 'Peers'; e = { $_.Peers.Count } } -AutoSize

if ($Lengths) {
    foreach ($r in $rows) {
        Write-Host "`n--- $($r.Proto) $($r.Port) ($($r.Process)) top packet lengths ---" -ForegroundColor Yellow
        $r.Lens.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 12 |
            ForEach-Object {
                $p = $_.Key -split ' '
                [pscustomobject]@{ Dir = $p[0]; Bytes = [int]$p[1]; Count = $_.Value }
            } | Format-Table -AutoSize
    }
}
