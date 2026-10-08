#Requires -Version 5.1
param(
    [ValidateSet('ToPhotoshop', 'ToComposa')][string]$Direction = 'ToPhotoshop',
    [string]$ImagePath,
    [string]$ComposaExe,
    [string]$ReportPath
)
$ErrorActionPreference = 'Stop'
function Fail([string]$message) { throw $message }
# Direct per-user pipe: no second Composa process, startup handshake race or polling.
$needsBridge = $Direction -eq 'ToComposa' -or -not $ImagePath
$pipe = $null; $writer = $null; $reader = $null
try {
    if ($Direction -eq 'ToComposa' -and (-not $ImagePath -or -not (Test-Path -LiteralPath $ImagePath -PathType Leaf))) { Fail 'Pass an existing image with -ImagePath.' }
    if ($Direction -eq 'ToPhotoshop' -and -not (Get-Process Photoshop -ErrorAction SilentlyContinue)) { Fail 'Open Photoshop first.' }
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class ComposaExchangeWindows {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int processId);
}
"@
    if ($needsBridge) {
        $pipeName = $env:COMPOSA_EXCHANGE_PIPE
        if (-not $pipeName) { $pipeName = 'composa-image-exchange' }
        $pipe = New-Object IO.Pipes.NamedPipeClientStream('.', $pipeName, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
        try { $pipe.Connect(2000) }
        catch { Fail 'No Composa image-exchange connection. Open updated Composa (preview.21 or newer). AI Control is not required; run both apps as the same Windows user without elevation.' }
        $encoding = New-Object Text.UTF8Encoding($false)
        $writer = New-Object IO.StreamWriter($pipe, $encoding); $writer.AutoFlush = $true
        $reader = New-Object IO.StreamReader($pipe, $encoding)
        $script:requestId = 0
        function Request([string]$method, $parameters) {
            $script:requestId++; $id = $script:requestId
            $writer.WriteLine((@{jsonrpc='2.0'; id=$id; method=$method; params=$parameters} | ConvertTo-Json -Depth 12 -Compress))
            $deadline = [DateTime]::UtcNow.AddSeconds(60)
            while ([DateTime]::UtcNow -lt $deadline) {
                $line = $reader.ReadLineAsync()
                if (-not $line.Wait([Math]::Max(1, [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds))) { throw "Composa timed out during $method." }
                if ($null -eq $line.Result) { throw 'Composa connection closed unexpectedly.' }
                $reply = $line.Result | ConvertFrom-Json
                if ($reply.id -ne $id) { continue }
                if ($reply.error) { throw $reply.error.message }
                if ($reply.result.isError) { throw (($reply.result.content | ForEach-Object { $_.text }) -join "`n") }
                return $reply.result
            }
            throw "Composa timed out during $method."
        }
        $null = Request 'initialize' @{protocolVersion='2025-11-25'; capabilities=@{}; clientInfo=@{name='composa-photoshop-exchange'; version='2'}}
        $writer.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    }
    if ($Direction -eq 'ToComposa') {
        # The initiated transfer may activate its receiving editor window.
        [void][ComposaExchangeWindows]::AllowSetForegroundWindow(-1)
        $result = Request 'tools/call' @{name='open_document'; arguments=@{path=[IO.Path]::GetFullPath($ImagePath)}}
        $message = ($result.content | ForEach-Object { $_.text }) -join "`n"
    } else {
        $temporary = $ImagePath
        if (-not $temporary) {
            $temporary = Join-Path ([IO.Path]::GetTempPath()) ('Composa-to-Photoshop-' + [Guid]::NewGuid().ToString('N') + '.png')
            $null = Request 'tools/call' @{name='export_image'; arguments=@{path=$temporary}}
        }
        if (-not (Test-Path -LiteralPath $temporary -PathType Leaf)) { throw 'The flattened image file is missing.' }
        try { $photoshop = [Runtime.InteropServices.Marshal]::GetActiveObject('Photoshop.Application') }
        catch { $photoshop = New-Object -ComObject Photoshop.Application }
        $null = $photoshop.Open($temporary); $photoshop.Visible = $true
        foreach ($process in (Get-Process Photoshop -ErrorAction SilentlyContinue)) {
            if ($process.MainWindowHandle -eq [IntPtr]::Zero) { continue }
            if ([ComposaExchangeWindows]::IsIconic($process.MainWindowHandle)) { [void][ComposaExchangeWindows]::ShowWindow($process.MainWindowHandle, 9) }
            [void][ComposaExchangeWindows]::SetForegroundWindow($process.MainWindowHandle)
            break
        }
        $message = "Opened in Photoshop: $temporary"
    }
    if ($ReportPath) { [IO.File]::WriteAllText($ReportPath, $message, (New-Object Text.UTF8Encoding($false))) }
    $message
} catch {
    if ($ReportPath) { [IO.File]::WriteAllText($ReportPath, ('ERROR: ' + $_.Exception.Message), (New-Object Text.UTF8Encoding($false))) }
    throw
} finally {
    if ($writer) { $writer.Dispose() }; if ($reader) { $reader.Dispose() }; if ($pipe) { $pipe.Dispose() }
}
