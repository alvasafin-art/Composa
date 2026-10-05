using System.Security.Cryptography;

namespace Composa.Vision;

/// <summary>
/// One on-device model file: where it is, what it must hash to, and whose it is. The subject and upscale catalogs
/// derive their records from it, so the same rules hold for every model: a permissive licence, a pinned hash
/// checked before the first load, and a file that is never downloaded at run time.
/// </summary>
public abstract record OnnxModel(
    string Id,
    string Name,
    string File,
    string Sha256,
    long Bytes,
    string Licence,
    string Attribution,
    string Source,
    string Url)
{
    /// <summary>The file's full path in the models folder, whether or not it is there.</summary>
    public string Path => System.IO.Path.Combine(OnnxModels.Directory, File);

    /// <summary>Whether the file is on this machine. Never reads it; the hash is checked when it is first loaded.</summary>
    public bool IsInstalled => System.IO.File.Exists(Path);

    /// <summary>Whether the file on disk is the one this record names. A damaged or swapped file is unavailable, not a crash.</summary>
    public bool Verify()
    {
        if (!IsInstalled) return false;
        try
        {
            using var stream = System.IO.File.OpenRead(Path);
            if (stream.Length != Bytes) return false;
            return Convert.ToHexStringLower(SHA256.HashData(stream)) == Sha256;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

public static class OnnxModels
{
    /// <summary>
    /// Where the model files are: <c>COMPOSA_MODELS_DIR</c> when set, otherwise the <c>models</c> folder beside the
    /// application, which is where the build puts them. Nothing is ever downloaded at run time.
    /// </summary>
    public static string Directory =>
        Environment.GetEnvironmentVariable("COMPOSA_MODELS_DIR") is { Length: > 0 } dir ? dir : System.IO.Path.Combine(AppContext.BaseDirectory, "models");
}
