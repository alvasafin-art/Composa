[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DataDirectory,
    [string]$Main = 'fde9ab4',
    [string]$RoundStart = '60fc816',
    [string]$Final = 'HEAD',
    [ValidateSet('Build', 'Run', 'All')][string]$Step = 'All',
    [string]$RestoreSource,
    [string]$Photo,
    [switch]$SkipNative,
    [switch]$SkipGpu
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$data = [IO.Path]::GetFullPath($DataDirectory)
[void][IO.Directory]::CreateDirectory($data)
$dotnet = (Get-Command dotnet -CommandType Application).Source

function Invoke-Checked([string]$Tool, [string[]]$Arguments) {
    & $Tool @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Tool failed ($LASTEXITCODE): $($Arguments[0..([Math]::Min(2, $Arguments.Count - 1))] -join ' ')" }
}

function Resolve-Commit([string]$Reference) {
    $sha = & git -C $repo rev-parse --verify --end-of-options "$Reference^{commit}"
    if ($LASTEXITCODE -ne 0 -or $sha -notmatch '^[a-f0-9]{40}$') { throw "Invalid commit $Reference" }
    return $sha
}

$versions = [ordered]@{
    main = Resolve-Commit $Main
    start = Resolve-Commit $RoundStart
    final = Resolve-Commit $Final
}
$manifest = Join-Path $data 'commits.json'
if (Test-Path -LiteralPath $manifest) {
    $existing = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json -AsHashtable
    foreach ($key in $versions.Keys) {
        if ($existing[$key] -ne $versions[$key]) { throw 'Use a new DataDirectory when the commits change.' }
    }
} else {
    $versions | ConvertTo-Json | Set-Content -LiteralPath $manifest -Encoding utf8
}

function Runner([string]$Version, [string]$Kind) {
    $assembly = if ($Kind -eq 'core') { 'CompositorLatency.dll' } else { 'Composa.App.Tests.dll' }
    Join-Path $data "bin/$Version/$Kind/$assembly"
}

function Build-Variant([string]$Version) {
    $tree = Join-Path $data "sources/$Version"
    if (!(Test-Path -LiteralPath (Join-Path $tree 'src/Composa.Core/Composa.Core.csproj'))) {
        [void][IO.Directory]::CreateDirectory($tree)
        $archive = Join-Path $data "$Version.tar"
        Invoke-Checked git @('-C', $repo, 'archive', '--format=tar', "--output=$archive", $versions[$Version])
        Invoke-Checked tar @('-xf', $archive, '-C', $tree)
    }
    foreach ($subdirectory in 'BrushLatency', 'CompositorLatency') {
        $target = Join-Path $tree "scripts/performance/$subdirectory"
        [void][IO.Directory]::CreateDirectory($target)
        Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot $subdirectory) -File | Where-Object Extension -In '.cs', '.csproj' |
            ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $target -Force }
    }
    foreach ($file in 'CanvasPerformanceBench.cs', 'AutomationWorkflowBench.cs') {
        Copy-Item -LiteralPath (Join-Path $repo "tests/Composa.App.Tests/$file") -Destination (Join-Path $tree 'tests/Composa.App.Tests') -Force
    }
    # Git archives omit the fetched model. MSBuild copies this file even though this suite never invokes it.
    $model = 'src/Composa.Core/Models/vitmatte_small.onnx'
    if (!(Test-Path -LiteralPath (Join-Path $tree $model))) {
        if (!(Test-Path -LiteralPath (Join-Path $repo $model))) { throw 'Fetch the pinned models with scripts/models/fetch.sh before building.' }
        Copy-Item -LiteralPath (Join-Path $repo $model) -Destination (Join-Path $tree $model)
    }
    # Old builds already mark the same draw boundary. Add only the subscription API used by the common runner.
    $trace = Join-Path $tree 'src/Composa.Core/Rendering/CanvasPerformanceTrace.cs'
    $code = Get-Content -LiteralPath $trace -Raw
    if (!$code.Contains('event Action<long>? CanvasDrawCompleted')) {
        $old = 'public static void FrameCompleted() => Interlocked.Exchange(ref frameAt, Stopwatch.GetTimestamp());'
        if (!$code.Contains($old)) { throw "No compatible draw marker at $($versions[$Version])." }
        $shim = @'
public static event Action<long>? CanvasDrawCompleted;
    public static void FrameCompleted()
    {
        var now = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref frameAt, now);
        CanvasDrawCompleted?.Invoke(now);
    }
