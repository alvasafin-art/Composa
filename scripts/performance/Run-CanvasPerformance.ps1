[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DataDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9-]+$')][string]$Label,
    [ValidateSet('card', 'banner', 'photo', 'tabs', 'brush8', 'brush16', 'interaction', 'brush-preview')][string[]]$Scenario = @('card', 'banner', 'photo', 'tabs'),
    [ValidateRange(1, 50)][int]$Repeat = 5,
    [switch]$GenerateFixtures,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$suite = Join-Path $repo 'tests/Composa.App.Tests'
$runner = Join-Path $suite 'bin/Release/net10.0/Composa.App.Tests.dll'
$data = [IO.Path]::GetFullPath($DataDirectory)
$fixtureDir = Join-Path $data 'fixtures'
$results = Join-Path $data $Label
[void][IO.Directory]::CreateDirectory($results)

if (-not $NoBuild) {
    & dotnet build (Join-Path $suite 'Composa.App.Tests.csproj') -c Release -m:1 -p:CanvasPerformance=true
    if ($LASTEXITCODE -ne 0) { throw 'Benchmark build failed.' }
}

function Invoke-Replay([string]$Case, [string]$OutputPath) {
    $env:COMPOSA_PERF_CASE = $Case
    $env:COMPOSA_PERF_OUTPUT = $OutputPath
    & dotnet $runner -class '*CanvasPerformanceBench' -nocolor
    if ($LASTEXITCODE -ne 0) { throw "Replay failed for $Case." }
}

$env:COMPOSA_PERF_FIXTURES = $fixtureDir
$env:COMPOSA_PERF_PHOTO = Join-Path $repo 'docs/images/studio-workspace.png'
if ($GenerateFixtures) {
    Invoke-Replay 'fixtures' ''
}
if ($Scenario | Where-Object { $_ -notin 'brush8', 'brush16', 'interaction', 'brush-preview' }) {
    foreach ($file in 'card.cmps', 'banner.cmps', 'photo.cmps') {
        if (-not (Test-Path -LiteralPath (Join-Path $fixtureDir $file))) { throw "Missing fixture $file." }
    }

    Get-FileHash -Path (Join-Path $fixtureDir '*.cmps') -Algorithm SHA256 |
        Select-Object @{Name = 'Fixture'; Expression = { [IO.Path]::GetFileName($_.Path) }}, Hash |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $results 'fixtures.json') -Encoding utf8
}

foreach ($case in $Scenario) {
    $runs = if ($case -eq 'tabs') { 1 } else { $Repeat }
    for ($i = 1; $i -le $runs; $i++) {
        $output = Join-Path $results "$case-$i.json"
        if (Test-Path -LiteralPath $output) { continue }
        Write-Host "$Label $case $i/$runs"
        Invoke-Replay $case $output
        if (-not (Test-Path -LiteralPath $output)) { throw "The replay did not write $output." }
    }
}
