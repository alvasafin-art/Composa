@echo off
setlocal
cd /d "%~dp0"
title Composa

set "COMPOSA_DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
if not exist "%COMPOSA_DOTNET%" set "COMPOSA_DOTNET=dotnet"
if not defined COMPOSA_LLAMA_SERVER (
  if exist "%~dp0..\..\llama-VULKAN\llama-server.exe" (
    set "COMPOSA_LLAMA_SERVER=%~dp0..\..\llama-VULKAN\llama-server.exe"
  ) else if exist "%~dp0..\..\llama\llama-server.exe" (
    set "COMPOSA_LLAMA_SERVER=%~dp0..\..\llama\llama-server.exe"
  )
)
if not defined COMPOSA_LLAMA_MODEL if exist "%~dp0..\..\Models\Qwen3.5-9B-Q4_K_M.gguf" set "COMPOSA_LLAMA_MODEL=%~dp0..\..\Models\Qwen3.5-9B-Q4_K_M.gguf"

rem Portable downloads contain the ready executable. No SDK or rebuild is needed.
if exist "%~dp0composa.exe" (
  start "" "%~dp0composa.exe" %*
  exit /b
)
if exist "%~dp0portable\composa.exe" (
  start "" "%~dp0portable\composa.exe" %*
  exit /b
)
if exist "%~dp0dist\portable-win-x64\composa.exe" (
  start "" "%~dp0dist\portable-win-x64\composa.exe" %*
  exit /b
)
rem Source checkout fallback; build products stay excluded from Git.
"%COMPOSA_DOTNET%" run --project "src\Composa.App\Composa.App.csproj" --configuration Release
if errorlevel 1 (
  echo.
  echo Composa could not start. Make sure the .NET 10 SDK is installed.
  pause
)
