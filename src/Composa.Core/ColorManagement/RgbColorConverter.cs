using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.ColorManagement;

/// <summary>Converts editable color swatches without an RGB8 intermediate. Styles currently store byte RGB.</summary>
internal sealed class RgbColorConverter : IDisposable
{
    private readonly SKBitmap input, output;
    private readonly Dictionary<uint, uint> cache = [];
    internal RgbColorConverter(RgbProfile from, RgbProfile to)
    {
        input = Pixels.NewWorkingColor(1, 1, ColorDepth.Sixteen, from);
        output = Pixels.NewWorkingColor(1, 1, ColorDepth.Sixteen, to);
    }
    internal uint Convert(uint value)
    {
        if (cache.TryGetValue(value, out var result)) return result;
        var color = new SKColor(value);
        // An opaque sample avoids quantizing/transferring premultiplied low-alpha colors.
        new PixelBuffer(input).Write(0, 0, new(color.Red / 255f, color.Green / 255f, color.Blue / 255f, 1));
        using var pixmap = input.PeekPixels();
        if (!pixmap.ReadPixels(output.Info, output.GetPixels(), output.RowBytes)) throw new InvalidOperationException("The RGB swatch could not be converted.");
        var pixel = new PixelBuffer(output).Read(0, 0);
        byte Byte(float c) => (byte)Math.Clamp(Math.Round(c * 255), 0, 255);
        return cache[value] = (uint)new SKColor(Byte(pixel.R), Byte(pixel.G), Byte(pixel.B), color.Alpha);
    }
    public void Dispose() { input.Dispose(); output.Dispose(); }
}
