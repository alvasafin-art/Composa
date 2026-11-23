using System.Collections.Concurrent;
using Composa.Model;
using SkiaSharp;

namespace Composa.Text;

/// <summary>Installed styles only, cached as small managed descriptions rather than open native font objects.</summary>
public static class FontCatalog
{
    public sealed record Choice(string Name, TextFace Face);
    private static readonly ConcurrentDictionary<string, IReadOnlyList<Choice>> families = new(StringComparer.Ordinal);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SKTypeface, HashSet<string>> featureTags = new();

    public static bool Supports(TextFace face, string tag) => featureTags.GetValue(TextLayout.TypefaceFor(face), font =>
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (uint table in new uint[] { 0x47535542, 0x47504F53 }) // GSUB and GPOS FeatureList
        {
            if (!font.TryGetTableData(table, out var bytes) || bytes.Length < 10) continue;
            int U16(int i) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(i));
            int offset = U16(6);
            if (offset + 2 > bytes.Length) continue;
            int count = U16(offset);
            if (offset + 2 + count * 6 > bytes.Length) continue;
            for (int i = 0; i < count; i++)
                tags.Add(System.Text.Encoding.ASCII.GetString(bytes, offset + 2 + i * 6, 4));
        }
        return tags;
    }).Contains(tag);

    public static IReadOnlyList<Choice> ForFamily(string? family) => families.GetOrAdd(
        TextStyle.IsFamilyName(family) ? family! : TextStyle.DefaultFontFamily, static name =>
    {
        using var styles = SKFontManager.Default.GetFontStyles(name);
        var result = new List<Choice>();
        for (var i = 0; i < styles.Count; i++)
        {
            using var style = styles[i];
            var label = styles.GetStyleName(i);
            if (string.IsNullOrWhiteSpace(label)) label = Description(style);
            var face = Face(name, style.Weight, style.Width, style.Slant);
            if (!result.Any(c => c.Face == face)) result.Add(new(label, face));
        }
        if (result.Count == 0)
        {
            var fallback = TextLayout.TypefaceFor(new TextFace(name, false, false));
            using var style = fallback.FontStyle;
            result.Add(new(Description(style), Face(name, style.Weight, style.Width, style.Slant)));
        }
        return result.OrderBy(c => Style(c.Face).Slant).ThenBy(c => Style(c.Face).Width).ThenBy(c => Style(c.Face).Weight).ToArray();
    });

    public static TextFace Face(string family, int weight, int width, SKFontStyleSlant slant) => new(family, weight >= 600, slant != SKFontStyleSlant.Upright)
    {
        FontStyle = weight is 400 or 700 && width == 5 && slant is SKFontStyleSlant.Upright or SKFontStyleSlant.Italic ? null : new(weight, width, slant)
    };

    public static TextFontStyle Style(TextFace face) => face.FontStyle ?? new(face.Bold ? 700 : 400, 5, face.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);

    /// <summary>Changing family keeps the closest available width, slant and weight.</summary>
    public static Choice Closest(string family, TextFace face)
    {
        var wanted = Style(face);
        return ForFamily(family).MinBy(c =>
        {
            var actual = Style(c.Face);
            return (actual.Slant == wanted.Slant ? 0 : 10000) + Math.Abs(actual.Width - wanted.Width) * 2000 + Math.Abs(actual.Weight - wanted.Weight);
        })!;
    }

    private static string Description(SKFontStyle style)
    {
        var weight = style.Weight switch { <= 100 => "Thin", <= 200 => "Extra Light", <= 300 => "Light", <= 400 => "Regular", <= 500 => "Medium", <= 600 => "Semi Bold", <= 700 => "Bold", <= 800 => "Extra Bold", _ => "Black" };
        return (style.Width < 5 ? "Condensed " : style.Width > 5 ? "Expanded " : "") + weight + (style.Slant == SKFontStyleSlant.Upright ? "" : style.Slant == SKFontStyleSlant.Italic ? " Italic" : " Oblique");
    }
}
