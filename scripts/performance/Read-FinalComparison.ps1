[CmdletBinding()]
param([Parameter(Mandatory)][string]$DataDirectory)

$ErrorActionPreference = 'Stop'
$data = (Resolve-Path -LiteralPath $DataDirectory).Path
$commits = Get-Content -LiteralPath (Join-Path $data 'commits.json') -Raw | ConvertFrom-Json -AsHashtable
$versions = @('main', 'start', 'final')
$rows = [ordered]@{}
$checks = [System.Collections.Generic.List[object]]::new()
$signatures = @{}
$native = @{}
$projects = @{}

function Read-Json([string]$Relative, [switch]$Optional) {
    $path = Join-Path $data $Relative
    if ($Optional -and !(Test-Path -LiteralPath $path)) { return $null }
    Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

function Record([string]$Version, [string]$Domain, [string]$Metric, [string]$Boundary, [string]$Unit, $Values, [int]$Attempted = 0) {
    $valuesSorted = @($Values | Where-Object { $null -ne $_ } | ForEach-Object { [double]$_ } | Sort-Object)
    $n = $valuesSorted.Count
    if ($Attempted -eq 0) { $Attempted = $n }
    $p50 = if ($n) { $valuesSorted[[int][Math]::Ceiling(.5 * $n) - 1] }
    $p95 = if ($n) { $valuesSorted[[int][Math]::Ceiling(.95 * $n) - 1] }
    Record-Summary $Version $Domain $Metric $Boundary $Unit $n $Attempted $p50 $p95
}

function Record-Summary([string]$Version, [string]$Domain, [string]$Metric, [string]$Boundary, [string]$Unit, [int]$N, [int]$Attempted, $P50, $P95) {
    $key = "$Domain/$Metric"
    if (!$rows.Contains($key)) { $rows[$key] = [ordered]@{ domain = $Domain; metric = $Metric; boundary = $Boundary; unit = $Unit } }
    $rows[$key][$Version] = @{ n = $N; attempted = $Attempted; p50 = $P50; p95 = $P95 }
}

function Add-Check([string]$Name, [bool]$Passed, $Details) {
    $checks.Add([ordered]@{ name = $Name; passed = $Passed; details = $Details })
}

function Record-Native([string]$Version, $Result) {
    if ($null -eq $Result) { return }
    foreach ($group in $Result.samples | Group-Object phase) {
        if (!$group.Name) { continue }
        Record $Version 'win32' $group.Name 'Win32 message or queued Avalonia wheel to Skia draw callback' 'ms' $group.Group.canvasDrawMs $group.Count
        Record $Version 'win32-input' $group.Name 'Post to handler entry' 'ms' $group.Group.queueMs $group.Count
    }
    foreach ($entry in $Result.control.PSObject.Properties | Where-Object { $_.Value -is [bool] }) {
        Add-Check "$Version win32 $($entry.Name)" $entry.Value $null
    }
    $native[$Version] = @{ remote = $Result.remote; desktopProbe = $Result.desktopProbe; hardwareSkia = $Result.hardwareSkia;
        frames = $Result.samples.Count; editorRequested = $Result.editorRequested; openCl = $Result.openCl;
        stages = @($Result.samples.diagnostics.Stages | Group-Object Name | ForEach-Object {
            @{ name = $_.Name; calls = ($_.Group.Calls | Measure-Object -Sum).Sum; milliseconds = ($_.Group.Milliseconds | Measure-Object -Sum).Sum }
        }) }
    $signatures[$Version]['win32'] = @($Result.control.'complex final pixels', $Result.control.'simple restored pixels')
}

foreach ($version in $versions) {
    $signatures[$version] = [ordered]@{}
    Record-Native $version (Read-Json "$version/interaction-cpu.json" -Optional)
    $project = Read-Json "$version/projects.json" -Optional
    if ($null -ne $project) {
        foreach ($group in $project.frames | Group-Object label) {
            Record $version 'projects' $group.Name 'OpenPath invocation to Skia draw callback, Win32' 'ms' $group.Group.canvasDrawMs
        }
        foreach ($group in $project.measurements | Group-Object label) {
            Record $version 'projects-end-to-end' $group.Name 'Native harness action through awaited completion' 'ms' $group.Group.elapsed
            Record $version 'projects-private-working-set' $group.Name 'Peak sampled process Private Working Set in the phase' 'MiB' @(
                $group.Group.sampled.PrivateWorkingSet | Where-Object { $null -ne $_ } | ForEach-Object { $_ / 1MB }) $group.Count
            Record $version 'projects-allocations' $group.Name 'Process-wide managed bytes allocated during the phase' 'MiB' @($group.Group.allocated | ForEach-Object { $_ / 1MB })
        }
        Record $version 'projects' 'startup first document' 'Managed benchmark Main to Skia draw; fresh process; OS disk cache retained' 'ms' @($project.startupDocumentMs)
        $projects[$version] = @{ afterClose = $project.afterClose; closed = $project.closed; closedAlive = $project.closedAlive }
    }

    $brush = Read-Json "$version/brush16.json"
    foreach ($phase in $brush.phases) {
        foreach ($group in $phase.Events | Group-Object Input) {
            Record $version 'headless-brush' "$($phase.Name) / $($group.Name)" 'Headless pointer event to Skia draw callback' 'ms' $group.Group.CompleteMs $group.Count
            Record $version 'headless-brush-input' "$($phase.Name) / $($group.Name)" 'Headless pointer handler only' 'ms' $group.Group.InputMs $group.Count
        }
    }
    $signatures[$version]['brush'] = $brush.checks

    $edits = @(Get-Content -LiteralPath (Join-Path $data "$version/edits.jsonl") | ForEach-Object { $_ | ConvertFrom-Json })
    foreach ($edit in $edits) { Record-Summary $version 'cpu-edits' $edit.name 'Core call to return; OpenCL disabled' 'ms' $edit.n $edit.n $edit.p50 $edit.p95 }
    $signatures[$version]['cpu-edits'] = @($edits | Where-Object check | Select-Object name,check)

    $io = @(Read-Json "$version/io/png.json")
    foreach ($fixture in $io) {
        Record $version 'png' $fixture.name 'Core PNG save, encode and file write, precomputed working raster' 'ms' $fixture.observations.ms
        Record $version 'png-allocations' $fixture.name 'Managed allocations on the PNG save thread' 'MiB' @($fixture.observations.bytes | ForEach-Object { $_ / 1MB })
    }
    $signatures[$version]['io'] = @($io | Select-Object name,depth,profile,pixels,masks,png,dpi,pngBytes)

    $automation = Read-Json "$version/automation/report.json"
    foreach ($group in $automation.samples | Group-Object Mode) {
        Record $version 'automation' $group.Name 'Local headless workflow per variant with PNG/CMPS and three previews' 'ms' $group.Group.Total
        $series = for ($i = 0; $i -lt $group.Count; $i += 2) { $group.Group[$i].Total + $group.Group[$i + 1].Total }
        Record $version 'automation-series' $group.Name 'Sum of two full variant times; no external model or user input' 'ms' @($series)
        Record $version 'automation-calls' $group.Name 'Local Assistant/MCP tool calls per variant' 'count' $group.Group.Calls
        Record $version 'automation-draws' $group.Name 'Canvas callbacks with a headless render opportunity after each edit call' 'count' $group.Group.Draws
    }
    $signatures[$version]['automation'] = @($automation.equivalent)
}

$gpu = Read-Json 'final/interaction-gpu.json' -Optional
if ($null -ne $gpu) { $signatures['gpu'] = [ordered]@{}; Record-Native 'gpu' $gpu }

foreach ($domain in $signatures['main'].Keys) {
    $a = ConvertTo-Json -InputObject $signatures['main'][$domain] -Depth 10 -Compress
    $b = ConvertTo-Json -InputObject $signatures['start'][$domain] -Depth 10 -Compress
    $c = ConvertTo-Json -InputObject $signatures['final'][$domain] -Depth 10 -Compress
    Add-Check "Identical $domain output on main, start and final" ($a -ceq $b -and $b -ceq $c) $null
}
if ($null -ne $gpu) {
    $a = $signatures['final']['win32'] -join '|'
    $b = $signatures['gpu']['win32'] -join '|'
    Add-Check 'Identical final Win32 pixels with CPU and GPU requested' ($a -ceq $b) $null
}

foreach ($row in $rows.Values) {
    if ($row.main.p50 -gt 0 -and $row.final.p50 -gt 0) { $row['mainToFinalP50Ratio'] = $row.main.p50 / $row.final.p50 }
    if ($row.start.p50 -gt 0 -and $row.final.p50 -gt 0) { $row['startToFinalP50Ratio'] = $row.start.p50 / $row.final.p50 }
}
$report = Join-Path $data 'comparison.json'
[ordered]@{ commits = $commits; fixtures = @(Read-Json 'fixtures.json'); measurements = @($rows.Values);
    checks = $checks.ToArray(); signatures = $signatures; native = $native; projects = $projects } |
    ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $report -Encoding utf8
Write-Host "A/B report: $report"
$failed = @($checks | Where-Object { !$_.passed })
if ($failed.Count) { throw "Failed correctness checks: $($failed.name -join '; ')" }
