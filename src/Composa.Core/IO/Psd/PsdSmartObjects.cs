using System.Text;
using Composa.Model;
using SkiaSharp;

namespace Composa.IO.Psd;

internal static class PsdSmartObjects
{
    public static Dictionary<string, byte[]> Embedded(PsdFile file)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var extras in new[] { file.Extra }.Concat(file.Layers.Select(l => l.Extra)))
        foreach (var key in new[] { "lnkD", "lnk2", "lnk3" })
        {
            if (!extras.TryGetValue(key, out var data)) continue;
            try
            {
                var c = new PsdCursor(data);
                while (c.Remaining >= 8)
                {
                    var size = c.U64();
                    if (size > int.MaxValue) break;
                    var record = new PsdCursor(c.Bytes((long)size));
                    var type = record.Ascii(4); var version = record.U32();
                    if (version is < 1 or > 7) continue;
                    var id = Encoding.ASCII.GetString(record.Bytes(record.U8()));
                    _ = record.Unicode(); record.Skip(8);
                    var bytes = record.U64(); var open = record.U8() != 0;
                    if (open && PsdDescriptor.ReadVersioned(ref record) == null) continue;
                    // Never follow links on disk or retrieve remote linked files while opening a PSD.
                    if (type == "liFD" && bytes <= (ulong)record.Remaining) result.TryAdd(id, record.Bytes((long)bytes).ToArray());
                    var padding = (int)((4 - size % 4) % 4);
                    if (padding > 0 && c.Remaining >= padding) c.Skip(padding);
                }
            }
            catch (PsdException) { /* The compatibility pixels remain usable if linked metadata is damaged. */ }
        }
        return result;
    }

    public static (string? Id, double[]? Quad, bool Warped) Placement(PsdLayer record)
    {
        foreach (var key in new[] { "SoLE", "SoLd" })
        {
            if (!record.Extra.TryGetValue(key, out var bytes)) continue;
            try
            {
                var c = new PsdCursor(bytes);
                if (c.Ascii(4) != "soLD") continue;
                _ = c.U32(); var d = PsdDescriptor.ReadVersioned(ref c);
                if (d == null) continue;
                var id = PsdDescriptor.Text(d, "Idnt");
                var list = PsdDescriptor.List(d, "nonAffineTransform") ?? PsdDescriptor.List(d, "Trnf");
                var quad = list?.OfType<double>().ToArray();
                var warp = PsdDescriptor.Child(d, "warp");
                var warped = PsdDescriptor.Enumeration(warp, "warpStyle") is not (null or "warpNone") || PsdDescriptor.Child(d, "quiltWarp") != null;
                return (id, quad?.Length == 8 && quad.All(double.IsFinite) ? quad : null, warped);
            }
            catch (PsdException) { }
        }
        foreach (var key in new[] { "PlLd", "plLd" })
        {
            if (!record.Extra.TryGetValue(key, out var bytes)) continue;
            try
            {
                var c = new PsdCursor(bytes); if (c.Ascii(4) != "plcL") continue;
                _ = c.U32(); var id = Encoding.ASCII.GetString(c.Bytes(c.U8())); c.Skip(16);
                var quad = Enumerable.Range(0, 8).Select(_ => 0.0).ToArray();
                for (var i = 0; i < 8; i++) quad[i] = c.F64();
                return (id, quad.All(double.IsFinite) ? quad : null, false);
            }
            catch (PsdException) { }
        }
        return (null, null, false);
    }

    public static LayerTransform Place(double[] quad, int width, int height)
    {
        var left = Math.Min(Math.Min(quad[0], quad[2]), Math.Min(quad[4], quad[6]));
        var top = Math.Min(Math.Min(quad[1], quad[3]), Math.Min(quad[5], quad[7]));
        var right = Math.Max(Math.Max(quad[0], quad[2]), Math.Max(quad[4], quad[6]));
        var bottom = Math.Max(Math.Max(quad[1], quad[3]), Math.Max(quad[5], quad[7]));
        if (right - left < .001 || bottom - top < .001) throw new PsdException("A smart object has an invalid placement.");
        // Affine quads use scale/rotation/reflection; perspective quads use the editor's corner placement.
        if (Math.Abs(quad[0] + quad[4] - quad[2] - quad[6]) < .001 && Math.Abs(quad[1] + quad[5] - quad[3] - quad[7]) < .001)
            return PsdGeometry.Place(new SKMatrix((float)((quad[2] - quad[0]) / width), (float)((quad[6] - quad[0]) / height), (float)quad[0],
                (float)((quad[3] - quad[1]) / width), (float)((quad[7] - quad[1]) / height), (float)quad[1], 0, 0, 1), width, height);
        var offsets = new float[8];
        for (var i = 0; i < 4; i++) { offsets[i * 2] = (float)(quad[i * 2] - (i is 1 or 2 ? right : left)); offsets[i * 2 + 1] = (float)(quad[i * 2 + 1] - (i is 2 or 3 ? bottom : top)); }
        return new LayerTransform { X = left, Y = top, Width = right - left, Height = bottom - top, Distort = offsets };
    }
}
