using System.ComponentModel;
using System.Text;
using Composa.Editing;
using Composa.Model;
using Composa.Painting;
using Composa.Rendering;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SkiaSharp;

namespace Composa.App.Mcp;

/// <summary>
/// Perception for an agent that draws by hand: the colors that are really in the picture and where its edges really
/// run, in canvas coordinates, so strokes can come from the pixels rather than from an estimate of a rendered view.
/// Both read the composite, or one layer's own pixels so a reference under the agent's strokes can still be read.
/// </summary>
public sealed partial class ComposaTools
{
    [McpServerTool(Name = "sample_color", ReadOnly = true, Idempotent = true)]
    [Description("The colors at canvas points, each averaged over a small disc, as document working RGB numbers: from the composite, or one layer's pixels so a photo under your strokes can still be read. The document profile is in describe_document. Take skin, hair and shadow colors from the picture with it instead of guessing them.")]
    public Task<string> SampleColor(
        [Description("The points as [[x, y], ...] in canvas pixels")] double[][] points,
        [Description("Radius of the disc averaged around each point, 0 to 50 pixels")] double radius = 2,
        [Description("Sample this layer's pixels alone; what is on screen when left out")] string? layer = null,
        int? document = null) => OnUi(() =>
    {
        var s = Session(document);
        if (points.Length == 0 || points.Any(p => p.Length != 2)) throw new McpException("points is a list of [x, y] pairs with at least one pair.");
        if (points.Length > 500) throw new McpException("At most 500 points go in one call.");
        var (pixels, toPixels) = Source(s, layer);
        using var working = !Pixels.ProfileOf(pixels).Equals(s.Document.Profile)
            ? layer == null ? s.Flatten() : Pixels.ConvertWorking(pixels, Pixels.DepthOf(pixels), s.Document.Profile) : null;
        pixels = working ?? pixels;
        var r = (float)(Math.Clamp(radius, 0, 50) * Math.Sqrt(Math.Abs(toPixels.ScaleX * toPixels.ScaleY - toPixels.SkewX * toPixels.SkewY)));
        var text = new StringBuilder();
        foreach (var point in points)
        {
            var p = toPixels.MapPoint((float)point[0], (float)point[1]);
            text.Append(point[0].ToString("0")).Append(',').Append(point[1].ToString("0")).Append(": ").AppendLine(Average(pixels, p, r));
        }
        return text.ToString().TrimEnd();
    });

    private static unsafe string Average(SKBitmap pixels, SKPoint center, float radius)
    {
        int x0 = Math.Max(0, (int)MathF.Floor(center.X - radius)), y0 = Math.Max(0, (int)MathF.Floor(center.Y - radius));
        int x1 = Math.Min(pixels.Width - 1, (int)MathF.Ceiling(center.X + radius)), y1 = Math.Min(pixels.Height - 1, (int)MathF.Ceiling(center.Y + radius));
        if (x1 < x0 || y1 < y0) return "outside the picture";
        double red = 0, green = 0, blue = 0, alpha = 0;
        var count = 0;
        var src = (byte*)pixels.GetPixels();
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                float dx = x + 0.5f - center.X, dy = y + 0.5f - center.Y;
                if (dx * dx + dy * dy > radius * radius + 0.25f) continue;
                // Premultiplied sums average to the straight color of what is there, weighted by how much is there.
                if (pixels.ColorType == SKColorType.RgbaF32)
                {
                    var p = (float*)(src + (long)y * pixels.RowBytes) + x * 4;
                    red += p[0] * 255; green += p[1] * 255; blue += p[2] * 255; alpha += p[3] * 255;
                }
                else
                {
                    var p = src + (long)y * pixels.RowBytes + x * 4;
                    red += p[0]; green += p[1]; blue += p[2]; alpha += p[3];
                }
                count++;
            }
        if (count == 0 || alpha <= 0) return "transparent";
        var color = new SKColor((byte)Math.Round(red / alpha * 255), (byte)Math.Round(green / alpha * 255), (byte)Math.Round(blue / alpha * 255));
        var opacity = alpha / count / 255;
        return $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}" + (opacity < 0.995 ? $" at {opacity:P0} opacity" : "");
    }

    [McpServerTool(Name = "trace_edges", ReadOnly = true, Idempotent = true)]
    [Description("The edges in the picture as polylines in canvas pixels, longest first, so line work can follow where a face, an object or a fold really is: feed them to paint_strokes with a thin brush, or read them to place your own strokes. detail 0 to 100 says how faint an edge may be; minLength drops scraps shorter than that many pixels; simplify is how far a polyline may stray from the edge in pixels, higher means fewer points. Trace one layer's pixels to read a photo under your strokes, and a region (x, y, width, height) to look at the eyes alone.")]
    public Task<string> TraceEdges(
        double detail = 50,
        double minLength = 20,
        double simplify = 2,
        [Description("How many edges to return at most, longest first, 1 to 1000")] int maxLines = 150,
        [Description("Trace this layer's pixels alone; what is on screen when left out")] string? layer = null,
        [Description("Left of a region to trace instead of the whole canvas, in canvas pixels")] double? x = null,
        double? y = null, double? width = null, double? height = null,
        int? document = null) => OnUi(() =>
    {
        var s = Session(document);
        if (x != null || y != null || width != null || height != null)
            if (x == null || y == null || width == null || height == null) throw new McpException("A region needs x, y, width and height.");
        var (pixels, toPixels) = Source(s, layer);
        SKRectI? region = null;
        if (x != null)
        {
            var mapped = toPixels.MapRect(SKRect.Create((float)x!.Value, (float)y!.Value, (float)width!.Value, (float)height!.Value));
            region = SKRectI.Create((int)Math.Floor(mapped.Left), (int)Math.Floor(mapped.Top), (int)Math.Ceiling(mapped.Width), (int)Math.Ceiling(mapped.Height));
        }
        if (!toPixels.TryInvert(out var toCanvas)) throw new McpException("That layer is too small to trace.");
        var edges = EdgeTracer.Trace(pixels, Math.Clamp(detail, 0, 100), Math.Max(0, minLength), Math.Max(0, simplify), Math.Clamp(maxLines, 1, 1000), region);
        if (edges.Count == 0) return "No edges found; raise detail or lower minLength.";
        var text = new StringBuilder();
        text.Append(edges.Count).AppendLine(" edges, longest first, as x,y points in canvas pixels:");
        foreach (var edge in edges)
        {
            var first = true;
            foreach (var point in edge)
            {
                var p = toCanvas.MapPoint(point);
                if (!first) text.Append(' ');
                text.Append(p.X.ToString("0")).Append(',').Append(p.Y.ToString("0"));
                first = false;
            }
            text.AppendLine();
        }
        return text.ToString().TrimEnd();
    });

    /// <summary>What to read: the composite in canvas coordinates, or a layer's pixels with the matrix from canvas to those pixels.</summary>
    private static (SKBitmap Pixels, SKMatrix ToPixels) Source(EditorSession s, string? layer)
    {
        if (layer == null) return (s.Composite(), SKMatrix.Identity);
        var target = Find(s, layer).WarpAppearance();
        if (target.Pixels == null) throw new McpException($"\"{target.Name}\" is a {Kind(target)} layer without pixels of its own; leave layer out to read what is on screen.");
        if (!target.Matrix.TryInvert(out var toPixels)) throw new McpException($"\"{target.Name}\" is too small to read.");
        return (target.RenderFilterPixels()!, toPixels);
    }
}
