using System.Diagnostics;

namespace Composa.App.Tests;

public class PhotoshopFileHandoffTests
{
    [Theory]
    [InlineData(".psd", false)]
    [InlineData(".png", false)]
    [InlineData(".psd", true)]
    public async Task Exchange_hands_the_quoted_file_to_the_running_executable_without_COM(string extension, bool fail)
    {
        if (!OperatingSystem.IsWindows()) return;
        var folder=Path.Combine(Path.GetTempPath(),"Composa handoff ' & [test] "+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var executable=Path.Combine(folder,"Photoshop.exe");var image=Path.Combine(folder,"Layered image"+extension);
            File.WriteAllText(executable,"");File.WriteAllText(image,"");
            var runner=Path.Combine(folder,"verify.ps1");
            File.WriteAllText(runner,"""
                $ErrorActionPreference='Stop'
                $global:handoffCalled=$false; $global:handoffDisposed=$false
                function Get-Process {
                    [pscustomobject]@{SessionId=[Diagnostics.Process]::GetCurrentProcess().SessionId; MainWindowHandle=[IntPtr]::Zero; Path=$env:HANDOFF_EXE}
                }
                function Start-Process {
                    param($FilePath,$ArgumentList,$WindowStyle,[switch]$PassThru)
                    if ($FilePath -cne $env:HANDOFF_EXE) { throw 'Wrong Photoshop executable' }
                    if ($ArgumentList -cne ('"'+$env:HANDOFF_IMAGE+'"')) { throw 'File argument lost its quoting' }
                    if ($WindowStyle -ne 'Hidden' -or -not $PassThru) { throw 'Unexpected launch options' }
                    $global:handoffCalled=$true
                    if ($env:HANDOFF_FAIL -eq 'true') { throw 'Simulated launch failure' }
                    $process=[pscustomobject]@{}
                    $process | Add-Member ScriptMethod Dispose { $global:handoffDisposed=$true }
                    $process
                }
                try {
                    $result=& $env:HANDOFF_HELPER -Direction ToPhotoshop -ImagePath $env:HANDOFF_IMAGE
                    if ($env:HANDOFF_FAIL -eq 'true') { throw 'Expected a launch failure' }
                    if (-not $global:handoffCalled -or -not $global:handoffDisposed) { throw 'Handoff was not completed' }
                    if ($result -notlike 'Sent to Photoshop:*') { throw 'Unexpected result' }
                } catch {
                    if ($env:HANDOFF_FAIL -ne 'true' -or -not $global:handoffCalled -or $_.Exception.Message -ne 'Simulated launch failure') { throw }
                }
                """);
            var start=new ProcessStartInfo("powershell.exe") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var arg in new[] {"-NoProfile","-ExecutionPolicy","Bypass","-File",runner})start.ArgumentList.Add(arg);
            start.Environment["HANDOFF_EXE"]=executable;start.Environment["HANDOFF_IMAGE"]=image;
            start.Environment["HANDOFF_FAIL"]=fail?"true":"false";
            start.Environment["HANDOFF_HELPER"]=Path.Combine(AppContext.BaseDirectory,"scripts","photoshop","Exchange.ps1");
            using var process=Process.Start(start)!;
            var output=process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var errors=process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try {await process.WaitForExitAsync(timeout.Token);}
            catch {if(!process.HasExited)process.Kill(entireProcessTree:true);throw;}
            Assert.True(process.ExitCode==0,(await errors)+await output);
        }
        finally {Directory.Delete(folder,recursive:true);}
    }
}
