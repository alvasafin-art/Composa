[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DataDirectory,
    [string]$Before = 'baseline',
    [string]$After = 'candidate'
)

$ErrorActionPreference = 'Stop'
$culture = [Globalization.CultureInfo]::InvariantCulture

function Read-Runs([string]$Variant, [string]$Case) {
    Get-ChildItem -LiteralPath (Join-Path $DataDirectory $Variant) -Filter "$Case-*.json" |
        Sort-Object Name | ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName | ConvertFrom-Json }
}

function Read-Fixtures([string]$Variant) {
    $path = Join-Path (Join-Path $DataDirectory $Variant) 'fixtures.json'
    Get-Content -Raw -LiteralPath $path | ConvertFrom-Json | Sort-Object Fixture
}

function Percentile($Values, [double]$Fraction) {
    $ordered = @($Values | Where-Object { $null -ne $_ } | Sort-Object)
    if ($ordered.Count -eq 0) { return '—' }
    ([double]$ordered[[math]::Ceiling($ordered.Count * $Fraction) - 1]).ToString('F2', $culture)
}

function Format-Phase($Runs, [string]$Phase) {
    $records = @($Runs | ForEach-Object { $_.phases | Where-Object Name -eq $Phase })
    $frames = @($records.Events | Where-Object Input -in 'press','move' | ForEach-Object CompleteMs)
    $release = @($records.Events | Where-Object Input -eq 'release' | ForEach-Object CompleteMs)
    $first = @($records | ForEach-Object { $_.Events | Where-Object Input -eq 'press' | ForEach-Object CompleteMs })
    $peakP = @($records | ForEach-Object { $_.Peak.Private / 1MB })
    $peakW = @($records | ForEach-Object { $_.Peak.WorkingSet / 1MB })
    $time = @($records | ForEach-Object ElapsedMs)
    [pscustomobject]@{
        Frame = "$(Percentile $frames .50) / $(Percentile $frames .95) / $(Percentile $frames 1)"
        Press = Percentile $first .50
        Release = Percentile $release .50
        Private = Percentile $peakP .50
        Working = Percentile $peakW .50
        Wall = Percentile $time .50
        SameKey = Percentile @($records | ForEach-Object { $_.Diagnostics.MaxSameKey }) 1
        Effects = Percentile @($records | ForEach-Object { $_.Diagnostics.Calculations }) .50
    }
}

$beforeFixtures = @(Read-Fixtures $Before)
$afterFixtures = @(Read-Fixtures $After)
if ($beforeFixtures.Count -ne 3 -or $afterFixtures.Count -ne 3 -or
    (Compare-Object $beforeFixtures $afterFixtures -Property Fixture, Hash)) {
    throw 'The two runs must use the same three CMPS files.'
}

$cases = 'card', 'banner', 'photo', 'tabs'
'| Сценарий | Кадр p50 / p95 / max, мс | Нажатие, мс | Отпускание, мс | Пик private / WS, МиБ | Вся фаза, мс | Расчёты / одновременные для одного ключа |'
'|---|---:|---:|---:|---:|---:|---:|'
foreach ($case in $cases) {
    $a = @(Read-Runs $Before $case)
    $b = @(Read-Runs $After $case)
    if ($a.Count -ne $b.Count -or $a.Count -eq 0) { throw "Different or empty sample sets for $case." }
    $reference = $a[0].checks | ConvertTo-Json -Compress
    foreach ($run in $a + $b) {
        if (($run.checks | ConvertTo-Json -Compress) -cne $reference) {
            throw "Different correctness fingerprints for $case."
        }
    }
    foreach ($phase in $a[0].phases.Name) {
        $old = Format-Phase $a $phase
        $new = Format-Phase $b $phase
        "| $case · $phase | $($old.Frame) → $($new.Frame) | $($old.Press) → $($new.Press) | $($old.Release) → $($new.Release) | $($old.Private) / $($old.Working) → $($new.Private) / $($new.Working) | $($old.Wall) → $($new.Wall) | $($old.Effects) / $($old.SameKey) → $($new.Effects) / $($new.SameKey) |"
    }
    $oldClosed = @($a.checkpoints | Where-Object Name -eq 'closed +30s')
    $newClosed = @($b.checkpoints | Where-Object Name -eq 'closed +30s')
    $pOld = Percentile @($oldClosed | ForEach-Object { $_.Memory.Private / 1MB }) .50
    $pNew = Percentile @($newClosed | ForEach-Object { $_.Memory.Private / 1MB }) .50
    $wOld = Percentile @($oldClosed | ForEach-Object { $_.Memory.WorkingSet / 1MB }) .50
    $wNew = Percentile @($newClosed | ForEach-Object { $_.Memory.WorkingSet / 1MB }) .50
    "| $case · закрыт +30 с | — | — | — | $pOld / $wOld → $pNew / $wNew | — | — |"
}
