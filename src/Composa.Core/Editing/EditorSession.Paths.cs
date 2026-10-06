using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;
namespace Composa.Editing;

public sealed partial class EditorSession
{
    public Layer AddPath(VectorPath documentPath)
    {
        documentPath = documentPath.Normalized();
        if (documentPath.Nodes.Length < 2) throw new ArgumentException("A path needs at least two finite nodes.");
        var nodes = documentPath.Nodes;
        var left = Math.Floor(nodes.Min(n => Math.Min(n.X, Math.Min(n.InX, n.OutX))) - 2);
        var top = Math.Floor(nodes.Min(n => Math.Min(n.Y, Math.Min(n.InY, n.OutY))) - 2);
        var width = Math.Max(2, (int)Math.Ceiling(nodes.Max(n => Math.Max(n.X, Math.Max(n.InX, n.OutX))) - left + 2));
        var height = Math.Max(2, (int)Math.Ceiling(nodes.Max(n => Math.Max(n.Y, Math.Max(n.InY, n.OutY))) - top + 2));
        if (!DocumentLimits.FitsSurface(width, height)) throw new InvalidOperationException("The path exceeds the surface size limit.");
        var local = documentPath with { Nodes = nodes.Select(n => new BezierNode((n.X - left) / width, (n.Y - top) / height, (n.InX - left) / width, (n.InY - top) / height, (n.OutX - left) / width, (n.OutY - top) / height)).ToArray() };
        var style = new ShapeStyle(ShapeKind.Path, (uint)Foreground, 0) { Path = local, FillEnabled = documentPath.Closed, Stroke = (uint)Foreground, StrokeWidth = 2 };
        var layer = Layer.Raster(document.UniqueName("Path"), RenderShape(style, width, height), left, top); layer.Shape = style;
        Apply("New Path", () => document.InsertAboveActive(layer)); InvalidateAll(); LayersChanged?.Invoke(); return layer;
    }
    public void PreviewVectorPath(Layer layer, VectorPath path, bool mask)
    {
        if (!IsInteracting || PixelsLocked(layer) || layer.Pixels == null) return;
        if (mask) layer.VectorMask = path;
        else if (layer.Shape is { } style) { layer.Shape = style with { Path = path }; layer.Pixels = RenderShape(layer.Shape, layer.Pixels.Width, layer.Pixels.Height); }
        InvalidateAll(); LayersChanged?.Invoke();
    }
    public void CompletePathEdit(Layer layer)
    {
        if (layer.Shape?.Path is not { Nodes.Length: >= 2 } geometry || layer.Pixels == null || PixelsLocked(layer)) return;
        var old = layer.Pixels; var nodes = geometry.Nodes;
        var minX = nodes.Min(n => Math.Min(n.X, Math.Min(n.InX, n.OutX))) * old.Width;
        var minY = nodes.Min(n => Math.Min(n.Y, Math.Min(n.InY, n.OutY))) * old.Height;
        var maxX = nodes.Max(n => Math.Max(n.X, Math.Max(n.InX, n.OutX))) * old.Width;
        var maxY = nodes.Max(n => Math.Max(n.Y, Math.Max(n.InY, n.OutY))) * old.Height;
        var pad = layer.Shape.Stroke == null ? 1 : layer.Shape.StrokeWidth / 2 + 1;
        var left = (int)Math.Floor(minX - pad); var top = (int)Math.Floor(minY - pad);
        var w = Math.Max(2, (int)Math.Ceiling(maxX + pad) - left); var h = Math.Max(2, (int)Math.Ceiling(maxY + pad) - top);
        if (!DocumentLimits.FitsSurface(w, h)) throw new InvalidOperationException("The edited path exceeds the surface size limit.");
        VectorPath Remap(VectorPath path) => path with { Nodes = path.Nodes.Select(n => new BezierNode((n.X * old.Width - left) / w, (n.Y * old.Height - top) / h,
            (n.InX * old.Width - left) / w, (n.InY * old.Height - top) / h, (n.OutX * old.Width - left) / w, (n.OutY * old.Height - top) / h)).ToArray() };
        var matrix = layer.Matrix; var center = matrix.MapPoint(left + w / 2f, top + h / 2f);
        var width = layer.Transform.Width * w / old.Width; var height = layer.Transform.Height * h / old.Height;
        if (layer.Mask is { } mask)
        {
            var moved = Pixels.NewMask(w, h); using var canvas = new SKCanvas(moved); canvas.DrawBitmap(mask, -left, -top); layer.Mask = moved;
        }
        if (layer.VectorMask != null) layer.VectorMask = Remap(layer.VectorMask);
        layer.Shape = layer.Shape with { Path = Remap(geometry) }; layer.Pixels = RenderShape(layer.Shape, w, h);
        layer.Transform = layer.Transform with { X = center.X - width / 2, Y = center.Y - height / 2, Width = width, Height = height };
        InvalidateAll(); LayersChanged?.Invoke();
    }
    public void PathToSelection(Layer layer)
    {
        var geometry = layer.VectorMask ?? layer.Shape?.Path;
        if (geometry == null || layer.Pixels == null) return;
        using var path = geometry.Build(layer.Pixels.Width, layer.Pixels.Height); path.Transform(layer.Matrix);
        using var mask = SelectionMask.FromPath(document.Width, document.Height, path); Select(mask, SelectionMode.Replace, "Path to Selection");
    }
    public void ApplyPathAsVectorMask(Layer source, Layer target)
    {
        var geometry = source.Shape?.Path;
        if (geometry == null || !geometry.Closed || source.Pixels == null || target.Pixels == null || PixelsLocked(target) || !target.Matrix.TryInvert(out var inverse)) return;
        var matrix = SKMatrix.Concat(inverse, source.Matrix);
        BezierNode Convert(BezierNode n)
        {
            var anchor = matrix.MapPoint((float)n.X * source.Pixels.Width, (float)n.Y * source.Pixels.Height);
            var incoming = matrix.MapPoint((float)n.InX * source.Pixels.Width, (float)n.InY * source.Pixels.Height);
            var outgoing = matrix.MapPoint((float)n.OutX * source.Pixels.Width, (float)n.OutY * source.Pixels.Height);
            return new(anchor.X / target.Pixels.Width, anchor.Y / target.Pixels.Height, incoming.X / target.Pixels.Width, incoming.Y / target.Pixels.Height, outgoing.X / target.Pixels.Width, outgoing.Y / target.Pixels.Height);
        }
        Apply("Vector Mask", () => target.VectorMask = geometry with { Nodes = geometry.Nodes.Select(Convert).ToArray() }); InvalidateAll(); LayersChanged?.Invoke();
    }
    public void RemoveVectorMask(Layer layer)
    {
        if (layer.VectorMask == null || PixelsLocked(layer)) return;
        Apply("Delete Vector Mask", () => layer.VectorMask = null); InvalidateAll(); LayersChanged?.Invoke();
    }
    public void RefinedSelectionToMask(SKBitmap selection)
    {
        if (ActiveLayer is not { Pixels: { } pixels } layer || PixelsLocked(layer) || !layer.Matrix.TryInvert(out var inverse)) return;
        var mask = SelectionMask.Remap(selection, pixels.Width, pixels.Height, inverse) ?? Pixels.NewMask(pixels.Width, pixels.Height);
        Apply("Refine Layer Mask", () => { layer.Mask = mask; layer.MaskEnabled = true; }); InvalidateAll(); LayersChanged?.Invoke();
    }
}
