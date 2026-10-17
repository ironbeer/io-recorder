@echo off
cd /d "%~dp0"
if exist "dist\IORecorder-win-x64\IORecorder.exe" (
  start "" "dist\IORecorder-win-x64\IORecorder.exe"
  exit /b 0
)
echo Build the application with scripts\build.ps1 first.
pause
