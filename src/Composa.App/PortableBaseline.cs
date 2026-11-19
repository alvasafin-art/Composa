using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Composa.App;

internal static class PortableBaseline
{
    private sealed record Entry(string Path, string? Source, string? Hash, string? Data);
    private sealed record Recipe(string Version, string Package, Entry[] Entries);

    public static void Ensure(string root, string cache, string version, string channel, CancellationToken token)
    {
        var name = $"ComposaStudio-{version}-{channel}-full.nupkg";
        var destination = Path.Combine(cache, name);
        var recipePath = Path.Combine(root, "portable-baseline.json");
        if (File.Exists(destination) || !File.Exists(recipePath)) return;
        var recipe = JsonSerializer.Deserialize<Recipe>(File.ReadAllBytes(recipePath));
        if (recipe?.Version != version || recipe.Package != name) return;
        Directory.CreateDirectory(cache);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                foreach (var entry in recipe.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    SafePath(root, entry.Path);
                    using var output = archive.CreateEntry(entry.Path, CompressionLevel.Fastest).Open();
                    if (entry.Source == null) { output.Write(Convert.FromBase64String(entry.Data!)); continue; }
                    using var input = File.OpenRead(SafePath(root, entry.Source));
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[81920];
                    int count;
                    while ((count = input.Read(buffer)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, count);
                        hash.AppendData(buffer, 0, count);
                    }
                    if (Convert.ToHexString(hash.GetHashAndReset()) != entry.Hash)
                        throw new InvalidDataException($"Portable installation file changed: {entry.Source}. Restore it from the original ZIP and retry.");
                }
            }
            try { File.Move(temporary, destination); }
            catch (IOException) when (File.Exists(destination)) { } // Another instance completed the same baseline.
        }
        finally { File.Delete(temporary); }
    }

    private static string SafePath(string root, string relative)
    {
        if (relative.Split('/').Any(p => p is "" or "." or "..") || relative.IndexOfAny([':', '\\']) >= 0)
            throw new InvalidDataException("Invalid portable package path.");
        return Path.Combine(root, relative);
    }
}
