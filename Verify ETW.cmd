@echo off
cd /d "%~dp0"
if exist "verify-etw.ps1" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0verify-etw.ps1"
) else (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\verify-etw.ps1"
)
