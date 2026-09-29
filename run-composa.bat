@echo off
setlocal
cd /d "%~dp0"
title Composa

set "COMPOSA_DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
if not exist "%COMPOSA_DOTNET%" set "COMPOSA_DOTNET=dotnet"

"%COMPOSA_DOTNET%" run --project "src\Composa.App\Composa.App.csproj" --configuration Release
if errorlevel 1 (
  echo.
  echo Composa could not start. Make sure the .NET 10 SDK is installed.
  pause
)
