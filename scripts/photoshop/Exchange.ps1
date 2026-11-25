#Requires -Version 5.1
param(
    [ValidateSet('ToPhotoshop', 'ToComposa')][string]$Direction = 'ToPhotoshop',
    [string]$ImagePath,
    [string]$ComposaExe,
    [string]$ReportPath,
    [switch]$Layers
)
$ErrorActionPreference = 'Stop'
function Fail([string]$message) { throw $message }
function WriteReport([string]$message) {
    if ($ReportPath) {
        $pendingReport = $ReportPath + '.part'
        [IO.File]::WriteAllText($pendingReport, $message, (New-Object Text.UTF8Encoding($false)))
        Move-Item -LiteralPath $pendingReport -Destination $ReportPath -Force
    }
}
# Prefer the running editor's per-user pipe. Launch the bundled editor for a PSD if it is closed.
$needsBridge = $Direction -eq 'ToComposa' -or -not $ImagePath
$pipe = $null; $writer = $null; $reader = $null
$generatedImage = $null
$handedOff = $false
try {
    $sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $photoshopProcesses = @(Get-Process Photoshop -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $sessionId })
    if ($Direction -eq 'ToComposa' -and (-not $ImagePath -or -not (Test-Path -LiteralPath $ImagePath -PathType Leaf))) { Fail 'Pass an existing image with -ImagePath.' }
    if ($Direction -eq 'ToPhotoshop' -and $photoshopProcesses.Count -eq 0) { Fail 'Open Photoshop in this Windows session first.' }
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
    if ($Direction -eq 'ToComposa') {
        # Let the receiver activate its editor and any PSD conversion dialog.
        [void][ComposaExchangeWindows]::AllowSetForegroundWindow(-1)
    }
    if ($needsBridge) {
        $pipeName = $env:COMPOSA_EXCHANGE_PIPE
        $userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        if (-not $userSid) { Fail 'The Windows user SID is unavailable.' }
        if (-not $pipeName) { $pipeName = 'composa-image-exchange-' + $userSid }
        $pipe = New-Object IO.Pipes.NamedPipeClientStream('.', $pipeName, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
        try { $pipe.Connect(2000) }
        catch [TimeoutException] {
            if ($Direction -ne 'ToComposa' -or [IO.Path]::GetExtension($ImagePath) -notin '.psd', '.psb') {
                Fail 'No Composa image-exchange connection. Open updated Composa. AI Control is not required; run both apps as the same Windows user without elevation.'
            }
            if (-not $ComposaExe) { $ComposaExe = Join-Path $PSScriptRoot '../../composa.exe' }
            if (-not (Test-Path -LiteralPath $ComposaExe -PathType Leaf)) {
                Fail 'Could not find composa.exe. Run the Photoshop script from scripts/photoshop in the extracted Composa package or installation.'
            }
            $snapshot = [IO.Path]::GetFullPath($ImagePath)
            Start-Process -FilePath ([IO.Path]::GetFullPath($ComposaExe)) -WindowStyle Normal -ArgumentList ('--photoshop-transfer "' + $snapshot + '"')
            $message = "Started Composa with $snapshot"
            WriteReport $message
            $message
            return
        }
        if ($pipe.GetAccessControl().GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $userSid) { Fail 'The Composa pipe belongs to another Windows user.' }
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
        $result = Request 'tools/call' @{name='open_document'; arguments=@{path=[IO.Path]::GetFullPath($ImagePath)}}
        $message = ($result.content | ForEach-Object { $_.text }) -join "`n"
    } else {
        $temporary = $ImagePath
        if (-not $temporary) {
            $extension = if ($Layers) { '.psd' } else { '.png' }
            $tool = if ($Layers) { 'export_psd' } else { 'export_image' }
            $generatedImage = Join-Path ([IO.Path]::GetTempPath()) ('Composa-to-Photoshop-' + [Guid]::NewGuid().ToString('N') + $extension)
            $temporary = $generatedImage
            $null = Request 'tools/call' @{name=$tool; arguments=@{path=$temporary}}
        }
        if (-not (Test-Path -LiteralPath $temporary -PathType Leaf)) { throw 'The image exchange file is missing.' }
        # Open through Photoshop's normal file handoff, outside its COM automation context.
        # Avoid a synchronous automation call while Photoshop imports layered documents.
        # Use the running instance's executable, not the default .psd/.png association.
        $photoshopProcess = $photoshopProcesses | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } | Select-Object -First 1
        if (-not $photoshopProcess) { $photoshopProcess = $photoshopProcesses | Select-Object -First 1 }
        $photoshopExecutable = $photoshopProcess.Path
        if (-not $photoshopExecutable -or -not (Test-Path -LiteralPath $photoshopExecutable -PathType Leaf)) { Fail 'Cannot locate the running Photoshop executable. Run both apps as the same Windows user without elevation.' }
        $temporary = [IO.Path]::GetFullPath($temporary)
        $handoff = Start-Process -FilePath $photoshopExecutable -ArgumentList ('"' + $temporary + '"') -WindowStyle Hidden -PassThru
        if ($null -eq $handoff) { Fail 'Could not hand the file to Photoshop.' }
        # Photoshop opens the submitted file asynchronously and may still need its path.
        $handedOff = $true
        $handoff.Dispose()
        foreach ($process in $photoshopProcesses) {
            if ($process.MainWindowHandle -eq [IntPtr]::Zero) { continue }
            if ([ComposaExchangeWindows]::IsIconic($process.MainWindowHandle)) { [void][ComposaExchangeWindows]::ShowWindow($process.MainWindowHandle, 9) }
            [void][ComposaExchangeWindows]::SetForegroundWindow($process.MainWindowHandle)
            break
        }
        $message = "Sent to Photoshop: $temporary"
    }
    WriteReport $message
    $message
} catch {
    WriteReport ('ERROR: ' + $_.Exception.Message)
    throw
} finally {
    if ($generatedImage -and -not $handedOff) { Remove-Item -LiteralPath $generatedImage -Force -ErrorAction SilentlyContinue }
    if ($writer) { $writer.Dispose() }; if ($reader) { $reader.Dispose() }; if ($pipe) { $pipe.Dispose() }
}
