using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Composa.Editing;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

internal static class FinalIo
{
    internal static void Run(string fixtures, string output)
    {
        Directory.CreateDirectory(output);
        var results = new List<object>();
        foreach (var name in new[] { "card", "banner" })
        {
            var input = Path.Combine(fixtures, name + ".cmps");
            var copy = Path.Combine(output, name + ".cmps");
            var png = Path.Combine(output, name + ".png");
            var original = ProjectFile.Load(input);
            var nested = original.AllLayers().Single(layer => layer.IsSmartObject).SmartObject!.OpenDocument();
            var masks = original.AllLayers().Where(layer => layer.Mask != null).Select(layer => Hash(layer.Mask!)).ToArray();
            Require(masks.Length != 0 && nested.Layers.Count != 0, "The project fixtures need masks and embedded content.");
            using var flatten = DocumentRenderer.Flatten(original);
            var pixels = Hash(flatten);
            var profile = Convert.ToHexString(SHA256.HashData(original.Profile.IccBytes()));
            ProjectFile.Save(original, copy);
            using (var a = ZipFile.OpenRead(input))
            using (var b = ZipFile.OpenRead(copy))
            {
                foreach (var entry in a.Entries.Where(entry => entry.FullName.StartsWith("images/", StringComparison.Ordinal)))
                {
                    var match = b.GetEntry(entry.FullName);
                    Require(match != null && match.Length == entry.Length && match.Crc32 == entry.Crc32,
                        "The CMPS raster entries changed after resave.");
                }
            }
            var reopened = ProjectFile.Load(copy);
            using (var flat = DocumentRenderer.Flatten(reopened)) Require(Hash(flat) == pixels, "CMPS pixels changed on reopen.");
            Require(reopened.Depth == original.Depth && reopened.Profile.Equals(original.Profile), "CMPS color mode or profile changed.");
            Require(reopened.AllLayers().Where(layer => layer.Mask != null).Select(layer => Hash(layer.Mask!)).SequenceEqual(masks), "CMPS masks changed.");
            var session = new EditorSession(reopened);
            var layer = session.Document.AllLayers().First(candidate => candidate.Mask != null);
            var id = layer.Id;
            var title = layer.Name;
            session.Rename(layer, "Proof rename");
            Require(session.History.Count == 1, "The edit did not create exactly one earlier state.");
            session.Undo();
            Require(session.Document.Find(id)!.Name == title, "Undo did not restore the document.");
            session.Redo();
            Require(session.Document.Find(id)!.Name == "Proof rename", "Redo did not restore the change.");

            const double dpi = 254;
            var reference = Convert.ToHexString(SHA256.HashData(ImageFiles.Encode(flatten, ExportFormat.Png, 100, resolution: dpi)));
            ImageFiles.Save(flatten, png, ExportFormat.Png, 100, dpi);
            var observations = new List<object>();
            for (var i = 0; i < 5; i++)
            {
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var started = Stopwatch.GetTimestamp();
                ImageFiles.Save(flatten, png, ExportFormat.Png, 100, dpi);
                observations.Add(new { ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    bytes = GC.GetAllocatedBytesForCurrentThread() - allocated });
            }
            var digest = HashFile(png);
            Require(digest == reference, "File export and in-memory PNG differ.");
            using var loadedPng = ImageFiles.Load(png, preserveProfile: true);
            Require(Pixels.DepthOf(loadedPng) == original.Depth && Pixels.ProfileOf(loadedPng).Equals(original.Profile), "PNG ICC or depth changed.");
            Require(Math.Abs(ImageResolution.Read(png) - dpi) < .01, "PNG PPI did not round-trip.");
            try
            {
                ImageFiles.Save(flatten, png, ExportFormat.Png, cancellationToken: new CancellationToken(canceled: true));
                throw new InvalidOperationException("Cancelled PNG save completed.");
            }
            catch (OperationCanceledException) { }
            Require(HashFile(png) == digest && !Directory.EnumerateFiles(output, name + ".png.tmp-*").Any(), "Cancelled PNG save changed the target or left a temp file.");
            results.Add(new { name, depth = original.Depth.ToString(), profile, pixels, masks, png = digest, dpi = ImageResolution.Read(png),
                pngBytes = new FileInfo(png).Length, cmpsBytes = new FileInfo(copy).Length, observations });
        }
        File.WriteAllText(Path.Combine(output, "png.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Hash(SKBitmap bitmap) => Convert.ToHexString(SHA256.HashData(bitmap.GetPixelSpan()));
    private static string HashFile(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
