using System.Text.Json;
using System.Text.RegularExpressions;
using Composa.AI;

namespace Composa.App.Automation;

public sealed record AutomationCommand(string Id, string Title, string File, string? PluginId = null, bool AllowExport = true);
public sealed record ScriptPluginCommand(string Id, string Title, string Script);
public sealed record ScriptPluginManifest(string Id, string Name, string Version, int ApiVersion, IReadOnlyList<ScriptPluginCommand> Commands)
{
    public IReadOnlyList<string> Permissions { get; init; } = [];
}
public sealed record InstalledScriptPlugin(ScriptPluginManifest Manifest, string Directory);

/// <summary>Explicitly installed JavaScript commands. Packages never execute code while loading.</summary>
public sealed partial class AutomationCatalog(string? root) : IPluginCommandProvider
{
    public string? Root { get; } = root == null ? null : Path.GetFullPath(root);
    public List<AutomationCommand> Commands { get; } = [];
    public List<InstalledScriptPlugin> Plugins { get; } = [];
    public List<string> Errors { get; } = [];
    public IEnumerable<string> CommandIds => Commands.Where(command => command.PluginId != null).Select(command => command.Id);
    private const int MaximumScriptBytes = 256_000, MaximumCommands = 32, MaximumPackageBytes = 2_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public void Reload(IEnumerable<string>? disabledPlugins = null)
    {
        Commands.Clear(); Plugins.Clear(); Errors.Clear();
        if (Root == null) return;
        if (Directory.Exists(Root))
            try { RejectLink(Root); }
            catch (Exception error) { Errors.Add(error.Message); return; }
        var disabled = new HashSet<string>(disabledPlugins ?? [], StringComparer.OrdinalIgnoreCase);
        var scripts = Path.Combine(Root, "scripts");
        if (Directory.Exists(scripts))
            foreach (var path in Entries(scripts, folders: false))
                try
                {
                    ReadScript(path);
                    Commands.Add(new("script:" + Path.GetFileName(path), Path.GetFileNameWithoutExtension(path), path));
                }
                catch (Exception error) { Errors.Add(Path.GetFileName(path) + ": " + error.Message); }
        var plugins = Path.Combine(Root, "plugins");
        if (!Directory.Exists(plugins)) return;
        foreach (var directory in Entries(plugins, folders: true))
            try
            {
                RejectLink(directory);
                var manifest = Validate(Path.Combine(directory, "plugin.json"));
                if (!string.Equals(Path.GetFileName(directory), manifest.Id, StringComparison.Ordinal))
                    throw new InvalidDataException("Plugin folder must match its id.");
                Plugins.Add(new(manifest, directory));
                if (disabled.Contains(manifest.Id)) continue;
                Commands.AddRange(manifest.Commands.Select(command => new AutomationCommand("plugin:" + manifest.Id + ":" + command.Id,
                    manifest.Name + " · " + command.Title, PackagePath(directory, command.Script), manifest.Id, manifest.Permissions.Contains("export"))));
            }
            catch (Exception error) { Errors.Add(Path.GetFileName(directory) + ": " + error.Message); }
    }

    private IEnumerable<string> Entries(string directory, bool folders)
    {
        try
        {
            RejectLink(directory);
            return (folders ? Directory.EnumerateDirectories(directory) : Directory.EnumerateFiles(directory, "*.js"))
                .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception error) { Errors.Add(Path.GetFileName(directory) + ": " + error.Message); return []; }
    }

