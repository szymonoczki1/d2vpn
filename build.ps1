<#
.SYNOPSIS
    Builds d2vpn: runs the tests, publishes the app, and makes an installer and a zip.

.DESCRIPTION
    One script so that a release is reproducible rather than a sequence somebody remembers.

    The publish is self-contained. That costs about 130 MB against roughly 2 MB
    framework-dependent, and it is worth it: the alternative is telling everyone who wants to
    try this to go and install a .NET runtime first, which is where most people stop.

    The installer step needs Inno Setup. If it is not there the script says where to get it and
    still produces the zip, because the zip is the fallback distribution and should never depend
    on a tool being present.

.PARAMETER SkipTests
    Skip the test run. For iterating on packaging only - a release should not use this.

.EXAMPLE
    .\build.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publish = Join-Path $root 'publish'
$dist = Join-Path $root 'dist'

function Step($text) {
    Write-Host ''
    Write-Host "==> $text" -ForegroundColor Cyan
}

# The version the installer and the zip are named after. Single source: Directory.Build.props,
# so bumping it in one place renames everything downstream.
$props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
$version = [regex]::Match($props, '<Version>([^<]+)</Version>').Groups[1].Value

if (-not $version) {
    throw 'No <Version> in Directory.Build.props - the installer needs one to name its output.'
}

Write-Host "d2vpn $version" -ForegroundColor Green

if (-not $SkipTests) {
    Step 'Tests'
    dotnet test (Join-Path $root 'd2vpn.sln') -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed - not packaging a build that does not pass.' }
}

Step 'Publish (self-contained, win-x64)'

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

# Both front ends land in one folder: the window is what people use, the CLI is what the
# measurement commands live in, and they share every assembly anyway.
foreach ($project in @('src\D2Vpn.Ui\D2Vpn.Ui.csproj', 'src\D2Vpn.Cli\D2Vpn.Cli.csproj')) {
    dotnet publish (Join-Path $root $project) `
        -c Release -r win-x64 --self-contained true `
        -o $publish --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
}

# Into the publish folder rather than added separately by each packager, so the zip and the
# installer carry the same thing and a licence cannot go missing from one of them.
Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE') $publish

$size = '{0:N0} MB' -f ((Get-ChildItem $publish -Recurse | Measure-Object Length -Sum).Sum / 1MB)
Write-Host "  $((Get-ChildItem $publish).Count) files, $size"

if (-not (Test-Path (Join-Path $publish 'd2vpn-ui.exe'))) {
    throw 'd2vpn-ui.exe is missing from the publish output.'
}

Step 'Zip'

New-Item $dist -ItemType Directory -Force | Out-Null
$zip = Join-Path $dist "d2vpn-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$publish\*" -DestinationPath $zip
Write-Host "  $zip"

Step 'Installer'

# Per-user and machine-wide installs both land here, so whichever way Inno was installed works.
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Host '  Inno Setup not found - skipping the installer, the zip above still works.' -ForegroundColor Yellow
    Write-Host '  To build it:  winget install JRSoftware.InnoSetup'
    return
}

& $iscc "/DAppVersion=$version" "/O$dist" (Join-Path $root 'installer\d2vpn.iss') | Out-String -Stream |
    Select-String -Pattern 'Successful|Error|error' | ForEach-Object { Write-Host "  $_" }

if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }

Step 'Done'
Get-ChildItem $dist | ForEach-Object {
    Write-Host ('  {0}  ({1:N0} MB)' -f $_.Name, ($_.Length / 1MB))
}
