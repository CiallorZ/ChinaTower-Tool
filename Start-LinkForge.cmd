@echo off
setlocal
cd /d "%~dp0"

where pwsh.exe >nul 2>nul
if errorlevel 1 (
  echo LinkForge requires PowerShell 7 ^(pwsh.exe^).
  echo Install PowerShell 7, or run scripts\run.ps1 from PowerShell.
  exit /b 1
)

pwsh.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\run.ps1" -BuildIfMissing %*
exit /b %errorlevel%
