using System.Buffers.Binary;
using System.Text;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.IO.Psd;

/// <summary>
/// Layered 8-bit RGB Photoshop interchange, written from Adobe's published specification. Channel rows go straight
/// to a seekable stream: saving does not hold a second copy of every layer or an entire compressed file in memory.
/// Native horizontal type and vector shapes carry editable descriptors alongside their raster caches. Unsupported
/// live features and effects are rendered into their own layers. Corrections that depend on the backdrop need a
/// merged appearance layer; their source layers remain in a hidden folder rather than claiming Photoshop semantics.
/// </summary>
public static partial class PsdExport
{
    public const string Extension = ".psd";
    public const long MaxFileBytes = int.MaxValue;
    public const string SourceFolder = "Composa source layers";
    public const string AppearanceLayer = "Composa appearance";

    public static IReadOnlyList<PsdConversion> Conversions(Document document)
    {
        var notes = new List<PsdConversion>();
        if (document.AlphaChannels.Count > 0) notes.Add(new("Saved selections", "Saved alpha channels stay in the Composa project and are not exported to PSD yet."));
        if (NeedsAppearance(document)) notes.Add(new("Document", "Adjustments are applied to a merged appearance layer. Source layers are kept in a hidden folder; adjustment settings stay in the Composa project."));
        foreach (var layer in document.AllLayers())
        {
            if (layer.FilterSource != null) notes.Add(new(layer.Name, "Smart filters are exported as the rendered appearance; their editable source and settings stay in the Composa project."));
            if (layer.VectorMask != null) notes.Add(new(layer.Name, "Vector masks are applied to the exported appearance; their editable paths stay in the Composa project."));
            if (layer.IsAdjustment) { notes.Add(new(layer.Name, "Adjustment settings cannot be represented exactly in Photoshop and are not exported as editable adjustments.")); continue; }
            if ((layer.Text != null || layer.Shape != null) && NativeProblem(layer, document) is { } problem)
                notes.Add(new(layer.Name, problem + " This layer is saved as pixels; keep a Composa project for editing."));
            if (layer.SmartObject != null) notes.Add(new(layer.Name, "Smart object content is saved as pixels; its embedded document stays in the Composa project."));
            if (layer.Effects?.Visible() is { IsEmpty: false }) notes.Add(new(layer.Name, "Layer effects are applied to pixels, together with the enabled mask. Their settings stay in the Composa project."));
            else if (layer.Effects is { IsEmpty: false }) notes.Add(new(layer.Name, "Disabled layer effect settings are not exported. They stay in the Composa project."));
            else if (!HasNativeContent(layer, document) && layer.Pixels is { } pixels && !layer.Transform.IsPureTranslation(pixels.Width, pixels.Height))
                notes.Add(new(layer.Name, "The transform is applied to pixels; the original pixels and transform stay in the Composa project."));
        }
        return notes;
    }

    private static bool NeedsAppearance(Document document) => document.AllLayers().Any(l => l.IsAdjustment);

