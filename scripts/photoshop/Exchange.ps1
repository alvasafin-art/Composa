#Requires -Version 5.1
param(
    [ValidateSet('ToPhotoshop', 'ToComposa')][string]$Direction = 'ToPhotoshop',
    [string]$ImagePath,
    [string]$ComposaExe,
    [string]$ReportPath
)
$ErrorActionPreference = 'Stop'
function Fail([string]$message) {
    if ($ReportPath) { [IO.File]::WriteAllText($ReportPath, ('ERROR: ' + $message), (New-Object System.Text.UTF8Encoding($false))) }
    throw $message
}

# Image exchange has its own narrow bridge, independent of Allow AI Control.
if (-not $ComposaExe) { $ComposaExe = $env:COMPOSA_EXE }
if (-not $ComposaExe) {
    $repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $candidates = @((Join-Path $repo 'dist/feedback-preview-win-x64/composa.exe'),
        (Join-Path $repo 'dist/portable-win-x64/composa.exe'), (Join-Path $repo 'composa.exe'))
    $ComposaExe = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
$needsBridge = $Direction -eq 'ToComposa' -or -not $ImagePath
if ($needsBridge -and (-not $ComposaExe -or -not (Test-Path -LiteralPath $ComposaExe -PathType Leaf))) {
    Fail 'Set COMPOSA_EXE to the full path of composa.exe, or pass -ComposaExe.'
}
if ($Direction -eq 'ToComposa' -and (-not $ImagePath -or -not (Test-Path -LiteralPath $ImagePath -PathType Leaf))) {
    Fail 'Pass an existing image with -ImagePath.'
}
if ($Direction -eq 'ToPhotoshop' -and -not (Get-Process Photoshop -ErrorAction SilentlyContinue)) {
    Fail 'Open Photoshop first.'
}
$start = New-Object System.Diagnostics.ProcessStartInfo
if ($needsBridge) { $start.FileName = [IO.Path]::GetFullPath($ComposaExe) }
$start.Arguments = '--exchange'
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)
$bridge = New-Object System.Diagnostics.Process
$bridge.StartInfo = $start
$requestId = 0
$started = $false
try {
    if ($needsBridge) {
        [void]$bridge.Start()
        $started = $true
        $writer = New-Object System.IO.StreamWriter($bridge.StandardInput.BaseStream, (New-Object System.Text.UTF8Encoding($false)))
        $writer.AutoFlush = $true
        $stderr = $bridge.StandardError.ReadToEndAsync()
        function Request([string]$method, $parameters) {
            $script:requestId++
            $id = $script:requestId
            $request = @{jsonrpc='2.0'; id=$id; method=$method; params=$parameters} | ConvertTo-Json -Depth 12 -Compress
            $writer.WriteLine($request)
            $deadline = [DateTime]::UtcNow.AddSeconds(60)
            while ([DateTime]::UtcNow -lt $deadline) {
                $line = $bridge.StandardOutput.ReadLineAsync()
                $remaining = [Math]::Max(1, [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds)
                if (-not $line.Wait($remaining)) { throw "Composa timed out during $method." }
                if ($null -eq $line.Result) { throw 'Composa bridge closed unexpectedly.' }
                $reply = $line.Result | ConvertFrom-Json
                if ($reply.id -ne $id) { continue }
                if ($reply.error) { throw $reply.error.message }
                if ($reply.result.isError) { throw (($reply.result.content | ForEach-Object { $_.text }) -join "`n") }
                return $reply.result
            }
            throw "Composa timed out during $method."
        }
        $null = Request 'initialize' @{protocolVersion='2025-11-25'; capabilities=@{}; clientInfo=@{name='composa-photoshop-exchange'; version='1'}}
        $writer.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
        # initialize belongs to the bridge; wait for the running editor's tool list.
        $connected = $false
        for ($attempt = 0; $attempt -lt 100; $attempt++) {
            $listing = Request 'tools/list' @{}
            if ($listing.tools.name -contains 'export_image' -and $listing.tools.name -contains 'open_document') { $connected = $true; break }
            Start-Sleep -Milliseconds 100
        }
        if (-not $connected) { throw 'No Composa image-exchange connection. Open the updated Composa (preview.20 or newer). Allow AI Control is not required; run both apps as the same Windows user without elevation.' }
    }
    if ($Direction -eq 'ToComposa') {
        $result = Request 'tools/call' @{name='open_document'; arguments=@{path=[IO.Path]::GetFullPath($ImagePath)}}
        $message = ($result.content | ForEach-Object { $_.text }) -join "`n"
    } else {
        $temporary = $ImagePath
        if (-not $temporary) {
            $temporary = Join-Path ([IO.Path]::GetTempPath()) ('Composa-to-Photoshop-' + [Guid]::NewGuid().ToString('N') + '.png')
            $null = Request 'tools/call' @{name='export_image'; arguments=@{path=$temporary}}
        }
        if (-not (Test-Path -LiteralPath $temporary -PathType Leaf)) { throw 'The flattened image file is missing.' }
        # Windows PowerShell 5.1 exposes the running Photoshop COM automation object.
        try { $photoshop = [Runtime.InteropServices.Marshal]::GetActiveObject('Photoshop.Application') }
        catch { $photoshop = New-Object -ComObject Photoshop.Application }
        $null = $photoshop.Open($temporary)
        $photoshop.Visible = $true
        $message = "Opened in Photoshop: $temporary"
    }
    if ($ReportPath) { [IO.File]::WriteAllText($ReportPath, $message, (New-Object System.Text.UTF8Encoding($false))) }
    $message
} catch {
    if ($ReportPath) { [IO.File]::WriteAllText($ReportPath, ('ERROR: ' + $_.Exception.Message), (New-Object System.Text.UTF8Encoding($false))) }
    throw
} finally {
    if ($started) {
        if ($writer) { $writer.Close() } else { $bridge.StandardInput.Close() }
        if (-not $bridge.WaitForExit(3000)) { $bridge.Kill() }
    }
    $bridge.Dispose()
}
