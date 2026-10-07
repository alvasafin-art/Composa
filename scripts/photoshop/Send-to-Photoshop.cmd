@echo off
powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "%~dp0Exchange.ps1" -Direction ToPhotoshop
if errorlevel 1 (
  echo Image exchange failed. Open Composa and Photoshop, then try again.
  pause
)
