<#
.SYNOPSIS
    Captures Destiny 2 network traffic plus a snapshot of which process owns which socket.

.DESCRIPTION
    Two artifacts are produced per run, sharing one label:
      captures\<label>.pcapng    - the packets themselves
      captures\<label>.sockets   - port -> owning process, sampled during the capture

    The socket snapshot matters because a capture alone cannot tell you which program sent a
    packet. Windows knows (it owns the socket table); the wire does not. Joining the two is how
    we prove that UDP 27056 is Destiny and not Steam, which shares that port range.

.PARAMETER Label
    Name for this run. Use the scenario, e.g. 'orbit', 'patrol', 'fireteam-join', 'strike'.

.PARAMETER Seconds
    Capture duration. Default 60.

.PARAMETER Filter
    Optional BPF *capture* filter. Anything excluded here is gone forever, so leave it empty
    for survey runs and only narrow it once you know what you are looking for.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Label,
    [int]$Seconds = 60,
    [string]$Filter = ''
)

$ErrorActionPreference = 'Stop'

$wireshark = 'C:\Program Files\Wireshark'
$dumpcap = Join-Path $wireshark 'dumpcap.exe'
if (-not (Test-Path $dumpcap)) { throw "dumpcap not found at $dumpcap" }

$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$outDir = Join-Path $repo 'captures'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

# --- Pick the interface the game actually uses -------------------------------
# Prefer the local address of a live destiny2 TCP connection: that is ground truth and
# automatically follows a VPN tunnel. Fall back to the lowest-metric default route.
# The game may hold IPv4 and/or IPv6 connections - both land on the same adapter, so try each
# local address until one resolves. Do not assume an address family.
$alias = $null
$localIp = $null
$game = Get-Process destiny2 -ErrorAction SilentlyContinue
if ($game) {
    $candidates = Get-NetTCPConnection -OwningProcess $game.Id -State Established -ErrorAction SilentlyContinue |
                  Select-Object -ExpandProperty LocalAddress -Unique
    foreach ($ip in $candidates) {
        $a = (Get-NetIPAddress -IPAddress $ip -ErrorAction SilentlyContinue | Select-Object -First 1).InterfaceAlias
        if ($a) { $alias = $a; $localIp = $ip; break }
    }
}
if ($alias) {
    Write-Host "Interface chosen from live destiny2 connection ($localIp)" -ForegroundColor Green
} else {
    Write-Host "No usable destiny2 connection; using default route" -ForegroundColor Yellow
    $alias = (Get-NetRoute -DestinationPrefix '0.0.0.0/0' |
              Where-Object { (Get-NetIPAddress -InterfaceIndex $_.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue).IPAddress -notlike '169.254.*' } |
              Sort-Object RouteMetric | Select-Object -First 1).InterfaceAlias
}
if (-not $alias) { throw 'Could not determine a capture interface.' }

$guid = (Get-NetAdapter -Name $alias).InterfaceGuid
$device = "\Device\NPF_$guid"
Write-Host "Capturing on '$alias'  $device" -ForegroundColor Cyan

# --- Snapshot socket ownership ----------------------------------------------
$socketPath = Join-Path $outDir "$Label.sockets"
$rows = New-Object System.Collections.Generic.List[string]
$rows.Add("proto`tlocalport`tprocess`tpid")
foreach ($e in (Get-NetUDPEndpoint -ErrorAction SilentlyContinue)) {
    $n = (Get-Process -Id $e.OwningProcess -ErrorAction SilentlyContinue).ProcessName
    $rows.Add("UDP`t$($e.LocalPort)`t$n`t$($e.OwningProcess)")
}
foreach ($c in (Get-NetTCPConnection -ErrorAction SilentlyContinue)) {
    $n = (Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue).ProcessName
    $rows.Add("TCP`t$($c.LocalPort)`t$n`t$($c.OwningProcess)")
}
$rows | Set-Content -Path $socketPath -Encoding utf8
Write-Host "Socket snapshot: $($rows.Count - 1) entries -> $socketPath"

# --- Capture -----------------------------------------------------------------
$pcapPath = Join-Path $outDir "$Label.pcapng"
# -q keeps dumpcap from streaming a packet counter to stderr. Under PowerShell 5.1 a native
# command's stderr gets wrapped in ErrorRecords, which turns harmless progress output into a
# terminating error the moment anything pipes this script.
$dumpArgs = @('-q', '-i', $device, '-a', "duration:$Seconds", '-w', $pcapPath)
if ($Filter) { $dumpArgs += @('-f', $Filter); Write-Host "Capture filter: $Filter" }

Write-Host "Capturing for $Seconds seconds..." -ForegroundColor Cyan
& $dumpcap @dumpArgs
Write-Host "Wrote $pcapPath" -ForegroundColor Green
Write-Host "Analyze with: .\Get-PortBreakdown.ps1 -Label $Label"
