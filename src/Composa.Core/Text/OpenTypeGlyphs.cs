using HarfBuzzSharp;
using Composa.Model;
using SkiaSharp;

namespace Composa.Text;

/// <summary>Shape optional OpenType substitutions and positioning against the exact typeface used by Skia.</summary>
internal sealed class OpenTypeGlyphs : IDisposable
{
    internal readonly record struct Glyph(ushort Id, float X, float Y, float Advance);
    private readonly Feature[] features;
    private readonly Dictionary<SKTypeface, Font> fonts = new();
    private readonly Dictionary<(SKTypeface, int), Glyph[]> glyphs = new();
    public OpenTypeGlyphs(Typography settings)
    {
        var tags = new List<string>();
        if (settings.Case == TextLetterCase.SmallCaps) tags.Add("smcp");
        if (settings.CaseForms) tags.Add("case");
        if (settings.CapitalSpacing) tags.Add("cpsp");
        if (settings.Figures != FigureStyle.Default) tags.Add(settings.Figures switch
        { FigureStyle.Lining => "lnum", FigureStyle.OldStyle => "onum", FigureStyle.Tabular => "tnum", _ => "pnum" });
        if (settings.FigurePosition != FigurePosition.Baseline) tags.Add(settings.FigurePosition == FigurePosition.Subscript ? "subs" : "sups");
        features = tags.Select(Feature.Parse).ToArray();
    }
    public bool Enabled => features.Length > 0;
    internal Glyph[] Get(SKTypeface typeface, int scalar)
    {
        if (glyphs.TryGetValue((typeface, scalar), out var cached)) return cached;
        if (!fonts.TryGetValue(typeface, out var font))
        {
            using var face = new Face((_, tag) => typeface.TryGetTableData(tag, out var bytes)
                ? Blob.FromStream(new MemoryStream(bytes, false)) : Blob.Empty);
            font = new Font(face); font.SetFunctionsOpenType(); font.SetScale(65536, 65536);
            fonts.Add(typeface, font);
        }
        using var buffer = new HarfBuzzSharp.Buffer();
        buffer.AddUtf16(char.ConvertFromUtf32(scalar)); buffer.GuessSegmentProperties();
        font.Shape(buffer, features);
        var info = buffer.GlyphInfos; var pos = buffer.GlyphPositions;
        var result = new Glyph[info.Length];
        float x = 0;
        for (int i = 0; i < result.Length; i++)
        {
            var p = pos[i]; float advance = p.XAdvance / 65536f;
            result[i] = new((ushort)info[i].Codepoint, x + p.XOffset / 65536f, -p.YOffset / 65536f, advance); x += advance;
        }
        return glyphs[(typeface, scalar)] = result;
    }
    public void Dispose() { foreach (var font in fonts.Values) font.Dispose(); }
}
