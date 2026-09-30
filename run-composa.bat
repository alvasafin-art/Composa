@echo off
setlocal
cd /d "%~dp0"
title Composa

set "COMPOSA_DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
if not exist "%COMPOSA_DOTNET%" set "COMPOSA_DOTNET=dotnet"
if not defined COMPOSA_LLAMA_SERVER (
  if exist "%~dp0..\..\llama-VULKAN\llama-server.exe" (
    set "COMPOSA_LLAMA_SERVER=%~dp0..\..\llama-VULKAN\llama-server.exe"
  ) else (
    set "COMPOSA_LLAMA_SERVER=%~dp0..\..\llama\llama-server.exe"
  )
)
if not defined COMPOSA_LLAMA_MODEL set "COMPOSA_LLAMA_MODEL=%~dp0..\..\Models\Qwen3.5-9B-Q4_K_M.gguf"

"%COMPOSA_DOTNET%" run --project "src\Composa.App\Composa.App.csproj" --configuration Release
if errorlevel 1 (
  echo.
  echo Composa could not start. Make sure the .NET 10 SDK is installed.
  pause
)
