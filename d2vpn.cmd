@echo off
REM Launcher so the repo root can be used as the working directory:  .\d2vpn calibrate 3074
REM
REM Prefers DEBUG, which is what every `dotnet build` produces and therefore what anyone working
REM in this repository has just built. It used to prefer Release, which silently served a stale
REM binary for three days after one `dotnet publish` - `.\d2vpn instance` reported an unknown
REM command while the source and the tests had it.
REM
REM A published build is run from its own output folder rather than through this, so preferring
REM Debug costs that case nothing.

setlocal
set "ROOT=%~dp0"
set "DBG=%ROOT%src\D2Vpn.Cli\bin\Debug\net9.0-windows\d2vpn.exe"
set "REL=%ROOT%src\D2Vpn.Cli\bin\Release\net9.0-windows\d2vpn.exe"

if exist "%DBG%" (
    "%DBG%" %*
    exit /b %errorlevel%
)

if exist "%REL%" (
    "%REL%" %*
    exit /b %errorlevel%
)

echo d2vpn is not built yet. Run:  dotnet build
exit /b 1
