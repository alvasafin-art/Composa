using System.Text;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Text;

/// <summary>
/// Lays a <see cref="TextStyle"/> out in layer pixels: which characters make each line, where every character
/// boundary sits, and how big the bitmap is. Point text is as wide as its longest line; paragraph text wraps at
/// word boundaries inside its box. The same layout draws the pixels and places the caret, so what is edited is
/// exactly what is rendered.
/// </summary>
public sealed class TextLayout
{
    /// <summary>The gap between the text and the edge of its bitmap, in layer pixels.</summary>
    public const float Padding = 12;

    public sealed class Line
    {
        /// <summary>Character range, end exclusive. A hard line break's newline is not part of any line.</summary>
        public int Start { get; init; }
        public int End { get; init; }
        /// <summary>Where the line starts, after alignment.</summary>
        public float X { get; init; }
        public float Baseline { get; init; }
        public float Ascent { get; init; }
        public float Descent { get; init; }
        public float Top { get; init; }
        public float Height { get; init; }
        /// <summary>The x offset of each character boundary from <see cref="X"/>: <c>Positions[k]</c> is where character <c>Start + k</c> begins, and the last entry is where the line ends.</summary>
        public float[] Positions { get; init; } = [];
        /// <summary>The width without trailing spaces, which alignment ignores.</summary>
        public float VisibleWidth { get; init; }
        public int Length => End - Start;
    }

    public TextStyle Style { get; }
    public string Text { get; }
    public int Width { get; }
    public int Height { get; }
    public float LineHeight { get; }
    public float Ascent { get; }
    public float Descent { get; }
    public IReadOnlyList<Line> Lines { get; }
    /// <summary>Paragraph text that does not fit its box; the lines beyond it are laid out but clipped.</summary>
    public bool Overflows { get; }

    /// <summary>Faces looked up once per process: matching a family through the font manager is slow, and a layout asks for every stretch it draws.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<TextFace, SKTypeface> typefaces = new();

    /// <summary>
    /// The closest face in the family. A family Skia does not know (Inter is Avalonia's font for the interface and is
    /// not installed for Skia) goes through the platform's substitution, which keeps the weight and slant; the plain
    /// default typeface would drop both.
    /// </summary>
    public static SKTypeface TypefaceFor(TextStyle style) => TypefaceFor(style.Face);

    public static SKTypeface TypefaceFor(TextFace face) => typefaces.GetOrAdd(face, static f =>
    {
        var selected = FontCatalog.Style(f);
        using var style = new SKFontStyle(selected.Weight, selected.Width, selected.Slant);
        return SKFontManager.Default.MatchFamily(f.FontFamily, style) ?? SKTypeface.FromFamilyName(f.FontFamily, style) ?? SKTypeface.Default;
    });

    /// <summary>The font for layout and drawing alike. A face without a bold or italic variant gets them synthesized, as Photoshop's faux styles do.</summary>
    private SKFont MakeFont(TextFace face, double size)
    {
        var typeface = TypefaceFor(face);
        var font = new SKFont(typeface, (float)Math.Clamp(size, 1, 4000)) { Subpixel = true, Edging = SKFontEdging.Antialias, Hinting = SKFontHinting.None };
        if (face.FontStyle == null && face.Bold && typeface.FontWeight < (int)SKFontStyleWeight.SemiBold) font.Embolden = true;
        if (face.FontStyle == null && face.Italic && typeface.FontSlant == SKFontStyleSlant.Upright) font.SkewX = -0.25f;
        return font;
    }

    /// <summary>One font per face the text uses, made as they are needed and disposed together.</summary>
    private sealed class Fonts(TextLayout layout) : IDisposable
    {
        private readonly Dictionary<(TextFace, double), SKFont> fonts = [];
        public SKFont For(TextFace face, double? size = null)
        {
            var key = (face, size ?? layout.Style.Size);
            return fonts.TryGetValue(key, out var font) ? font : fonts[key] = layout.MakeFont(face, key.Item2);
        }
        public void Dispose() { foreach (var font in fonts.Values) font.Dispose(); }
    }

