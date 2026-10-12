using System.Globalization;
using System.Text;

namespace Composa.App.Assistant;

/// <summary>One launch contract for automatic startup and the portable launcher.</summary>
public static class LlamaLauncher
{
    public static string ResolveExecutable(string value)
    {
        if (Directory.Exists(value)) return FindExecutable(value);
        if (File.Exists(value)) return Path.GetFullPath(value);
        var name = string.IsNullOrWhiteSpace(value) ? (OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server") : value;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(directory.Trim('"'), name);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        throw new FileNotFoundException("Choose the llama.cpp folder or add llama-server to PATH. Bonsai PQ2_0 requires the PrismML runtime.", value);
    }

    public static string FindExecutable(string directory)
    {
        var name = OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";
        foreach (var relative in new[] { name, Path.Combine("bin", name), Path.Combine("build", "bin", name) })
        {
            var path = Path.Combine(directory, relative);
            if (File.Exists(path)) return Path.GetFullPath(path);
        }
        throw new FileNotFoundException($"The selected folder must contain {name} (or bin/{name}), together with its libraries.");
    }

    public static IReadOnlyList<string> Arguments(Settings settings)
    {
        if (!Uri.TryCreate(settings.AssistantServerUrl, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme is not ("http" or "https"))
            throw new FormatException("A llama.cpp launcher needs a localhost server URL.");
        if (uri.Scheme != "http" || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new FormatException("For automatic startup use http://localhost:port without a path or credentials.");
        List<string> args = ["--model", Path.GetFullPath(settings.AssistantModelPath), "--host", uri.Host, "--port", uri.Port.ToString(CultureInfo.InvariantCulture),
            "--ctx-size", Math.Clamp(settings.AssistantContextSize, 2048, 131072).ToString(CultureInfo.InvariantCulture),
            "--parallel", "1", "--jinja", "--reasoning", "auto", "--no-webui", "--n-gpu-layers", settings.AssistantGpuLayers.ToString(CultureInfo.InvariantCulture)];
        if (!string.IsNullOrWhiteSpace(settings.AssistantProjectorPath))
        {
            if (!File.Exists(settings.AssistantProjectorPath))
                throw new FileNotFoundException("Choose an existing matching vision projector GGUF in Assistant Settings.", settings.AssistantProjectorPath);
            args.AddRange(["--mmproj", Path.GetFullPath(settings.AssistantProjectorPath)]);
        }
        return args;
    }

    public static string Write(string directory, Settings settings)
    {
        directory = Path.GetFullPath(directory);
        if (!File.Exists(settings.AssistantModelPath))
            throw new FileNotFoundException("Choose an existing llama-server executable and GGUF model before creating the launcher.");
        var windows = OperatingSystem.IsWindows();
        var path = Path.Combine(directory, windows ? "run-composa-assistant.cmd" : "run-composa-assistant.sh");
        string Portable(string value)
        {
            if (!Path.IsPathFullyQualified(value)) return value;
            var relative = Path.GetRelativePath(directory, value);
            return relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) ? value : relative;
        }
        string Quote(string value)
        {
            if (value.Contains('\r') || value.Contains('\n') || value.Contains('\0')) throw new FormatException("Launcher arguments cannot contain line breaks.");
            return windows ? "\"" + value.Replace("%", "%%").Replace("\"", "") + "\"" : "'" + value.Replace("'", "'\"'\"'") + "'";
        }
        var arguments = Arguments(settings).Select((v, i) => Quote(Portable(i == 1 ? Path.GetFullPath(v) : v)));
        var executable = Portable(ResolveExecutable(settings.AssistantServerExecutable));
        if (!Path.IsPathFullyQualified(executable)) executable = "." + Path.DirectorySeparatorChar + executable;
        var command = Quote(executable) + " " + string.Join(" ", arguments);
        var contents = windows
            ? "@echo off\r\nsetlocal DisableDelayedExpansion\r\nchcp 65001 >nul\r\ncd /d \"%~dp0\"\r\n" + command + "\r\npause\r\n"
            : "#!/bin/sh\ncd -- \"$(dirname -- \"$0\")\" || exit 1\nexec " + command + "\n";
        File.WriteAllText(path, contents, new UTF8Encoding(false));
        if (!windows) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