    /// <summary>Writes beside the destination and replaces it only after a complete, flushed file exists.</summary>
    public static void Save(Document document, string path)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                Write(document, stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private sealed record Record(Layer Layer, SKRectI Bounds, int Section = 0, bool Appearance = false)
    {
        public bool HasEffects => Layer.Effects?.Visible() is { IsEmpty: false } || Layer.VectorMask != null && Layer.VectorMaskEnabled;
        public bool HasMask => Layer.Mask != null && !(HasEffects && Layer.MaskEnabled);
        public List<(short Id, long Position)> Channels { get; } = [];
        public SKRectI MaskBounds => Layer.IsGroup ? new(0, 0, Layer.Mask!.Width, Layer.Mask.Height) : Bounds;
    }

    private static List<Record> Records(Document document)
    {
        var records = new List<Record>();
        void Add(IEnumerable<Layer> layers)
        {
            foreach (var layer in layers)
            {
                if (layer.IsAdjustment) continue;
                if (layer.IsGroup)
                {
                    records.Add(new(Layer.Group("</Layer group>"), SKRectI.Empty, 3));
                    Add(layer.Children);
                    records.Add(new(layer, SKRectI.Empty, layer.Collapsed ? 2 : 1));
                }
                else
                {
                    var bounds = layer.Pixels == null ? SKRectI.Empty : Geometry.RoundOut(layer.VisibleBounds);
                    Validate(bounds);
                    records.Add(new(layer, bounds));
                }
            }
        }
        if (NeedsAppearance(document))
        {
            records.Add(new(Layer.Group("</Layer group>"), SKRectI.Empty, 3));
            Add(document.Layers);
            var sources = Layer.Group(SourceFolder); sources.Visible = false;
            records.Add(new(sources, SKRectI.Empty, 2));
            records.Add(new(Layer.Group(AppearanceLayer), document.Bounds, Appearance: true));
        }
        else Add(document.Layers);
        // A negative layer count advertises the merged transparency, including an entirely empty canvas.
        if (records.Count == 0) records.Add(new(Layer.Group(AppearanceLayer), document.Bounds, Appearance: true));
        if (records.Count > PsdReader.MaxLayers) throw new PsdException($"Photoshop export supports at most {PsdReader.MaxLayers:N0} layer records, including folder dividers.");
        return records;
    }

    private static void Validate(SKRectI bounds)
    {
        if (!bounds.IsEmpty && !DocumentLimits.FitsSurface(bounds.Width, bounds.Height)) throw PsdException.TooLarge();
    }

    /// <summary>Writes at the stream's current position, leaving it open. A seekable output is needed to patch lengths.</summary>
    public static void Write(Document document, Stream stream)
    {
        if (!stream.CanWrite || !stream.CanSeek) throw new ArgumentException("Photoshop export needs a writable, seekable stream.", nameof(stream));
        if (!DocumentLimits.FitsSurface(document.Width, document.Height)) throw PsdException.TooLarge();
        var records = Records(document);
        var output = new Output(stream);
        using var merged = new MergedImage(document);
        output.Id("8BPS"); output.U16(1); output.Zeros(6); output.U16(4);
        output.I32(document.Height); output.I32(document.Width); output.U16(8); output.U16(3); output.U32(0);
        output.Block(() => Resources(output, document));
        output.Block(() =>
        {
            output.Block(() =>
            {
                output.U16(unchecked((ushort)-records.Count));
                foreach (var record in records) WriteRecord(output, record, document);
                foreach (var record in records)
                {
                    using var prepared = Prepare(document, record, merged);
                    foreach (var (id, position) in record.Channels)
                    {
                        var start = output.Position;
                        var bitmap = id == -2 ? prepared.Mask : prepared.Image;
                        if (bitmap == null) output.U16(0);
                        else WritePlanes(output, bitmap, [id]);
                        output.Patch(position, output.Position - start);
                    }
                }
            }, even: true);
            output.U32(0); // global layer mask
        });
        WritePlanes(output, merged.Image, [0, 1, 2, -1], matte: true);
        output.CheckSize();
    }

    internal static ReadOnlySpan<byte> SrgbProfileBytes => SrgbProfile.Value;

    private static readonly Lazy<byte[]> SrgbProfile = new(() =>
    {
        using var resource = typeof(PsdExport).Assembly.GetManifestResourceStream("Composa.IO.Psd.sRGB2014.icc")
            ?? throw new IOException("The built-in sRGB profile is missing.");
        using var data = new MemoryStream(); resource.CopyTo(data); return data.ToArray();
    });

    private static void Resources(Output output, Document document)
    {
        output.Resource(1039, () => output.Bytes(SrgbProfile.Value));
        output.Resource(1005, () =>
        {
            var resolution = (uint)Math.Round(Math.Clamp(document.Resolution, 1, 9600) * 65536);
            for (var i = 0; i < 2; i++) { output.U32(resolution); output.U16(1); output.U16(1); }
        });
        output.Resource(1057, () =>
        {
            output.U32(1); output.U8(1); output.Unicode("Composa"); output.Unicode("Adobe Photoshop"); output.U32(1);
        });
        if (document.Guides.Count > 0) output.Resource(1032, () =>
        {
            output.U32(1); output.U32(576); output.U32(576); output.U32((uint)document.Guides.Count);
            foreach (var guide in document.Guides)
            {
                output.I32(checked((int)Math.Round(guide.Position * 32)));
                output.U8(guide.Axis == GuideAxis.Vertical ? (byte)0 : (byte)1);
            }
        });
    }

    private static void WriteRecord(Output output, Record record, Document document)
    {
        var layer = record.Layer;
        output.Rect(record.Bounds);
        var ids = record.Section != 0 ? Array.Empty<short>() : new short[] { -1, 0, 1, 2 };
        output.U16((ushort)(ids.Length + (record.HasMask ? 1 : 0)));
        foreach (var id in ids.Concat(record.HasMask ? new short[] { -2 } : []))
        {
            output.U16(unchecked((ushort)id)); record.Channels.Add((id, output.Position)); output.U32(0);
        }
        var blend = layer.IsGroup && layer.Blend == BlendMode.Normal && record.Section is 1 or 2 ? "pass" : PsdImport.BlendKey(layer.Blend);
        output.Id("8BIM"); output.Id(blend);
        output.U8((byte)Math.Clamp(Math.Round(layer.Opacity * 255), 0, 255));
        output.U8(layer.Clipped ? (byte)1 : (byte)0); output.U8(layer.Visible ? (byte)0 : (byte)2); output.U8(0);
        output.Block(() =>
        {
            if (record.HasMask)
            {
                output.U32(20); output.Rect(record.MaskBounds); output.U8(0);
                output.U8(layer.MaskEnabled ? (byte)0 : (byte)2); output.U16(0);
            }
            else output.U32(0);
            output.U32(0); // default blending ranges
            var name = Encoding.Latin1.GetBytes(layer.Name[..Math.Min(255, layer.Name.Length)]);
            output.U8((byte)name.Length); output.Bytes(name); output.Zeros((4 - (name.Length + 1) % 4) % 4);
            output.Additional("luni", () => output.Unicode(layer.Name));
            if (record.Section == 0 && !record.Appearance && HasNativeContent(layer, document)) WriteNative(output, layer, document);
            if (record.Section != 0) output.Additional("lsct", () =>
            {
                output.U32((uint)record.Section); output.Id("8BIM"); output.Id(blend);
            });
        });
    }

    private sealed class Prepared : IDisposable
    {
        public SKBitmap? Image, Mask;
        public bool OwnImage, OwnMask;
        public void Dispose() { if (OwnImage) Image?.Dispose(); if (OwnMask) Mask?.Dispose(); }
    }

    private sealed class MergedImage(Document document) : IDisposable
    {
        private SKBitmap? image;
        public SKBitmap Image => image ??= DocumentRenderer.Flatten(document);
        public void Dispose() => image?.Dispose();
    }

    private static Prepared Prepare(Document document, Record record, MergedImage merged)
    {
        var layer = record.Layer;
        var result = new Prepared();
        try
        {
            if (record.Appearance) { result.Image = merged.Image; return result; }
            if (record.Section != 0) { result.Mask = record.HasMask ? layer.Mask : null; return result; }
            if (layer.Pixels == null) return result;
            var direct = !record.HasEffects && layer.FillOpacity >= 1 && layer.Transform.IsPureTranslation(layer.Pixels.Width, layer.Pixels.Height);
            if (direct) result.Image = layer.Pixels;
            else
            {
                var shown = layer.Clone();
                shown.Visible = true; shown.Opacity = 1; shown.Blend = BlendMode.Normal; shown.Clipped = false;
                if (!record.HasEffects) shown.Mask = null;
                result.Image = DocumentRenderer.RenderLayers(document, [shown], record.Bounds); result.OwnImage = true;
            }
            if (record.HasMask)
            {
                if (direct && layer.Mask!.Width == layer.Pixels.Width && layer.Mask.Height == layer.Pixels.Height) result.Mask = layer.Mask;
                else
                {
                    result.Mask = Pixels.NewMask(record.Bounds.Width, record.Bounds.Height); result.OwnMask = true;
                    // Derive coverage from the renderer's masked and unmasked results. Rotating a mask onto
                    // an empty surface and multiplying later would apply its antialiased boundary twice.
                    var maskedLayer = layer.Clone();
                    maskedLayer.Visible = true; maskedLayer.Opacity = 1; maskedLayer.Blend = BlendMode.Normal;
                    maskedLayer.Clipped = false; maskedLayer.Effects = null; maskedLayer.MaskEnabled = true;
                    using var masked = DocumentRenderer.RenderLayers(document, [maskedLayer], record.Bounds);
                    for (var y = 0; y < result.Mask.Height; y++)
                    {
                        var bare = result.Image!.GetPixelSpan().Slice(y * result.Image.RowBytes);
                        var covered = masked.GetPixelSpan().Slice(y * masked.RowBytes);
                        var target = result.Mask.GetPixelSpan().Slice(y * result.Mask.RowBytes);
                        for (var x = 0; x < result.Mask.Width; x++)
                        {
                            var a = bare[x * 4 + 3];
                            target[x] = a == 0 ? (byte)0 : (byte)Math.Min(255, (covered[x * 4 + 3] * 255 + a / 2) / a);
                        }
                    }
                    Pixels.Invalidate(result.Mask);
                }
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    /// <summary>PackBits counts precede rows. Only one row and its encoded form are allocated, reused for every plane.</summary>
    private static void WritePlanes(Output output, SKBitmap bitmap, short[] ids, bool matte = false)
    {
        output.U16(1);
        var counts = output.Position;
        output.Zeros(checked(ids.Length * bitmap.Height * 2));
        var row = new byte[bitmap.Width];
        var packed = new byte[bitmap.Width + (bitmap.Width + 127) / 128 + 1];
        var rowCounts = new ushort[ids.Length * bitmap.Height];
        var index = 0;
        foreach (var id in ids)
        for (var y = 0; y < bitmap.Height; y++, index++)
        {
            var pixels = bitmap.GetPixelSpan().Slice(y * bitmap.RowBytes, bitmap.Width * (bitmap.ColorType == SKColorType.Alpha8 ? 1 : 4));
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (id == -2) row[x] = pixels[x];
                else
                {
                    var a = pixels[x * 4 + 3];
                    var value = id == -1 ? a : pixels[x * 4 + id];
                    row[x] = id == -1 ? a : matte ? (byte)Math.Min(255, value + 255 - a)
                        : a == 0 ? (byte)0 : (byte)Math.Min(255, (value * 255 + a / 2) / a);
                }
            }
            var length = PackBits(row, packed);
            output.Bytes(packed.AsSpan(0, length));
            rowCounts[index] = checked((ushort)length);
            output.CheckSize();
        }
        output.RowCounts(counts, rowCounts);
    }

    internal static int PackBits(ReadOnlySpan<byte> row, Span<byte> output)
    {
        int x = 0, p = 0;
        while (x < row.Length)
        {
            var run = 1;
            while (run < 128 && x + run < row.Length && row[x + run] == row[x]) run++;
            if (run >= 3) { output[p++] = unchecked((byte)(1 - run)); output[p++] = row[x]; x += run; continue; }
            var start = x;
            while (x < row.Length && x - start < 128)
            {
                if (x + 2 < row.Length && row[x] == row[x + 1] && row[x] == row[x + 2]) break;
                x++;
            }
            output[p++] = (byte)(x - start - 1); row[start..x].CopyTo(output[p..]); p += x - start;
        }
        return p;
    }

    private sealed class Output(Stream stream)
    {
        public long Position => stream.Position;
        public void Bytes(ReadOnlySpan<byte> value) => stream.Write(value);
        public void U8(byte value) => stream.WriteByte(value);
        public void U16(ushort value) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, value); Bytes(b); }
        public void U32(uint value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); Bytes(b); }
        public void I32(int value) => U32(unchecked((uint)value));
        public void F64(double value) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, BitConverter.DoubleToInt64Bits(value)); Bytes(b); }
        public void F32(float value) => I32(BitConverter.SingleToInt32Bits(value));
        public void Id(string value) => Bytes(Encoding.ASCII.GetBytes(value));
        public void Rect(SKRectI box) { I32(box.Top); I32(box.Left); I32(box.Bottom); I32(box.Right); }
        public void Unicode(string value) { U32((uint)value.Length + 1); Bytes(Encoding.BigEndianUnicode.GetBytes(value)); U16(0); }
        public void Zeros(int count)
        {
            Span<byte> zero = stackalloc byte[256]; zero.Clear();
            while (count > 0) { var n = Math.Min(count, zero.Length); Bytes(zero[..n]); count -= n; }
        }
        public void CheckSize() { if (Position > MaxFileBytes) throw new PsdException("The PSD exceeds 2 GB. Save a Composa project instead."); }
        public void Patch(long position, long value, bool shortForm = false)
        {
            var end = Position; stream.Position = position;
            if (shortForm) U16(checked((ushort)value)); else U32(checked((uint)value));
            stream.Position = end;
        }
        public void RowCounts(long position, ushort[] values)
        {
            var end = Position; stream.Position = position;
            foreach (var value in values) U16(value);
            stream.Position = end;
        }
        public void Block(Action body, bool even = false)
        {
            var start = Position; U32(0); body();
            if (even && (Position - start - 4) % 2 != 0) U8(0);
            CheckSize(); Patch(start, Position - start - 4);
        }
        public void Resource(ushort id, Action body) { Id("8BIM"); U16(id); U16(0); var start = Position; Block(body); if ((Position - start - 4) % 2 != 0) U8(0); }
        public void Additional(string key, Action body) { Id("8BIM"); Id(key); Block(body, even: true); }
    }
}
