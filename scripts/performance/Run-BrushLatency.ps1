[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DataDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9-]+$')][string]$Label,
    [ValidateRange(1, 50)][int]$Repeat = 10,
    [switch]$NoBuild,
    [string]$Runner
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'BrushLatency/BrushLatency.csproj'
if (-not $Runner) {
    $Runner = Join-Path $PSScriptRoot 'BrushLatency/bin/Release/net10.0/Composa.App.Tests.dll'
}
if (-not $NoBuild) {
    & dotnet build $project -c Release -m:1 -p:CanvasPerformance=true
    if ($LASTEXITCODE -ne 0) { throw 'Benchmark build failed.' }
}
$results = Join-Path ([IO.Path]::GetFullPath($DataDirectory)) $Label
[void][IO.Directory]::CreateDirectory($results)
foreach ($i in 1..$Repeat) {
    foreach ($case in 'simple', 'layered') {
        $output = Join-Path $results "$case-$i.json"
        if (Test-Path -LiteralPath $output) { continue }
        Write-Host "$Label $case $i/$Repeat"
        & dotnet $Runner $case $output
        if ($LASTEXITCODE -ne 0) { throw "Replay failed for $case." }
    }
}