    public TextLayout(TextStyle style)
    {
        Style = style = style.Clamped();
        Text = style.Text.Replace("\r", "");
        using var fonts = new Fonts(this);
        // Line metrics are the style's own face's, whatever faces the letters are in, so lines do not shift as letters change.
        var metrics = fonts.For(style.Face).Metrics;
        Ascent = -metrics.Ascent;
        Descent = metrics.Descent;
        LineHeight = (float)style.LineHeight;
        var tracking = (float)style.Tracking;
        var advances = Advances(fonts, Text, tracking);

        var available = style.IsBox ? (float)Math.Max(1, style.BoxWidth!.Value - 2 * Padding) : float.PositiveInfinity;
        var ranges = new List<(int Start, int End)>();
        var paragraphStart = 0;
        for (var i = 0; i <= Text.Length; i++)
        {
            if (i < Text.Length && Text[i] != '\n') continue;
            Wrap(paragraphStart, i, advances, available, ranges);
            paragraphStart = i + 1;
        }

        float contentWidth = 0;
        var visible = new float[ranges.Count];
        for (var i = 0; i < ranges.Count; i++)
        {
            var (start, end) = ranges[i];
            var last = end;
            while (last > start && Text[last - 1] == ' ') last--;
            visible[i] = Sum(advances, start, last);
            contentWidth = Math.Max(contentWidth, visible[i]);
        }
        var lineMetrics = new List<(float Ascent, float Descent, float Height)>();
        foreach (var (start, end) in ranges)
        {
            float ascent = 0, descent = 0, largest = 0;
            for (var k = start; k < Math.Max(start + 1, end); k++)
            {
                var size = style.SizeAt(k);
                var m = fonts.For(style.FaceAt(k), size).Metrics;
                ascent = Math.Max(ascent, -m.Ascent); descent = Math.Max(descent, m.Descent); largest = Math.Max(largest, (float)size);
            }
            lineMetrics.Add((ascent, descent, (float)(style.Leading > 0 ? style.Leading : largest * 1.2)));
        }
        var explicitShift = style.Leading > 0
            ? Math.Max(0, lineMetrics.Select((m, i) => m.Ascent - lineMetrics[0].Ascent - i * LineHeight).Max()) : 0;
        if (style.IsBox)
        {
            Width = (int)Math.Round(style.BoxWidth!.Value);
            Height = (int)Math.Round(style.BoxHeight!.Value);
            contentWidth = available;
        }
        else
        {
            // A caret's worth of width so an empty line still has somewhere to type.
            var largestSize = Math.Max(style.Size, style.SizeRuns?.Max(r => r.Size) ?? style.Size);
            Width = (int)Math.Max(TextStyle.MinBox, Math.Ceiling(contentWidth + Padding * 2 + largestSize * 0.1));
            var contentHeight = style.Leading > 0
                ? Math.Max(ranges.Count * LineHeight + explicitShift, lineMetrics.Select((m, i) => lineMetrics[0].Ascent + explicitShift + i * LineHeight + m.Descent).Max())
                : Math.Max(lineMetrics.Sum(m => m.Height), lineMetrics.Take(lineMetrics.Count - 1).Sum(m => m.Height) + lineMetrics[^1].Ascent + lineMetrics[^1].Descent);
            Height = (int)Math.Max(TextStyle.MinBox, Math.Ceiling(contentHeight + Padding * 2));
        }
        Width = Math.Min(Width, Document.MaxSide);
        Height = Math.Min(Height, Document.MaxSide);

        var lines = new List<Line>(ranges.Count);
        float top = Padding;
        for (var i = 0; i < ranges.Count; i++)
        {
            var (start, end) = ranges[i];
            var positions = new float[end - start + 1];
            for (var k = 1; k <= end - start; k++) positions[k] = positions[k - 1] + advances[start + k - 1];
            var x = Padding + style.Alignment switch
            {
                TextAlignment.Center => (contentWidth - visible[i]) / 2,
                TextAlignment.Right => contentWidth - visible[i],
                _ => 0
            };
            var lm = lineMetrics[i];
            var baseline = style.Leading > 0 ? Padding + lineMetrics[0].Ascent + explicitShift + i * LineHeight : top + lm.Ascent;
            lines.Add(new Line { Start = start, End = end, X = x, Top = top, Height = lm.Height, Ascent = lm.Ascent, Descent = lm.Descent, Baseline = baseline, Positions = positions, VisibleWidth = visible[i] });
            top += lm.Height;
        }
        Lines = lines;
        Overflows = style.IsBox && lines.Any(l => l.Baseline + l.Descent > Height - Padding + 0.5f);
    }

