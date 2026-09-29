using Composa.AI;

namespace Composa.App.AI;

public sealed class EngineCatalog
{
    public IReadOnlyList<EngineProfile> Profiles { get; }
    public IReadOnlyList<string> Errors { get; }
    public string Root { get; }

    public EngineCatalog(string root)
    {
        Root = root;
        var profiles = new List<EngineProfile>();
        var errors = new List<string>();
        if (Directory.Exists(root))
        {
            foreach (var manifest in Directory.EnumerateFiles(root, "manifest.json", SearchOption.AllDirectories).Order())
            {
                try { profiles.Add(EngineProfile.Load(manifest)); }
                catch (Exception error) { errors.Add($"{manifest}: {error.Message}"); }
            }
        }
        Profiles = profiles;
        Errors = errors;
    }

    public EngineProfile? Find(string? id) => Profiles.FirstOrDefault(profile => profile.Id == id) ?? Profiles.FirstOrDefault();
    public string DirectoryOf(EngineProfile profile) => Path.Combine(Root, profile.Id);
}

public static class AppPromptPresets
{
    public static IReadOnlyList<PromptPreset> Load(string root)
    {
        var result = new List<PromptPreset>();
        if (!Directory.Exists(root)) return result;
        foreach (var file in Directory.EnumerateFiles(root, "*.json").Order())
        {
            using var stream = File.OpenRead(file);
            result.AddRange(PromptPresetCatalog.Parse(stream));
        }
        return result;
    }
}
