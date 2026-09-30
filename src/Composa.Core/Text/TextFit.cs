using Composa.Model;

namespace Composa.Text;

/// <summary>Fits full editable text using the actual renderer, never estimates glyph widths or truncates content.</summary>
public static class TextFit
{
    public static TextStyle Within(TextStyle style, double width, double height)
    {
        width = Math.Floor(width); height = Math.Floor(height);
        if (width < TextStyle.MinBox || height < TextStyle.MinBox) throw new ArgumentException("Not enough canvas space for text; move its origin or enlarge the canvas.");
        style = style.Clamped();
        bool Fits(TextStyle candidate)
        {
            var layout = new TextLayout(candidate);
            return layout.Width <= width && layout.Height <= height && !layout.Overflows
                && (layout.Lines.Count == 0 || layout.Lines[^1].Baseline + layout.Descent <= layout.Height - TextLayout.Padding + .5)
                && layout.Lines.All(line => line.VisibleWidth <= layout.Width - 2 * TextLayout.Padding);
        }
        if (Fits(style)) return style;
        // Wrap first, before reducing the requested font. Explicit boxes remain bounded by the canvas.
        style = style with { BoxWidth = Math.Min(style.BoxWidth ?? width, width), BoxHeight = Math.Min(style.BoxHeight ?? height, height) };
        if (Fits(style)) return style;
        var smallest = style with { Size = 1, Leading = 0, Tracking = 0 };
        if (!Fits(smallest)) throw new ArgumentException("The full text cannot fit on this canvas, even at the minimum font size.");
        double low = 1, high = style.Size;
        for (var i = 0; i < 18; i++)
        {
            var size = (low + high) / 2;
            var candidate = style with { Size = size, Leading = style.Leading * size / style.Size, Tracking = style.Tracking * size / style.Size };
            if (Fits(candidate)) { low = size; smallest = candidate; } else high = size;
        }
        return smallest;
    }
}
