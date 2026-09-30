using Composa.Editing;
using Composa.Model;
using Composa.Text;
using SkiaSharp;

namespace Composa.App.Automation;

internal static class AutomationText
{
    public static (SKPoint Origin, TextStyle Style) Prepare(EditorSession session, double x, double y, TextStyle style, bool fit)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentException("Text coordinates must be finite.");
        if (!fit) return (new SKPoint((float)x, (float)y), style.Clamped());
        if (x < 0 || y < 0 || x >= session.Document.Width || y >= session.Document.Height) throw new ArgumentException("Place the text origin inside the canvas, or explicitly disable fitToCanvas.");
        var left = Math.Max(0, Math.Round(x) - TextLayout.Padding);
        var top = Math.Max(0, Math.Round(y) - TextLayout.Padding);
        return (new SKPoint((float)(left + TextLayout.Padding), (float)(top + TextLayout.Padding)),
            TextFit.Within(style, session.Document.Width - left, session.Document.Height - top));
    }

    public static TextStyle PrepareUpdate(EditorSession session, Layer layer, TextStyle style, bool fit)
    {
        if (!fit) return style.Clamped();
        var scaleX = layer.Transform.Width / layer.Pixels!.Width;
        var scaleY = layer.Transform.Height / layer.Pixels.Height;
        if (scaleX <= 0 || scaleY <= 0) throw new ArgumentException("Text has an invalid scale.");
        var width = session.Document.Width - Math.Clamp(layer.Bounds.Left, 0, session.Document.Width - 1);
        var height = session.Document.Height - Math.Clamp(layer.Bounds.Top, 0, session.Document.Height - 1);
        // Conservative local space for rotated text; retain its rotation and scale.
        var angle = layer.Transform.Rotation * Math.PI / 180;
        var factor = Math.Abs(Math.Cos(angle)) + Math.Abs(Math.Sin(angle));
        return TextFit.Within(style, width / scaleX / factor, height / scaleY / factor);
    }

    public static void KeepInside(EditorSession session, Layer layer)
    {
        var bounds = layer.Bounds;
        if (bounds.Width > session.Document.Width + .01 || bounds.Height > session.Document.Height + .01)
            throw new ArgumentException("Transformed text cannot fit inside the canvas. Reduce its size or explicitly disable fitToCanvas.");
        var dx = bounds.Left < 0 ? -bounds.Left : bounds.Right > session.Document.Width ? session.Document.Width - bounds.Right : 0;
        var dy = bounds.Top < 0 ? -bounds.Top : bounds.Bottom > session.Document.Height ? session.Document.Height - bounds.Bottom : 0;
        if (dx != 0 || dy != 0) session.SetTransform(layer, layer.Transform with { X = layer.Transform.X + dx, Y = layer.Transform.Y + dy });
    }
}