    /// <summary>The advance of every character (a surrogate pair's second half advances nothing), tracking included, each stretch of one face measured with its own font.</summary>
    private float[] Advances(Fonts fonts, string text, float tracking)
    {
        var advances = new float[text.Length];
        for (var start = 0; start < text.Length;)
        {
            var face = Style.FaceAt(start);
            var size = Style.SizeAt(start);
            var end = start + 1;
            while (end < text.Length && Style.FaceAt(end) == face && Style.SizeAt(end) == size) end++;
            var font = fonts.For(face, size);
            var segment = text.Substring(start, end - start);
            var widths = font.GetGlyphWidths(font.GetGlyphs(segment));
            var glyph = 0;
            for (var i = 0; i < segment.Length && glyph < widths.Length; i++)
            {
                advances[start + i] = widths[glyph++] + tracking;
                if (char.IsHighSurrogate(segment[i]) && i + 1 < segment.Length && char.IsLowSurrogate(segment[i + 1])) i++;
            }
            start = end;
        }
        return advances;
    }

    private static float Sum(float[] advances, int start, int end)
    {
        float total = 0;
        for (var i = start; i < end; i++) total += advances[i];
        return total;
    }

    /// <summary>Greedy word wrap of one paragraph; a word wider than the box breaks between characters.</summary>
    private void Wrap(int start, int end, float[] advances, float available, List<(int, int)> lines)
    {
        if (start == end) { lines.Add((start, end)); return; }
        var position = start;
        while (position < end)
        {
            float width = 0;
            int k = position, lastSpace = -1;
            for (; k < end; k++)
            {
                if (width + advances[k] > available && k > position) break;
                width += advances[k];
                if (Text[k] == ' ') lastSpace = k;
            }
            var lineEnd = k;
            if (k < end)
            {
                // Spaces at the break stay on this line (they take no visible width); mid-word, go back to the last
                // space so the next line starts with a letter.
                if (Text[k] == ' ') { while (lineEnd < end && Text[lineEnd] == ' ') lineEnd++; }
                else if (lastSpace >= position) lineEnd = lastSpace + 1;
            }
            lines.Add((position, lineEnd));
            position = lineEnd;
        }
    }

    // ---- Drawing --------------------------------------------------------------------------------------------------

    /// <summary>Draws the text into a fresh bitmap of exactly the layout's size.</summary>
    public SKBitmap Render()
    {
        var bitmap = Pixels.NewColor(Width, Height);
        using var canvas = new SKCanvas(bitmap);
        Draw(canvas);
        return bitmap;
    }

    public void Draw(SKCanvas canvas)
    {
        using var fonts = new Fonts(this);
        using var paint = new SKPaint { IsAntialias = true };
        canvas.Save();
        if (Style.IsBox) canvas.ClipRect(new SKRect(0, 0, Width, Height));
        // Letters are drawn a stretch at a time, one stretch per face and color; text in one face and color is one stretch per line.
        foreach (var line in Lines)
        {
            if (line.Length == 0) continue;
            for (var k = 0; k < line.Length;)
            {
                var face = Style.FaceAt(line.Start + k);
                var size = Style.SizeAt(line.Start + k);
                var color = Style.ColorAt(line.Start + k);
                var end = k + 1;
                while (end < line.Length && Style.FaceAt(line.Start + end) == face && Style.SizeAt(line.Start + end) == size && Style.ColorAt(line.Start + end) == color) end++;
                var font = fonts.For(face, size);
                var segment = Text.Substring(line.Start + k, end - k);
                var glyphs = font.GetGlyphs(segment);
                if (glyphs.Length > 0)
                {
                    var positions = new SKPoint[glyphs.Length];
                    var glyph = 0;
                    for (var i = 0; i < segment.Length && glyph < glyphs.Length; i++)
                    {
                        positions[glyph++] = new SKPoint(line.X + line.Positions[k + i], line.Baseline);
                        if (char.IsHighSurrogate(segment[i]) && i + 1 < segment.Length && char.IsLowSurrogate(segment[i + 1])) i++;
                    }
                    using var builder = new SKTextBlobBuilder();
                    var run = builder.AllocatePositionedRun(font, glyphs.Length);
                    run.SetGlyphs(glyphs);
                    run.SetPositions(positions);
                    using var blob = builder.Build();
                    paint.Color = new SKColor(color);
                    if (blob != null) canvas.DrawText(blob, 0, 0, paint);
                }
                k = end;
            }
        }
        canvas.Restore();
    }

