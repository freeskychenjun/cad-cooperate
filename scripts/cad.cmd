@echo off
rem cad.cmd - cmd.exe entry point for cad-cooperate (M3-E1: native client, no PowerShell tax).
rem
rem Measured on this machine: `powershell -Command exit` alone costs ~1856 ms, so a PowerShell
rem wrapper can never reach the 600 ms command budget no matter how fast the daemon is. cad.cmd now
rem calls bin\CadClient.exe (a small console exe, ~60-100 ms start) which speaks the same named-pipe
rem JSON protocol; scripts\cad.ps1 stays available for complex argument construction and as the
rem bootstrap that compiles the client/helper (see --build-client).
rem
rem Measured 2026-11-04 (warm daemon, same machine): bin\CadClient.exe status = 108 ms median,
rem this cmd entry = 135 ms median, powershell -File cad.ps1 = 2480 ms (PS startup ~1.9 s).
rem So: scripts\cad.cmd (this file) or bin\CadClient.exe directly is the fast path; scripts\cad.ps1
rem is kept for complex argument building / compatibility only.
rem
rem Fallback: if the client cannot be built, the original PowerShell wrapper runs instead, so the
rem command surface and the single-JSON-on-stdout contract never change.
setlocal
set "ROOT=%~dp0.."
set "CLIENT=%ROOT%\bin\CadClient.exe"
if not exist "%CLIENT%" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0cad.ps1" --build-client >nul 2>&1
)
if not exist "%CLIENT%" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0cad.ps1" %*
    exit /b %ERRORLEVEL%
)
"%CLIENT%" %*
exit /b %ERRORLEVEL%