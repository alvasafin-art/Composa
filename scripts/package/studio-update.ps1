param(
    [Parameter(Mandatory)][string]$Tree,
    [Parameter(Mandatory)][string]$Output,
    [Parameter(Mandatory)][string]$Version,
    [ValidateSet('win-x64','win-arm64')][string]$Rid = 'win-x64',
    [string]$Vpk = 'vpk',
    [string]$PreviousPackage
)
$ErrorActionPreference = 'Stop'
$treePath = (Resolve-Path -LiteralPath $Tree).Path
$binaryVersion = (Get-Item -LiteralPath (Join-Path $treePath 'composa.dll')).VersionInfo.ProductVersion.Split('+')[0]
if ($binaryVersion -ne $Version) { throw "Publish version $binaryVersion differs from package version $Version" }
$outputPath = [IO.Directory]::CreateDirectory($Output).FullName
if (Get-ChildItem -LiteralPath $outputPath -File) { throw 'Choose an empty output directory for this release.' }
if ($PreviousPackage) {
    $base = Get-Item -LiteralPath $PreviousPackage
    if ($base.Name -notlike "ComposaStudio-*-$Rid-full.nupkg") { throw "Wrong delta baseline for $Rid" }
    $baseVersion = $base.Name.Substring('ComposaStudio-'.Length).Replace("-$Rid-full.nupkg",'')
    if ([semver]$baseVersion -ge [semver]$Version) { throw 'The baseline must precede this release.' }
    Copy-Item -LiteralPath $base.FullName -Destination $outputPath
}
$icon = Join-Path $PSScriptRoot '../../packaging/icons/composa.ico'
& $Vpk pack -u ComposaStudio -v $Version -p $treePath -o $outputPath -c $Rid -r $Rid -e composa.exe --packTitle 'Composa Studio' --icon $icon
if ($LASTEXITCODE) { throw 'Velopack packaging failed.' }
$feed = Join-Path $outputPath "releases.$Rid.json"
$index = Get-Content -Raw -LiteralPath $feed | ConvertFrom-Json
$entries = @($index.Assets | Where-Object Version -eq $Version)
$full = @($entries | Where-Object Type -eq 'Full')
if ($full.Count -ne 1) { throw 'Missing full package for this release.' }
if ($PreviousPackage -and @($entries | Where-Object Type -eq 'Delta').Count -ne 1) { throw 'Missing delta package.' }
# A GitHub release owns its own assets. Velopack finds intermediate patches across prior releases.
$index.Assets = $entries
[IO.File]::WriteAllText($feed, ($index | ConvertTo-Json -Depth 8 -Compress))
if ($PreviousPackage) { Remove-Item -LiteralPath (Join-Path $outputPath $base.Name) }
foreach ($entry in $entries) {
    $file = Get-Item -LiteralPath (Join-Path $outputPath $entry.FileName)
    if ($file.Length -ne $entry.Size -or (Get-FileHash -LiteralPath $file.FullName).Hash -ne $entry.SHA256) {
        throw "Release feed does not match $($file.Name)"
    }
}
# Keep only metadata unique to the full package and checksums for its installed files.
$portable = Join-Path $outputPath "ComposaStudio-$Rid-Portable.zip"
$archive = [IO.Compression.ZipFile]::Open($portable, [IO.Compression.ZipArchiveMode]::Update)
$package = [IO.Compression.ZipFile]::OpenRead((Join-Path $outputPath $full[0].FileName))
try {
    $rows = foreach ($entry in $package.Entries) {
        if (!$entry.Name) { continue }
        $path = $entry.FullName
        $source = $null
        if ($path.StartsWith('lib/app/')) {
            $leaf = $path.Substring(8)
            $source = switch ($leaf) {
                'Squirrel.exe' { 'Update.exe' }
                'Composa Studio_ExecutionStub.exe' { 'Composa Studio.exe' }
                default { "current/$leaf" }
            }
        }
        $stream = $entry.Open()
        try {
            if ($source) {
                $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))
                if (!$archive.GetEntry($source)) { throw "Portable payload lacks $source" }
                @{ Path=$path; Source=$source; Hash=$hash }
            } else {
                if ($entry.Length -gt 2MB) { throw "Unexpected package metadata: $path" }
                $buffer = [IO.MemoryStream]::new()
                try { $stream.CopyTo($buffer); @{ Path=$path; Data=[Convert]::ToBase64String($buffer.ToArray()) } }
                finally { $buffer.Dispose() }
            }
        } finally { $stream.Dispose() }
    }
    $recipe = @{ Version=$Version; Package=$full[0].FileName; Entries=@($rows) } | ConvertTo-Json -Depth 5 -Compress
    $file = $archive.CreateEntry('portable-baseline.json').Open()
    try { $file.Write([Text.Encoding]::UTF8.GetBytes($recipe)) } finally { $file.Dispose() }
} finally { $package.Dispose(); $archive.Dispose() }
