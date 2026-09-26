@echo off
REM Launcher so the repo root can be used as the working directory:  .\d2vpn calibrate 3074
REM Prefers a Release build when one exists, otherwise falls back to Debug.

setlocal
set "ROOT=%~dp0"
set "REL=%ROOT%src\D2Vpn.Cli\bin\Release\net9.0-windows\d2vpn.exe"
set "DBG=%ROOT%src\D2Vpn.Cli\bin\Debug\net9.0-windows\d2vpn.exe"

if exist "%REL%" (
    "%REL%" %*
    exit /b %errorlevel%
)

if exist "%DBG%" (
    "%DBG%" %*
    exit /b %errorlevel%
)

echo d2vpn is not built yet. Run:  dotnet build
exit /b 1
