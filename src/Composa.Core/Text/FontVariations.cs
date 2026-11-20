using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarfBuzzSharp;
using SkiaSharp;

namespace Composa.Text;

/// <summary>Read installed fvar axes and instantiate the chosen coordinates for both Skia measurement and drawing.</summary>
public static class FontVariations
{
    public sealed record Axis(string Tag, double Min, double Default, double Max);
    private static readonly ConditionalWeakTable<SKTypeface, Axis[]> available = new();
    private static readonly LinkedList<(SKTypeface Source, string Coordinates, SKTypeface Font)> instances = new();
    public static IReadOnlyList<Axis> Axes(SKTypeface font) => available.GetValue(font, static face =>
    {
        if (!face.TryGetTableData(0x66766172, out var data) || data.Length < 16) return [];
        int U16(int i) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i));
        double Fixed(int i) => BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(i)) / 65536d;
        int offset = U16(4), count = U16(8), size = U16(10);
        if (size < 20 || count > 64 || offset + (long)count * size > data.Length) return [];
        var axes = new List<Axis>();
        for (int n = 0; n < count; n++, offset += size)
        {
            var axis = new Axis(System.Text.Encoding.ASCII.GetString(data, offset, 4), Fixed(offset + 4), Fixed(offset + 8), Fixed(offset + 12));
            if (axis.Min <= axis.Default && axis.Default <= axis.Max && axis.Max > axis.Min) axes.Add(axis);
        }
        return axes.ToArray();
    });
    public static double Value(string coordinates, Axis axis)
    {
        foreach (var pair in coordinates.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (pair.StartsWith(axis.Tag + "=", StringComparison.Ordinal) && double.TryParse(pair.AsSpan(5), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v))
                return Math.Clamp(v, axis.Min, axis.Max);
        return axis.Default;
    }
    public static string WithValue(string current, Axis axis, double value)
    {
        var pairs = current.Split(',', StringSplitOptions.RemoveEmptyEntries).Where(p => !p.StartsWith(axis.Tag + "=", StringComparison.Ordinal)).ToList();
        if (value != axis.Default) pairs.Add(axis.Tag + "=" + value.ToString("R", CultureInfo.InvariantCulture));
        pairs.Sort(StringComparer.Ordinal); return string.Join(',', pairs);
    }
    internal static SKTypeface Instance(SKTypeface source, string coordinates)
    {
        if (coordinates.Length == 0 || Axes(source).Count == 0) return source;
        lock (instances)
        {
            for (var n = instances.First; n != null; n = n.Next)
                if (n.Value.Source == source && n.Value.Coordinates == coordinates)
                { instances.Remove(n); instances.AddFirst(n); return n.Value.Font; }
            var font = Instantiate(source, coordinates) ?? source;
            instances.AddFirst((source, coordinates, font));
            if (instances.Count > 12) instances.RemoveLast();
            return font;
        }
    }
    // HarfBuzzSharp does not expose the HarfBuzz subset API. Pinning axes produces a static OpenType
    // instance, so glyph advances, outlines, print and raster exports all use the same coordinates.
    private const string Library = "libHarfBuzzSharp";
    [DllImport(Library)] private static extern IntPtr hb_subset_input_create_or_fail();
    [DllImport(Library)] private static extern void hb_subset_input_destroy(IntPtr input);
    [DllImport(Library)] private static extern IntPtr hb_subset_input_unicode_set(IntPtr input);
    [DllImport(Library)] private static extern void hb_set_add_range(IntPtr set, uint first, uint last);
    [DllImport(Library)] private static extern int hb_subset_input_pin_axis_location(IntPtr input, IntPtr face, uint tag, float value);
    [DllImport(Library)] private static extern IntPtr hb_subset_or_fail(IntPtr face, IntPtr input);
    [DllImport(Library)] private static extern IntPtr hb_face_reference_blob(IntPtr face);
    [DllImport(Library)] private static extern IntPtr hb_blob_get_data(IntPtr blob, out uint length);
    [DllImport(Library)] private static extern void hb_blob_destroy(IntPtr blob);
    [DllImport(Library)] private static extern void hb_face_destroy(IntPtr face);
    private static SKTypeface? Instantiate(SKTypeface source, string settings)
    {
        using var stream = source.OpenStream(out int index);
        if (stream == null) return null;
        using var data = SKData.Create(stream);
        using var blob = new Blob(data.Data, checked((int)data.Size), MemoryMode.ReadOnly);
        using var face = new Face(blob, index);
        var input = hb_subset_input_create_or_fail(); if (input == IntPtr.Zero) return null;
        IntPtr subset = IntPtr.Zero, result = IntPtr.Zero;
        try
        {
            hb_set_add_range(hb_subset_input_unicode_set(input), 0, 0x10ffff);
            foreach (var a in Axes(source))
                if (hb_subset_input_pin_axis_location(input, face.Handle, Tag.Parse(a.Tag), (float)Value(settings, a)) == 0) return null;
            subset = hb_subset_or_fail(face.Handle, input);
            if (subset == IntPtr.Zero) return null;
            result = hb_face_reference_blob(subset);
            var ptr = hb_blob_get_data(result, out uint length);
            if (ptr == IntPtr.Zero || length == 0) return null;
            using var instance = SKData.CreateCopy(ptr, (int)length);
            return SKTypeface.FromData(instance);
        }
        finally
        {
            if (result != IntPtr.Zero) hb_blob_destroy(result);
            if (subset != IntPtr.Zero) hb_face_destroy(subset);
            hb_subset_input_destroy(input);
        }
    }
}
