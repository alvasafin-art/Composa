@echo off
setlocal
cd /d "%~dp0"
if not defined COMPOSA_LLAMA_SERVER (
  if exist "%~dp0..\..\llama-VULKAN\llama-server.exe" (
    set "COMPOSA_LLAMA_SERVER=%~dp0..\..\llama-VULKAN\llama-server.exe"
  ) else if exist "%~dp0..\..\llama\llama-server.exe" (
    set "COMPOSA_LLAMA_SERVER=%~dp0..\..\llama\llama-server.exe"
  )
)
if not defined COMPOSA_LLAMA_MODEL if exist "%~dp0..\..\Models\Qwen3.5-9B-Q4_K_M.gguf" set "COMPOSA_LLAMA_MODEL=%~dp0..\..\Models\Qwen3.5-9B-Q4_K_M.gguf"
if not exist "%~dp0dist\feedback-preview-win-x64\composa.exe" (
  echo The feedback preview build is missing.
  pause
  exit /b 1
)
start "" "%~dp0dist\feedback-preview-win-x64\composa.exe" %*
