using Composa.App.Assistant;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Composa.App.Tests;

public class LlamaLauncherTests
{
    [AvaloniaFact]
    public async Task Assistant_settings_exposes_a_llama_folder_and_fits_the_existing_dialog_style()
    {
        var owner = new Window { Width = 800, Height = 740 }; owner.Show();
        var settings = new Settings(); var showing = AssistantDialogs.Settings(owner, settings);
        var dialog = Assert.Single(owner.OwnedWindows); Dispatcher.UIThread.RunJobs();
        Assert.Contains(dialog.GetVisualDescendants().OfType<Button>(), b => (b.Content as string)?.StartsWith("Choose folder & create launcher", StringComparison.Ordinal) == true);
        Assert.True(dialog.Bounds.Width <= owner.Bounds.Width); Assert.True(dialog.Bounds.Height <= owner.Bounds.Height);
        Screenshots.Save(dialog, "assistant-llama-folder-settings");
        dialog.Close(false); Assert.False(await showing); owner.Close();
    }

    [Fact]
    public void Portable_launcher_keeps_libraries_and_matches_application_arguments()
    {
        // Create only under the test build, never in an unrelated llama installation.
        var directory = Path.Combine(AppContext.BaseDirectory, "launcher-fixtures", Guid.NewGuid().ToString()); Directory.CreateDirectory(directory);
        var exe = Path.Combine(directory, OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server"); File.WriteAllText(exe, "fixture");
        var library = Path.Combine(directory, "ggml-library.dll"); File.WriteAllText(library, "keep");
        var model = Path.Combine(directory, "Модель & 100% !.gguf"); File.WriteAllText(model, "fixture");
        var settings = new Settings { AssistantServerExecutable = exe, AssistantModelPath = model, AssistantServerUrl = "http://127.0.0.1:8095", AssistantContextSize = 32768 };
        Assert.Equal(exe, LlamaLauncher.FindExecutable(directory));
        var path = LlamaLauncher.Write(directory, settings); var script = File.ReadAllText(path);
        Assert.Contains("8095", script); Assert.Contains("32768", script); Assert.Contains("--jinja", script); Assert.Contains("--no-webui", script);
        Assert.DoesNotContain(directory, script); Assert.Equal("keep", File.ReadAllText(library));
        if (OperatingSystem.IsWindows()) { Assert.Contains("DisableDelayedExpansion", script); Assert.Contains("100%%", script); Assert.Contains("%~dp0", script); }
        settings.AssistantServerUrl = "http://example.com:8095"; Assert.Throws<FormatException>(() => LlamaLauncher.Arguments(settings));
        Directory.Delete(directory, recursive: true);
    }
}
