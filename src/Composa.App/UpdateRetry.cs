using System.Text;

namespace Composa.App;

/// <summary>One retry for an installation the user already asked to update.</summary>
internal sealed class UpdateRetry(string installationRoot)
{
    private string FilePath => Path.Combine(installationRoot, "composa-update-retry");

    internal void Arm(string version) => File.WriteAllText(FilePath, version);

    internal bool Take(string version)
    {
        // Claim the retry before launching the updater. A concurrent editor launch
        // or another updater restart cannot consume the same request again.
        using var file = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (file.Length is 0 or > 256) return false;
        using var reader = new StreamReader(file, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadToEnd() != version) return false;
        file.SetLength(0);
        return true;
    }

    internal void Cancel() => File.Delete(FilePath);
}
