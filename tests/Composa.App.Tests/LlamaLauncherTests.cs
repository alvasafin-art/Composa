using Composa.App.Assistant;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Composa.App.Tests;

public class LlamaLauncherTests
{
    [Theory]
    [InlineData("{\"modalities\":{\"vision\":false}}", true, true)]
    [InlineData("{\"modalities\":{\"vision\":true}}", true, false)]
    [InlineData("{\"modalities\":{\"vision\":false}}", false, false)]
    [InlineData("{}", true, false)]
    public async Task Vision_checks_server_capabilities_even_without_a_selected_model(string properties, bool vision, bool rejected)
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var serving = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var client = await listener.AcceptTcpClientAsync(deadline.Token);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                var request = await reader.ReadLineAsync(deadline.Token);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(deadline.Token))) { }
                var body = request!.Split(' ')[1] == "/props" ? properties : "{}";
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), deadline.Token);
            }
        }, deadline.Token);
        using var host = new LlamaServerHost(new Settings { AssistantServerUrl = $"http://127.0.0.1:{port}", AssistantVision = vision, AssistantAutoStart = false });
        if (rejected)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.EnsureReadyAsync(deadline.Token));
            Assert.Contains("matching --mmproj", error.Message);
            Assert.StartsWith("Not ready", host.Status);
        }
        else { await host.EnsureReadyAsync(deadline.Token); Assert.Equal("Ready", host.Status); }
        await serving;
    }

    [Fact]
    public async Task External_loading_server_is_reused_and_wrong_model_is_reported_without_launching_another_process()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var paths = new List<string>();
        var serving = Task.Run(async () =>
        {
            for (var i = 0; i < 7; i++)
            {
                using var client = await listener.AcceptTcpClientAsync(deadline.Token);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                var request = await reader.ReadLineAsync(deadline.Token); paths.Add(request!.Split(' ')[1]);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(deadline.Token))) { }
                var body = paths[^1] == "/props" ? "{\"model_path\":\"/models/selected.gguf\",\"default_generation_settings\":{\"n_ctx\":16384}}" : "{}";
                var response = $"HTTP/1.1 {(i == 0 ? "503 Service Unavailable" : "200 OK")}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(response), deadline.Token);
            }
        }, deadline.Token);
        var settings = new Settings { AssistantServerUrl = $"http://127.0.0.1:{port}/v1", AssistantModelPath = "/chosen/selected.gguf", AssistantServerExecutable = "does-not-exist" };
        using (var host = new LlamaServerHost(settings))
        {
            await host.EnsureReadyAsync(deadline.Token); Assert.Equal("Ready", host.Status);
            settings.AssistantModelPath = "/chosen/different.gguf";
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.EnsureReadyAsync(deadline.Token));
            Assert.Contains("different model", error.Message);
            settings.AssistantModelPath = "/chosen/selected.gguf"; settings.AssistantContextSize = 32768;
            error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.EnsureReadyAsync(deadline.Token));
            Assert.Contains("only 16384 context tokens", error.Message);
        }
        await serving;
        Assert.Equal(new[] { "/health", "/health", "/props", "/health", "/props", "/health", "/props" }, paths);
    }

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
        var projector = Path.Combine(directory, "vision projector.gguf"); File.WriteAllText(projector, "fixture");
        var settings = new Settings { AssistantServerExecutable = exe, AssistantModelPath = model, AssistantProjectorPath = projector, AssistantServerUrl = "http://127.0.0.1:8095", AssistantContextSize = 32768 };
        Assert.Equal(exe, LlamaLauncher.FindExecutable(directory));
        Assert.Equal(exe, LlamaLauncher.ResolveExecutable(directory)); Assert.Equal(exe, LlamaLauncher.ResolveExecutable(exe));
        var path = LlamaLauncher.Write(directory, settings); var script = File.ReadAllText(path);
        Assert.Contains("8095", script); Assert.Contains("32768", script); Assert.Contains("--jinja", script); Assert.Contains("--no-webui", script);
        Assert.Contains("auto", script); Assert.Contains("--n-gpu-layers", script);
        Assert.Contains("--mmproj", script); Assert.Contains("vision projector.gguf", script);
        Assert.DoesNotContain(directory, script); Assert.Equal("keep", File.ReadAllText(library));
        if (OperatingSystem.IsWindows()) { Assert.Contains("DisableDelayedExpansion", script); Assert.Contains("100%%", script); Assert.Contains("%~dp0", script); }
        settings.AssistantProjectorPath = Path.Combine(directory, "missing.gguf"); Assert.Throws<FileNotFoundException>(() => LlamaLauncher.Arguments(settings));
        settings.AssistantServerUrl = "http://example.com:8095"; Assert.Throws<FormatException>(() => LlamaLauncher.Arguments(settings));
        Directory.Delete(directory, recursive: true);
    }
}
