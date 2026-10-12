param(
    [Parameter(Mandatory = $true)][string]$Server,
    [Parameter(Mandatory = $true)][string]$Model,
    [string]$Cases = '',
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$serverPath = (Resolve-Path -LiteralPath $Server).Path
if (Test-Path -LiteralPath $serverPath -PathType Container) {
    $serverPath = Join-Path $serverPath $(if ($env:OS -eq 'Windows_NT') { 'llama-server.exe' } else { 'llama-server' })
}
if (!(Test-Path -LiteralPath $serverPath -PathType Leaf)) { throw 'Pass the llama-server executable or its directory.' }
$modelPath = (Resolve-Path -LiteralPath $Model).Path
$previousServer = $env:COMPOSA_LIVE_LLAMA_SERVER
$previousModel = $env:COMPOSA_LIVE_LLAMA_MODEL
$previousCases = $env:COMPOSA_LIVE_LLAMA_CASES
try {
    $env:COMPOSA_LIVE_LLAMA_SERVER = $serverPath
    $env:COMPOSA_LIVE_LLAMA_MODEL = $modelPath
    $env:COMPOSA_LIVE_LLAMA_CASES = $Cases
    $dotnetPath = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
    if (!(Test-Path -LiteralPath $dotnetPath)) { $dotnetPath = 'dotnet' }
    $filter = if ($Cases) { 'FullyQualifiedName~Production_agent_passes_basic_intermediate_and_advanced_scenarios' } else { 'FullyQualifiedName~AgentAcceptanceTests' }
    & $dotnetPath test (Join-Path $projectRoot 'tests/Composa.App.Tests') -c $Configuration --filter $filter --logger 'console;verbosity=normal'
    $result = $LASTEXITCODE
    Write-Host "Reports: tests/Composa.App.Tests/bin/$Configuration/net10.0/artifacts/assistant-evals"
    exit $result
}
finally {
    $env:COMPOSA_LIVE_LLAMA_SERVER = $previousServer
    $env:COMPOSA_LIVE_LLAMA_MODEL = $previousModel
    $env:COMPOSA_LIVE_LLAMA_CASES = $previousCases
}
