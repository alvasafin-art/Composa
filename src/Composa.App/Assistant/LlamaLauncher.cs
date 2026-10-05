using System.Globalization;
using System.Text;

namespace Composa.App.Assistant;

/// <summary>One launch contract for automatic startup and the portable launcher.</summary>
public static class LlamaLauncher
{
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
        return ["--model", settings.AssistantModelPath, "--host", uri.Host, "--port", uri.Port.ToString(CultureInfo.InvariantCulture),
            "--ctx-size", Math.Clamp(settings.AssistantContextSize, 2048, 131072).ToString(CultureInfo.InvariantCulture),
            "--parallel", "1", "--jinja", "--reasoning", "off", "--no-webui"];
    }

    public static string Write(string directory, Settings settings)
    {
        directory = Path.GetFullPath(directory);
        if (!File.Exists(settings.AssistantServerExecutable) || !File.Exists(settings.AssistantModelPath))
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
        var executable = Portable(Path.GetFullPath(settings.AssistantServerExecutable));
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
