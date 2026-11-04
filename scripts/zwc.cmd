@echo off
rem zwc.cmd - cmd.exe entry point for zwcad-cooperate (M3-E1: native client, no PowerShell tax).
rem
rem Measured on this machine: `powershell -Command exit` alone costs ~1856 ms, so a PowerShell
rem wrapper can never reach the 600 ms command budget no matter how fast the daemon is. zwc.cmd now
rem calls bin\ZwcClient.exe (a small console exe, ~60-100 ms start) which speaks the same named-pipe
rem JSON protocol; scripts\zwc.ps1 stays available for complex argument construction and as the
rem bootstrap that compiles the client/helper (see --build-client).
rem
rem Measured 2026-11-04 (warm daemon, same machine): bin\ZwcClient.exe status = 108 ms median,
rem this cmd entry = 135 ms median, powershell -File zwc.ps1 = 2480 ms (PS startup ~1.9 s).
rem So: scripts\zwc.cmd (this file) or bin\ZwcClient.exe directly is the fast path; scripts\zwc.ps1
rem is kept for complex argument building / compatibility only.
rem
rem Fallback: if the client cannot be built, the original PowerShell wrapper runs instead, so the
rem command surface and the single-JSON-on-stdout contract never change.
setlocal
set "ROOT=%~dp0.."
set "CLIENT=%ROOT%\bin\ZwcClient.exe"
if not exist "%CLIENT%" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0zwc.ps1" --build-client >nul 2>&1
)
if not exist "%CLIENT%" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0zwc.ps1" %*
    exit /b %ERRORLEVEL%
)
"%CLIENT%" %*
exit /b %ERRORLEVEL%