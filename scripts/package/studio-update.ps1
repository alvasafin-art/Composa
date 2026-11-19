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
# Setup seeds the local package cache itself. Seed the official portable layout as well so its
# first update only needs a patch. Store the already-compressed package without recompressing it.
$portable = Join-Path $outputPath "ComposaStudio-$Rid-Portable.zip"
$archive = [IO.Compression.ZipFile]::Open($portable, [IO.Compression.ZipArchiveMode]::Update)
try {
    $name = $full[0].FileName
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $outputPath $name),
        "packages/$name", [IO.Compression.CompressionLevel]::NoCompression)
} finally { $archive.Dispose() }