'@
        [IO.File]::WriteAllText($trace, $code.Replace($old, $shim))
    }
    $projects = [ordered]@{ native = 'scripts/performance/BrushLatency'; core = 'scripts/performance/CompositorLatency'; tests = 'tests/Composa.App.Tests' }
    foreach ($kind in $projects.Keys) {
        Write-Host "Build $Version / $kind at $($versions[$Version])"
        $buildArguments = @('build', (Join-Path $tree $projects[$kind]), '-c', 'Release', '-p:CanvasPerformance=true', '-m:1', '-nr:false', '-p:UseSharedCompilation=false',
            '--verbosity', 'quiet', '-clp:ErrorsOnly', '-o', (Split-Path (Runner $Version $kind)))
        if ($RestoreSource) { $buildArguments += @('--source', $RestoreSource, '-p:NuGetAudit=false') }
        Invoke-Checked $dotnet $buildArguments
    }
}

if ($Step -ne 'Run') { foreach ($version in $versions.Keys) { Build-Variant $version } }
if ($Step -eq 'Build') { return }

if (!$Photo) { $Photo = Join-Path $repo 'docs/images/studio-workspace.png' }
$Photo = (Resolve-Path -LiteralPath $Photo).Path
$fixtures = Join-Path $data 'fixtures'
if (!(Test-Path -LiteralPath (Join-Path $fixtures 'banner.cmps')) -or !(Test-Path -LiteralPath (Join-Path $fixtures 'card.cmps'))) {
    Invoke-Checked $dotnet @((Runner final native), 'project-fixtures', $fixtures, $Photo)
}
Get-FileHash -LiteralPath (Join-Path $fixtures 'card.cmps'), (Join-Path $fixtures 'banner.cmps') |
    Select-Object @{n='Name';e={[IO.Path]::GetFileName($_.Path)}}, Hash | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $data 'fixtures.json') -Encoding utf8

$env:COMPOSA_PERF_SKIP_DESKTOP_PROBE = '1'
$env:COMPOSA_PERF_SOFTWARE_PRESENTATION = '0'
$env:COMPOSA_PERF_LOW_LATENCY_COMPOSITION = '0'
$env:COMPOSA_PROJECT_SHORT = '1'
$env:COMPOSA_PERF_DIAG_GC = '0'
$env:COMPOSA_PERF_RECENT = '1'
$env:COMPOSA_PERF_FAST_BRUSH = '0'
$env:COMPOSA_PERF_BURST_BRUSH = '0'
$env:COMPOSA_AUTOMATION_REVERSE = '0'

foreach ($version in $versions.Keys) {
    Write-Host "Measure $version at $($versions[$version])"
    $directory = Join-Path $data $version
    [void][IO.Directory]::CreateDirectory($directory)
    $env:COMPOSA_PERF_EDITOR_GPU = '0'
    if ($IsWindows -and !$SkipNative) {
        $interaction = Join-Path $directory 'interaction-cpu.json'
        if (!(Test-Path -LiteralPath $interaction)) { Invoke-Checked $dotnet @((Runner $version native), 'interaction', $interaction) }
        $projects = Join-Path $directory 'projects.json'
        if (!(Test-Path -LiteralPath $projects)) { Invoke-Checked $dotnet @((Runner $version native), 'projects', $fixtures, $projects, 'banner') }
    }
    $env:COMPOSA_PERF_CASE = 'brush16'
    $env:COMPOSA_PERF_LONG_BRUSH = '0'
    $env:COMPOSA_PERF_FIXTURES = $fixtures
    $env:COMPOSA_PERF_OUTPUT = Join-Path $directory 'brush16.json'
    if (!(Test-Path -LiteralPath $env:COMPOSA_PERF_OUTPUT)) {
        Invoke-Checked $dotnet @((Runner $version tests), '-class', '*CanvasPerformanceBench', '-nocolor')
    }
    $edits = Join-Path $directory 'edits.jsonl'
    if (!(Test-Path -LiteralPath $edits)) {
        $rows = Invoke-Checked $dotnet @((Runner $version core), 'heavy-edits', 'short')
        $rows | Where-Object { $_.StartsWith('{') } | Set-Content -LiteralPath $edits -Encoding utf8
    }
    $io = Join-Path $directory 'io'
    if (!(Test-Path -LiteralPath (Join-Path $io 'png.json'))) {
        Invoke-Checked $dotnet @((Runner $version core), 'final-io', $fixtures, $io)
    }
    $env:COMPOSA_AUTOMATION_REPLAY = Join-Path $directory 'automation'
    if (!(Test-Path -LiteralPath (Join-Path $env:COMPOSA_AUTOMATION_REPLAY 'report.json'))) {
        Invoke-Checked $dotnet @((Runner $version tests), '-class', '*AutomationWorkflowBench', '-nocolor')
    }
}
if ($IsWindows -and !$SkipNative -and !$SkipGpu) {
    $env:COMPOSA_PERF_EDITOR_GPU = '1'
    $gpu = Join-Path $data 'final/interaction-gpu.json'
    if (!(Test-Path -LiteralPath $gpu)) { Invoke-Checked $dotnet @((Runner final native), 'interaction', $gpu) }
}

& (Join-Path $PSScriptRoot 'Read-FinalComparison.ps1') -DataDirectory $data
