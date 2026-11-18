using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App;

internal static class RecentPreviews
{
    private static readonly SemaphoreSlim writer = new(1);
    private static string Folder => Path.Combine(AppPaths.Cache, "recent-previews");
    private static string CachePath(string path)
    {
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) full = full.ToUpperInvariant();
        return Path.Combine(Folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full))) + ".png");
    }

    internal static Bitmap? Load(string path)
    {
        if (!Settings.Persist) return null;
        try
        {
            var cache = CachePath(path);
            return File.Exists(cache) && File.GetLastWriteTimeUtc(cache) == File.GetLastWriteTimeUtc(path) ? new Bitmap(cache) : null;
        }
        catch (Exception) { return null; } // A thumbnail is optional, including when a removable drive disappears.
    }

    internal static async Task Remember(string path, Document snapshot)
    {
        if (!Settings.Persist) return;
        try
        {
            var modified = File.GetLastWriteTimeUtc(path);
            await writer.WaitAsync();
            try
            {
                await Task.Run(() =>
                {
                    var cache = CachePath(path);
                    if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) != modified ||
                        File.Exists(cache) && File.GetLastWriteTimeUtc(cache) == modified) return;
                    var scale = Math.Min(1, 96d / Math.Max(snapshot.Width, snapshot.Height));
                    using var sample = Pixels.NewColor(Math.Max(1, (int)Math.Ceiling(snapshot.Width * scale)), Math.Max(1, (int)Math.Ceiling(snapshot.Height * scale)));
                    DocumentRenderer.Render(snapshot, sample, new SKRectI(0, 0, sample.Width, sample.Height), new RenderView((float)scale, SKPoint.Empty));
                    using var encoded = sample.Encode(SKEncodedImageFormat.Png, 90);
                    Directory.CreateDirectory(Folder);
                    var temp = cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        using (var file = File.Create(temp)) encoded.SaveTo(file);
                        File.SetLastWriteTimeUtc(temp, modified);
                        File.Move(temp, cache, true);
                    }
                    finally { File.Delete(temp); }
                    foreach (var stale in new DirectoryInfo(Folder).EnumerateFiles("*.png").OrderByDescending(f => f.CreationTimeUtc).Skip(32))
                        stale.Delete();
                });
            }
            finally { writer.Release(); }
        }
        catch (Exception) { /* Preview failures must never turn a successful save into an error. */ }
    }
}
