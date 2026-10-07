using System.Diagnostics;
using Composa.Editing;
using Composa.IO;

namespace Composa.App;

public sealed partial class MainWindow
{
    private async Task SendToPhotoshop()
    {
        if (session is not { } target) return;
        try { await SendDocumentToPhotoshop(target, CancellationToken.None); ShowNote("Image sent to Photoshop."); }
        catch (Exception error) { ShowProblem(error.Message); }
    }

    internal async Task SendDocumentToPhotoshop(EditorSession target, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Photoshop exchange currently requires Windows.");
        if (target.IsInteracting || canvas.IsDragging) throw new InvalidOperationException("Finish the current edit before sending the image.");
        var helper = Path.Combine(AppContext.BaseDirectory, "scripts", "photoshop", "Exchange.ps1");
        if (!File.Exists(helper)) throw new FileNotFoundException("The Photoshop exchange helper is missing. Reinstall the complete Composa package.", helper);
        var image = Path.Combine(Path.GetTempPath(), "Composa-to-Photoshop-" + Guid.NewGuid().ToString("N") + ".png");
        using (var flat = target.Flatten()) await Task.Run(() => ImageFiles.Save(flat, image, ExportFormat.Png, 100), cancellation);
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", helper, "-Direction", "ToPhotoshop", "-ImagePath", image }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Couldn't start the Photoshop exchange helper.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(cancellation); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        if (process.ExitCode != 0) throw new InvalidOperationException("Couldn't send the image to Photoshop: " + (await error).Trim());
        await output;
    }
}