    public static ScriptPluginManifest Validate(string manifestPath)
    {
        var path = Path.GetFullPath(manifestPath);
        RejectLink(path);
        if (new FileInfo(path).Length > MaximumScriptBytes) throw new InvalidDataException("Plugin manifest is too large.");
        var manifest = JsonSerializer.Deserialize<ScriptPluginManifest>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Plugin manifest is empty.");
        if (manifest.Id == null || !Identifier().IsMatch(manifest.Id) || Reserved().IsMatch(manifest.Id))
            throw new InvalidDataException("Use a safe lowercase plugin id (letters, numbers and hyphens).");
        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 100 || string.IsNullOrWhiteSpace(manifest.Version))
            throw new InvalidDataException("Plugin name and version are required.");
        if (manifest.ApiVersion != 1) throw new InvalidDataException("This plugin needs an unsupported scripting API version.");
        if (manifest.Commands == null || manifest.Commands.Count is < 1 or > MaximumCommands)
            throw new InvalidDataException($"A plugin must contain 1–{MaximumCommands} commands.");
        if (manifest.Permissions == null || manifest.Permissions.Any(permission => permission != "export"))
            throw new InvalidDataException("Only the optional export permission is supported.");
        var directory = Path.GetDirectoryName(path)!;
        RejectLink(directory);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (var command in manifest.Commands)
        {
            if (command.Id == null || !Identifier().IsMatch(command.Id) || !ids.Add(command.Id) || string.IsNullOrWhiteSpace(command.Title) || command.Title.Length > 100)
                throw new InvalidDataException("Plugin command ids must be unique; each command needs a title.");
            var script = PackagePath(directory, command.Script);
            bytes += new FileInfo(script).Length;
            ReadScript(script);
            if (bytes > MaximumPackageBytes) throw new InvalidDataException("Plugin scripts exceed the package size limit.");
        }
        return manifest;
    }

    public void Install(string manifestPath)
    {
        var manifest = Validate(manifestPath);
        var directory = ManagedRoot();
        var destination = Path.Combine(directory, "plugins", manifest.Id);
        if (Directory.Exists(destination)) throw new InvalidOperationException("This plugin is already installed. Remove it before installing a replacement.");
        var source = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var staging = Path.Combine(directory, ".install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var command in manifest.Commands.DistinctBy(command => command.Script))
            {
                var target = PackagePath(staging, command.Script, mustExist: false);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(PackagePath(source, command.Script), target);
            }
            File.WriteAllText(Path.Combine(staging, "plugin.json"), JsonSerializer.Serialize(manifest, JsonOptions));
            foreach (var notice in new[] { "LICENSE", "LICENSE.txt", "README.md" })
                if (File.Exists(Path.Combine(source, notice)))
                {
                    var sourceNotice = Path.Combine(source, notice); RejectLink(sourceNotice);
                    if (new FileInfo(sourceNotice).Length <= MaximumScriptBytes) File.Copy(sourceNotice, Path.Combine(staging, notice));
                }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); RejectLink(Path.GetDirectoryName(destination)!);
            Directory.Move(staging, destination);
        }
        catch
        {
            // Failed partial installs are kept recoverably outside the active plugin folder.
            var archive = Path.Combine(directory, "archive"); Directory.CreateDirectory(archive); RejectLink(archive);
            if (Directory.Exists(staging)) Directory.Move(staging, Path.Combine(archive, Path.GetFileName(staging)));
            throw;
        }
    }

    public string ImportScript(string path)
    {
        var script = ReadScript(path);
        return SaveScript(Path.GetFileNameWithoutExtension(path), script);
    }

    public string SaveScript(string name, string script, string? existingPath = null)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(script) > MaximumScriptBytes || script.Contains('\0'))
            throw new InvalidDataException("Scripts must be text, at most 256 KB.");
        var directory = Path.Combine(ManagedRoot(), "scripts"); Directory.CreateDirectory(directory); RejectLink(directory);
        string target;
        if (existingPath != null)
        {
            target = Path.GetFullPath(existingPath);
            if (!string.Equals(Path.GetDirectoryName(target), directory, StringComparison.OrdinalIgnoreCase) || Path.GetExtension(target) != ".js")
                throw new InvalidOperationException("Only a managed library script can be replaced.");
            if (File.Exists(target)) RejectLink(target);
        }
        else
        {
            var safe = string.Concat(name.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_' or ' ')).Trim();
            if (safe.Length == 0 || Reserved().IsMatch(safe)) safe = "Script";
            if (safe.Length > 80) safe = safe[..80];
            target = Path.Combine(directory, safe + ".js");
            for (var suffix = 2; File.Exists(target); suffix++) target = Path.Combine(directory, safe + " " + suffix + ".js");
        }
        // Keep an editable script's previous version for recovery.
        if (File.Exists(target))
        { if (File.Exists(target + ".bak")) RejectLink(target + ".bak"); File.Copy(target, target + ".bak", overwrite: true); }
        File.WriteAllText(target, script);
        return target;
    }

    public void RemovePlugin(string id)
    {
        var directory = ManagedRoot();
        var plugin = Plugins.Single(plugin => plugin.Manifest.Id == id);
        var target = Path.GetFullPath(plugin.Directory);
        if (target != Path.Combine(directory, "plugins", id)) throw new InvalidOperationException("Plugin is outside the managed folder.");
        RejectLink(target);
        var archive = Path.Combine(directory, "archive"); Directory.CreateDirectory(archive); RejectLink(archive);
        Directory.Move(target, Path.Combine(archive, id + "-" + Guid.NewGuid().ToString("N")));
    }

    public void RemoveScript(string id)
    {
        var directory = ManagedRoot();
        var script = Commands.Single(command => command.Id == id && command.PluginId == null);
        var target = Path.GetFullPath(script.File);
        var scripts = Path.Combine(directory, "scripts");
        if (!string.Equals(Path.GetDirectoryName(target), scripts, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Script is outside the managed library.");
        RejectLink(scripts); RejectLink(target);
        var archive = Path.Combine(directory, "archive"); Directory.CreateDirectory(archive); RejectLink(archive);
        var recovered = Path.Combine(archive, "script-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(recovered);
        File.Move(target, Path.Combine(recovered, Path.GetFileName(target)));
        if (File.Exists(target + ".bak"))
        { RejectLink(target + ".bak"); File.Move(target + ".bak", Path.Combine(recovered, Path.GetFileName(target) + ".bak")); }
    }

    public static string ReadScript(string path)
    {
        RejectLink(path);
        if (!string.Equals(Path.GetExtension(path), ".js", StringComparison.OrdinalIgnoreCase) || new FileInfo(path).Length > MaximumScriptBytes)
            throw new InvalidDataException("Choose a JavaScript file smaller than 256 KB.");
        var text = File.ReadAllText(path);
        if (text.Contains('\0')) throw new InvalidDataException("The script is not a text file.");
        if (text.TrimStart().StartsWith("#target photoshop", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Run Send-to-Composa.js in Photoshop (File > Scripts > Browse). In Composa use Scripts > Run Script File > Send-to-Photoshop.js.");
        return text;
    }

    private string ManagedRoot()
    {
        var directory = Root ?? throw new InvalidOperationException("No automation library is configured.");
        Directory.CreateDirectory(directory); RejectLink(directory);
        return directory;
    }

    private static string PackagePath(string directory, string relative, bool mustExist = true)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\\')
            || relative.Split('/').Any(part => part is "" or "." or "..") || Path.GetExtension(relative) != ".js")
            throw new InvalidDataException("Plugin scripts must be relative .js paths inside the package.");
        var rootPath = Path.GetFullPath(directory);
        var path = Path.GetFullPath(Path.Combine(rootPath, relative));
        if (!path.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Script escapes its package.");
        for (var current = path; current != rootPath; current = Path.GetDirectoryName(current)!)
            if (File.Exists(current) || Directory.Exists(current)) RejectLink(current);
        if (mustExist && !File.Exists(path)) throw new FileNotFoundException("Plugin script not found.", relative);
        return path;
    }

    private static void RejectLink(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Linked files/folders are not allowed in automation packages.");
    }
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)] private static partial Regex Identifier();
    [GeneratedRegex("^(con|prn|aux|nul|com[1-9]|lpt[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Reserved();
}