    // ---- Caret geometry -------------------------------------------------------------------------------------------

    /// <summary>The line holding a character index. At a soft wrap the caret belongs to the start of the next line.</summary>
    public int LineOf(int index)
    {
        index = Math.Clamp(index, 0, Text.Length);
        for (var i = 0; i < Lines.Count; i++)
        {
            var line = Lines[i];
            if (index < line.Start) return Math.Max(0, i - 1);
            if (index > line.End) continue;
            if (index == line.End && i + 1 < Lines.Count && Lines[i + 1].Start == line.End) return i + 1;
            return i;
        }
        return Lines.Count - 1;
    }

    /// <summary>Where the caret is drawn before a character index: its x and the top and bottom of the line.</summary>
    public (float X, float Top, float Bottom) CaretAt(int index)
    {
        index = Math.Clamp(index, 0, Text.Length);
        var line = Lines[LineOf(index)];
        var k = Math.Clamp(index - line.Start, 0, line.Positions.Length - 1);
        var metrics = MakeFont(Style.FaceAt(Math.Max(0, index - 1)), Style.SizeAt(Math.Max(0, index - 1)));
        using (metrics) return (line.X + line.Positions[k], line.Baseline + metrics.Metrics.Ascent, line.Baseline + metrics.Metrics.Descent);
    }

    /// <summary>The character boundary nearest a point in layout pixels.</summary>
    public int IndexAt(SKPoint point)
    {
        if (Lines.Count == 0) return 0;
        var line = Lines.FirstOrDefault(l => point.Y < l.Top + l.Height) ?? Lines[^1];
        return line.Start + NearestBoundary(line, point.X);
    }

    /// <summary>The index on the line above or below that keeps the caret's x, for the up and down arrows.</summary>
    public int IndexOnAdjacentLine(int index, int direction)
    {
        var current = LineOf(index);
        var target = current + direction;
        if (target < 0) return 0;
        if (target >= Lines.Count) return Text.Length;
        var x = CaretAt(index).X;
        var line = Lines[target];
        return line.Start + NearestBoundary(line, x);
    }

    private static int NearestBoundary(Line line, float x)
    {
        var best = 0;
        var bestDistance = float.MaxValue;
        for (var k = 0; k < line.Positions.Length; k++)
        {
            var distance = Math.Abs(line.X + line.Positions[k] - x);
            if (distance < bestDistance) { bestDistance = distance; best = k; }
        }
        return best;
    }

    /// <summary>The highlighted rectangles for a character range, one per line it touches.</summary>
    public List<SKRect> SelectionRects(int start, int end)
    {
        var rects = new List<SKRect>();
        if (end <= start) return rects;
        start = Math.Clamp(start, 0, Text.Length);
        end = Math.Clamp(end, 0, Text.Length);
        foreach (var line in Lines)
        {
            if (line.End < start || line.Start >= end) { if (!(line.Start == line.End && start <= line.Start && line.Start < end)) continue; }
            var from = Math.Clamp(start, line.Start, line.End);
            var to = Math.Clamp(end, line.Start, line.End);
            var left = line.X + line.Positions[from - line.Start];
            var right = line.X + line.Positions[to - line.Start];
            // A newline taken into the selection shows as a sliver past the line's end.
            if (end > line.End && to == line.End) right += Math.Max(4, line.Ascent * 0.3f);
            rects.Add(new SKRect(left, line.Baseline - line.Ascent, right, line.Baseline + line.Descent));
        }
        return rects;
    }

    /// <summary>The start of the word the index is in (or before it), for Ctrl+Left and double-click.</summary>
    public int WordStart(int index)
    {
        index = Math.Clamp(index, 0, Text.Length);
        while (index > 0 && !char.IsLetterOrDigit(Text[index - 1])) index--;
        while (index > 0 && char.IsLetterOrDigit(Text[index - 1])) index--;
        return index;
    }

    public int WordEnd(int index)
    {
        index = Math.Clamp(index, 0, Text.Length);
        while (index < Text.Length && !char.IsLetterOrDigit(Text[index])) index++;
        while (index < Text.Length && char.IsLetterOrDigit(Text[index])) index++;
        return index;
    }

    public override string ToString()
    {
        var builder = new StringBuilder();
        foreach (var line in Lines) builder.Append('[').Append(Text, line.Start, line.Length).Append(']');
        return builder.ToString();
    }
}
