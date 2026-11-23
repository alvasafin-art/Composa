using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SkiaSharp;

namespace Composa.App.AI;

internal static class LocalCodex
{
    internal static string? FindExecutable(string? selectedPath = null)
    {
        if (!string.IsNullOrWhiteSpace(selectedPath))
            return Path.IsPathFullyQualified(selectedPath) ? ResolveExecutable(selectedPath) : null;

        var file = OperatingSystem.IsWindows() ? "codex.exe" : "codex";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var path = directory.Trim('"');
            if (!Path.IsPathFullyQualified(path)) continue;
            if (ResolveExecutable(Path.Combine(path, file)) is { } executable)
                return executable;
            if (OperatingSystem.IsWindows() && ResolveExecutable(Path.Combine(path, "codex.cmd")) is { } bundled)
                return bundled;
        }
        return null;
    }

    private static string? ResolveExecutable(string path)
    {
        if (OperatingSystem.IsWindows() && !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var extension = Path.GetExtension(path);
            if (!File.Exists(path) || !Path.GetFileNameWithoutExtension(path).Equals("codex", StringComparison.OrdinalIgnoreCase)
                || !extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
                return null;
            var architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
                ? (Package: "arm64", Target: "aarch64") : (Package: "x64", Target: "x86_64");
            var package = Path.Combine(Path.GetDirectoryName(path)!, "node_modules", "@openai", "codex");
            var vendor = Path.Combine(architecture.Target + "-pc-windows-msvc", "bin", "codex.exe");
            var platformPackage = Path.Combine(package, "node_modules", "@openai", "codex-win32-" + architecture.Package, "vendor", vendor);
            path = File.Exists(platformPackage) ? platformPackage : Path.Combine(package, "vendor", vendor);
        }

        return File.Exists(path) ? Path.GetFullPath(path) : null;
    }

    internal static async Task<string> EnhanceAsync(string path, string model, string instructions, string message,
        CancellationToken cancellationToken)
    {
        var executable = FindExecutable(path);
        if (executable == null)
            throw new InvalidOperationException("Codex CLI was not found. Install Codex or select its executable.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        var workspace = Directory.CreateTempSubdirectory("composa-codex-");
        try
        {
            await VerifyLoginAsync(executable, workspace.FullName, token);

            var answerPath = Path.Combine(workspace.FullName, "answer.txt");
            var args = new List<string> { "exec", "--ephemeral", "--ignore-user-config", "--disable", "shell_tool",
                "--disable", "image_generation", "--skip-git-repo-check", "--sandbox", "read-only",
                "--output-last-message", answerPath };
            if (!string.IsNullOrWhiteSpace(model))
            {
                args.Add("--model");
                args.Add(model.Trim());
            }
            args.Add("-");
            var prompt = "Rewrite the image prompt as plain text. Do not use tools or access files. Return only the result.\n\n"
                + "Instructions:\n" + instructions + "\n\nImage prompt:\n" + message;
            var exitCode = await RunAsync(executable, workspace.FullName, args, prompt, token);
            if (exitCode != 0)
                throw new InvalidOperationException("Codex CLI could not run the selected model. Check its model ID and local sign-in.");
            if (!File.Exists(answerPath) || new FileInfo(answerPath).Length > 256 * 1024)
                throw new InvalidDataException("Codex CLI returned no usable text.");
            var answer = (await File.ReadAllTextAsync(answerPath, token)).Trim();
            return answer.Length > 0 ? answer : throw new InvalidDataException("Codex CLI returned no usable text.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Codex CLI timed out.");
        }
        finally
        {
            try { workspace.Delete(recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static async Task<SKBitmap> GenerateAsync(string path, string prompt, int width, int height,
        CancellationToken token, IReadOnlyList<byte[]>? images = null)
    {
        var executable = FindExecutable(path) ?? throw new InvalidOperationException("Codex CLI was not found. Install Codex or select its executable.");
        var workspace = Directory.CreateTempSubdirectory("composa-codex-");
        Guid? threadId = null;
        string? output = null;
        try
        {
            await VerifyLoginAsync(executable, workspace.FullName, token);
            var framing = width == height ? "square (1:1)" : width > height ? "landscape (3:2)" : "portrait (2:3)";
            var args = new List<string> { "exec", "--json", "--ephemeral", "--ignore-user-config", "--disable", "shell_tool",
                "--enable", "image_generation", "--skip-git-repo-check", "--sandbox", "read-only",
                "--model", "gpt-6.2-sol" };
            if (images != null)
            {
                for (var i = 0; i < images.Count; i++)
                {
                    var attachment = Path.Combine(workspace.FullName, $"image-{i + 1}.png");
                    await File.WriteAllBytesAsync(attachment, images[i], token);
                    args.Add("--image");
                    args.Add(attachment);
                }
            }
            args.Add("-");
            var input = $"Call the built-in image generation tool exactly once. Produce a {framing} raster image. "
                + "Use no other tools. Apply the following request to the attached images when present.\n\n" + prompt;
            var exitCode = await RunAsync(executable, workspace.FullName, args, input, token, line =>
            {
                if (threadId != null || !line.Contains("thread.started", StringComparison.Ordinal)) return;
                try
                {
                    using var json = JsonDocument.Parse(line);
                    var root = json.RootElement;
                    if (root.TryGetProperty("type", out var kind) && kind.GetString() == "thread.started"
                        && root.TryGetProperty("thread_id", out var id) && Guid.TryParse(id.GetString(), out var parsed))
                        threadId = parsed;
                }
                catch (JsonException) { }
            });
            if (exitCode != 0 || threadId == null)
                throw new InvalidOperationException("Codex CLI could not generate an image. Check its version and local sign-in.");

            var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(codexHome))
                codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            var directory = Path.Combine(codexHome, "generated_images", threadId.Value.ToString());
            var candidates = Directory.Exists(directory) ? Directory.GetFiles(directory, "exec-*.png") : [];
            if (candidates.Length != 1 || (File.GetAttributes(candidates[0]) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Codex CLI did not return exactly one generated PNG.");
            output = candidates[0];
            using var file = File.OpenRead(output);
            return DirectImageGenerator.Decode(file);
        }
        finally
        {
            if (output != null)
            {
                try { File.Delete(output); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            try { workspace.Delete(recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task VerifyLoginAsync(string executable, string workspace, CancellationToken token)
    {
        if (await RunAsync(executable, workspace, ["login", "status"], null, token) != 0)
            throw new InvalidOperationException("Codex CLI is not signed in. Run codex login on this computer.");
    }

    private static async Task<int> RunAsync(string executable, string workingDirectory, IEnumerable<string> args, string? input,
        CancellationToken token, Action<string>? output = null)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Win32Exception)
        {
            throw new InvalidOperationException("Composa could not start Codex CLI. Check the selected executable.");
        }

        var stdout = output == null ? process.StandardOutput.BaseStream.CopyToAsync(Stream.Null)
            : ReadEventsAsync(process.StandardOutput, output, token);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            using (var stdin = process.StandardInput)
            {
                if (input != null)
                    await stdin.WriteAsync(input.AsMemory(), token);
            }
            await process.WaitForExitAsync(token);
            await Task.WhenAll(stdout, stderr).WaitAsync(token);
            return process.ExitCode;
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();
            throw;
        }
    }

    private static async Task ReadEventsAsync(StreamReader reader, Action<string> accept, CancellationToken token)
    {
        while (await reader.ReadLineAsync(token) is { } line) accept(line);
    }
}
