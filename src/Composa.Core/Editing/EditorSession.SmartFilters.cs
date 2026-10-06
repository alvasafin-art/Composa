using Composa.Filters;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Editing;

public sealed partial class EditorSession
{
    public bool CanSmartFilter => ActiveLayer is { Pixels: not null, Shape: null, Text: null } layer && !PixelsLocked(layer) && !IsEditingMask;
    public void PreviewSmartFilters(Layer layer, SmartFilter[] filters)
    {
        if (!IsInteracting || PixelsLocked(layer) || layer.Pixels == null) return;
        layer.FilterSource ??= layer.Pixels; layer.SmartFilters = filters;
        RenderSmartFilters(layer); InvalidateAll(); LayersChanged?.Invoke();
    }
    public void AddSmartFilter(Layer layer, FilterSettings settings)
    {
        FinishText();
        if (layer.Pixels == null || layer.Shape != null || layer.Text != null || PixelsLocked(layer) || settings.IsIdentity) return;
        if (layer.SmartFilters.Length >= 64) throw new InvalidOperationException("This layer already has 64 smart filters.");
        Apply("Add Smart Filter", () =>
        {
            layer.FilterSource ??= layer.Pixels;
            layer.SmartFilters = layer.SmartFilters.Append(new SmartFilter(Guid.NewGuid(), settings)).ToArray();
            RenderSmartFilters(layer);
        });
        InvalidateAll(); LayersChanged?.Invoke();
    }
    public void ChangeSmartFilter(Layer layer, Guid id, FilterSettings? settings = null, bool? enabled = null, bool remove = false)
    {
        FinishText();
        if (PixelsLocked(layer) || !layer.SmartFilters.Any(f => f.Id == id)) return;
        Apply(remove ? "Delete Smart Filter" : "Change Smart Filter", () =>
        {
            layer.SmartFilters = layer.SmartFilters.Where(f => !remove || f.Id != id).Select(f => f.Id == id ? f with { Settings = settings ?? f.Settings, Enabled = enabled ?? f.Enabled } : f).ToArray();
            RenderSmartFilters(layer);
            if (layer.SmartFilters.Length == 0) layer.FilterSource = null;
        });
        InvalidateAll(); LayersChanged?.Invoke();
    }
    private static LayerTransform SmartFilterSourceTransform(Layer layer)
    {
        var source = layer.FilterSource!; var old = layer.Pixels!; var matrix = layer.Matrix;
        var sourceCenter = matrix.MapPoint(layer.FilterPaddingX + source.Width / 2f, layer.FilterPaddingY + source.Height / 2f);
        var sourceWidth = layer.Transform.Width * source.Width / old.Width; var sourceHeight = layer.Transform.Height * source.Height / old.Height;
        return layer.Transform with { X = sourceCenter.X - sourceWidth / 2, Y = sourceCenter.Y - sourceHeight / 2, Width = sourceWidth, Height = sourceHeight };
    }
    private void RenderSmartFilters(Layer layer, Document? budgetDocument = null)
    {
        var source = layer.FilterSource!; var old = layer.Pixels!;
        var sourceTransform = SmartFilterSourceTransform(layer);
        var sourceWidth = sourceTransform.Width; var sourceHeight = sourceTransform.Height;
        var result = source; var growX = 0; var growY = 0;
        try
        {
            foreach (var filter in layer.SmartFilters.Where(f => f.Enabled))
            {
                var next = ImageFilters.Run(result, filter.Settings);
                if (!DocumentLimits.FitsSurface(next.Result.Width, next.Result.Height)) { next.Result.Dispose(); throw new InvalidOperationException("The smart filter exceeds the surface size limit."); }
                if (!ReferenceEquals(result, source)) result.Dispose(); result = next.Result; growX += next.GrowX; growY += next.GrowY;
            }
            if (layer.Transform.Distort != null && (growX != layer.FilterPaddingX || growY != layer.FilterPaddingY))
                throw new InvalidOperationException("Edit filters that expand the layer before applying a perspective transform, or rasterize the layer first.");
            var candidate = (budgetDocument ?? document).Clone();
            var proposed = candidate.Find(layer.Id)!; proposed.Pixels = result; proposed.FilterSource = source;
            if (candidate.RasterPixels() > DocumentLimits.DocumentPixelBudget)
                throw new InvalidOperationException("The smart filter exceeds the document raster budget.");
            var center = sourceTransform.Matrix(source.Width, source.Height).MapPoint(result.Width / 2f - growX, result.Height / 2f - growY);
            var w = sourceWidth * result.Width / source.Width; var h = sourceHeight * result.Height / source.Height;
            if (layer.Mask is { } mask && (old.Width != result.Width || old.Height != result.Height || growX != layer.FilterPaddingX || growY != layer.FilterPaddingY))
            {
                var next = Pixels.NewMask(result.Width, result.Height, 255);
                using var canvas = new SKCanvas(next); canvas.DrawBitmap(mask, growX - layer.FilterPaddingX, growY - layer.FilterPaddingY); layer.Mask = next;
            }
            if (layer.VectorMask is { } vector && (old.Width != result.Width || old.Height != result.Height || growX != layer.FilterPaddingX || growY != layer.FilterPaddingY))
            {
                var dx = growX - layer.FilterPaddingX; var dy = growY - layer.FilterPaddingY;
                layer.VectorMask = vector with { Nodes = vector.Nodes.Select(n => new BezierNode((n.X * old.Width + dx) / result.Width, (n.Y * old.Height + dy) / result.Height,
                    (n.InX * old.Width + dx) / result.Width, (n.InY * old.Height + dy) / result.Height, (n.OutX * old.Width + dx) / result.Width, (n.OutY * old.Height + dy) / result.Height)).ToArray() };
            }
            layer.Transform = sourceTransform with { X = center.X - w / 2, Y = center.Y - h / 2, Width = w, Height = h };
            layer.Pixels = result; layer.FilterPaddingX = growX; layer.FilterPaddingY = growY;
        }
        catch { if (!ReferenceEquals(result, source)) result.Dispose(); throw; }
    }
}
