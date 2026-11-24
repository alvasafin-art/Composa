using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Composa.App.Mcp;
using Composa.Editing;
using SkiaSharp;

namespace Composa.App.Tests;

public class PhotoshopFileHandoffTests
{
    [AvaloniaTheory]
    [InlineData(".psd", false, false)]
    [InlineData(".png", false, false)]
    [InlineData(".psd", true, false)]
    [InlineData(".png", false, true)]
    [InlineData(".psd", false, true)]
    [InlineData(".png", true, true)]
    [InlineData(".psd", true, true)]
    public async Task Exchange_hands_off_the_quoted_file_and_cleans_up_only_its_failed_exports(string extension, bool fail, bool export)
    {
        if (!OperatingSystem.IsWindows()) return;
        var window = new MainWindow();
        var session = EditorSession.NewCanvas(32, 24, SKColors.CornflowerBlue);
        window.AddSession(session);
        var history = session.History.CurrentId;
        using var host = new McpHost(window, "composa-handoff-test-" + Guid.NewGuid().ToString("N"), exchangeOnly: true);
        if (export) Assert.True(await host.StartAsync());
        var folder=Path.Combine(Path.GetTempPath(),"Composa handoff ' & [test] "+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var executable=Path.Combine(folder,"Photoshop.exe");var image=Path.Combine(folder,"Layered image"+extension);
            File.WriteAllText(executable,"");File.WriteAllText(image,"");
            var runner=Path.Combine(folder,"verify.ps1");
            File.WriteAllText(runner,"""
                $ErrorActionPreference='Stop'
                $global:handoffImage=$null; $global:handoffDisposed=$false
                function Get-Process {
                    [pscustomobject]@{SessionId=[Diagnostics.Process]::GetCurrentProcess().SessionId; MainWindowHandle=[IntPtr]::Zero; Path=$env:HANDOFF_EXE}
                }
                function Start-Process {
                    param($FilePath,$ArgumentList,$WindowStyle,[switch]$PassThru)
                    if ($FilePath -cne $env:HANDOFF_EXE) { throw 'Wrong Photoshop executable' }
                    if ($ArgumentList -notmatch '^"([^"]+)"$') { throw 'File argument lost its quoting' }
                    $global:handoffImage=$Matches[1]
                    if (-not (Test-Path -LiteralPath $global:handoffImage -PathType Leaf)) { throw 'File vanished before launch' }
                    if ($env:HANDOFF_EXPORT -eq 'true') {
                        if ([IO.Path]::GetDirectoryName($global:handoffImage) -ne $env:TEMP -or
                            [IO.Path]::GetExtension($global:handoffImage) -ne [IO.Path]::GetExtension($env:HANDOFF_IMAGE)) {
                            throw 'Wrong temporary export path'
                        }
                    } elseif ($global:handoffImage -cne $env:HANDOFF_IMAGE) { throw 'Wrong file argument' }
                    if ($WindowStyle -ne 'Hidden' -or -not $PassThru) { throw 'Unexpected launch options' }
                    if ($env:HANDOFF_FAIL -eq 'true') { throw 'Simulated launch failure' }
                    $process=[pscustomobject]@{}
                    $process | Add-Member ScriptMethod Dispose { $global:handoffDisposed=$true }
                    $process
                }
                $options = @{ Direction = 'ToPhotoshop' }
                if ($env:HANDOFF_EXPORT -eq 'true') { $options.Layers = $env:HANDOFF_IMAGE.EndsWith('.psd') }
                else { $options.ImagePath = $env:HANDOFF_IMAGE }
                try {
                    $result=& $env:HANDOFF_HELPER @options
                    if ($env:HANDOFF_FAIL -eq 'true') { throw 'Expected a launch failure' }
                    if (-not $global:handoffImage -or -not $global:handoffDisposed) { throw 'Handoff was not completed' }
                    if ($result -notlike 'Sent to Photoshop:*') { throw 'Unexpected result' }
                } catch {
                    if ($env:HANDOFF_FAIL -ne 'true' -or -not $global:handoffImage -or $_.Exception.Message -ne 'Simulated launch failure') { throw }
                }
                $expectedFile = $env:HANDOFF_FAIL -ne 'true' -or $env:HANDOFF_EXPORT -ne 'true'
                if ((Test-Path -LiteralPath $global:handoffImage -PathType Leaf) -ne $expectedFile) { throw 'Incorrect handoff file lifetime' }
                """);
            var start=new ProcessStartInfo("powershell.exe") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var arg in new[] {"-NoProfile","-ExecutionPolicy","Bypass","-File",runner})start.ArgumentList.Add(arg);
            start.Environment["HANDOFF_EXE"]=executable;start.Environment["HANDOFF_IMAGE"]=image;
            start.Environment["HANDOFF_FAIL"]=fail?"true":"false";
            start.Environment["HANDOFF_EXPORT"] = export ? "true" : "false";
            start.Environment["COMPOSA_EXCHANGE_PIPE"] = host.PipeName;
            start.Environment["TEMP"] = start.Environment["TMP"] = folder;
            start.Environment["HANDOFF_HELPER"]=Path.Combine(AppContext.BaseDirectory,"scripts","photoshop","Exchange.ps1");
            using var process=Process.Start(start)!;
            var output=process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var errors=process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var exit = process.WaitForExitAsync(timeout.Token);
                while (!exit.IsCompleted)
                {
                    Dispatcher.UIThread.RunJobs();
                    await Task.WhenAny(exit, Task.Delay(10, timeout.Token));
                }
                await exit;
            }
            catch {if(!process.HasExited)process.Kill();throw;}
            Assert.True(process.ExitCode==0,(await errors)+await output);
            Assert.Equal(history, session.History.CurrentId);
        }
        finally {Directory.Delete(folder,recursive:true);}
    }
}
