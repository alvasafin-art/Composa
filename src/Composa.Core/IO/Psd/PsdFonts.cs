using System.Buffers.Binary;
using System.Text;
using Composa.Editing;
using Composa.Model;
using Composa.Text;
using SkiaSharp;

namespace Composa.IO.Psd;

/// <summary>Photoshop identifies fonts by the OpenType PostScript name, not by an invented family/style suffix.</summary>
internal static class PsdFonts
{
    private static readonly Lazy<Dictionary<string, TextFace>> installed = new(() =>
    {
        var result = new Dictionary<string, TextFace>(StringComparer.Ordinal);
        foreach (var family in EditorSession.FontFamilies)
        {
            using var styles = SKFontManager.Default.GetFontStyles(family);
            for (var i = 0; i < styles.Count; i++)
            {
                using var typeface = styles.CreateTypeface(i);
                if (typeface == null) continue;
                result.TryAdd(PostScriptName(typeface), FontCatalog.Face(typeface.FamilyName, typeface.FontWeight, typeface.FontWidth, typeface.FontSlant));
            }
        }
        return result;
    });

    public static TextFace? Find(string name, TextFace guess)
    {
        var face = TextLayout.TypefaceFor(guess);
        if (PostScriptName(face) == name) return FontCatalog.Face(face.FamilyName, face.FontWeight, face.FontWidth, face.FontSlant);
        return installed.Value.TryGetValue(name, out var actual) ? actual : null;
    }

    public static string PostScriptName(SKTypeface face)
    {
        var table = face.GetTableData(0x6E616D65); // 'name'
        if (table is { Length: >= 6 })
        {
            var count = BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(2));
            var strings = BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(4));
            string? fallback = null;
            for (var i = 0; i < count && 6 + (i + 1) * 12 <= table.Length; i++)
            {
                var r = table.AsSpan(6 + i * 12, 12);
                var platform = BinaryPrimitives.ReadUInt16BigEndian(r);
                if (BinaryPrimitives.ReadUInt16BigEndian(r[6..]) != 6) continue;
                var length = BinaryPrimitives.ReadUInt16BigEndian(r[8..]);
                var offset = strings + BinaryPrimitives.ReadUInt16BigEndian(r[10..]);
                if (offset > table.Length - length) continue;
                var name = platform is 0 or 3 ? Encoding.BigEndianUnicode.GetString(table, offset, length) : Encoding.Latin1.GetString(table, offset, length);
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (platform == 3) return name;
                fallback = name;
            }
            if (fallback != null) return fallback;
        }
        return face.FamilyName.Replace(" ", "") + (face.IsBold ? "-Bold" : "-Regular") + (face.IsItalic ? "Italic" : "");
    }
}
