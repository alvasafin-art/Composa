using Composa.AI;
using Composa.Filters;
using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.Editing;

public sealed record AiOutput(string Name, SKBitmap Pixels, SKBitmap? Mask = null, IReadOnlyCollection<string>? Tags = null, SKRect? Bounds = null);

public sealed partial class EditorSession
{
    /// <summary>Matches a raster layer to the surrounding scene with ordinary editable adjustment layers.</summary>
    public IReadOnlyList<Layer> MatchActiveLayerToScene()
    {
        var active = ActiveLayer;
        if (active?.Pixels == null) throw new InvalidOperationException("Select an image layer first.");
        using var scene = Flatten();
        var subject = document.Selection == null
            ? ImageStatistics.Of(active.Pixels, null, active.Pixels.Info.Rect)
            : ImageStatistics.Of(scene, document.Selection, document.Bounds);
        var visible = active.VisibleBounds;
        var subjectBounds = document.Selection != null ? SelectionMask.Bounds(document.Selection) : new SKRectI(
            (int)Math.Floor(visible.Left), (int)Math.Floor(visible.Top), (int)Math.Ceiling(visible.Right), (int)Math.Ceiling(visible.Bottom));
        var ringSize = Math.Clamp(Math.Max(subjectBounds.Width, subjectBounds.Height) / 3, 16, 256);
        var ring = new SKRectI(Math.Max(0, subjectBounds.Left - ringSize), Math.Max(0, subjectBounds.Top - ringSize),
            Math.Min(document.Width, subjectBounds.Right + ringSize), Math.Min(document.Height, subjectBounds.Bottom + ringSize));
        var surroundings = ImageStatistics.Of(scene, document.Selection, ring, excludeMask: document.Selection != null,
            excludedBounds: document.Selection == null ? subjectBounds : null);
        if (subject.Count == 0 || surroundings.Count == 0)
            throw new InvalidOperationException("There is not enough visible surrounding image to match this layer to the scene.");

        var exposure = Math.Clamp(Math.Log2(Math.Max(1, surroundings.Luma) / Math.Max(1, subject.Luma)), -2, 2);
        var balance = new ColorBalanceAdjustment()
            .WithShift(1, 0, Math.Clamp((surroundings.Red - subject.Red) * 0.55, -45, 45))
            .WithShift(1, 1, Math.Clamp((surroundings.Green - subject.Green) * 0.55, -45, 45))
            .WithShift(1, 2, Math.Clamp((surroundings.Blue - subject.Blue) * 0.55, -45, 45));
        var adjustments = new List<(string Name, Adjustment Adjustment)>
        {
            ("Match Exposure", new ExposureAdjustment { Exposure = exposure }),
            ("Match Color", balance)
        };
        if (subject.Detail > surroundings.Detail * 1.2 && surroundings.Detail > 0)
            adjustments.Add(("Match Focus", new GaussianBlurAdjustment { Radius = Math.Clamp(subject.Detail / surroundings.Detail - 1, 0.3, 3) }));
        if (surroundings.Detail > subject.Detail * 1.18)
            adjustments.Add(("Match Grain", new AddNoiseAdjustment
            {
                Amount = Math.Clamp((surroundings.Detail - subject.Detail) / 1.4, AddNoiseAdjustment.MinAmount, 18),
                Gaussian = true, Monochromatic = true, Seed = (uint)(active.Id.GetHashCode() & int.MaxValue)
            }));

        var layers = adjustments.Select(item =>
        {
            var layer = Layer.ForAdjustment(item.Adjustment);
            layer.Name = item.Name;
            layer.Clipped = true;
            layer.Tags.Add("editable");
            if (document.Selection != null) layer.Mask = Pixels.Clone(document.Selection);
            return layer;
        }).ToList();
        Apply("AI Match to Scene", () =>
        {
            document.SetActive(active.Id);
            foreach (var layer in layers) document.InsertAboveActive(layer);
        });
        InvalidateAll();
        LayersChanged?.Invoke();
        return layers;
    }

    private readonly record struct ImageStatistics(long Count, double Red, double Green, double Blue, double Luma, double Detail)
    {
        public static ImageStatistics Of(SKBitmap image, SKBitmap? mask, SKRectI area, bool excludeMask = false, SKRectI? excludedBounds = null)
        {
            area = SKRectI.Intersect(area, image.Info.Rect);
            long count = 0;
            double red = 0, green = 0, blue = 0, luma = 0, detail = 0;
            for (var y = area.Top; y < area.Bottom; y++)
                for (var x = area.Left; x < area.Right; x++)
                {
                    if (excludedBounds is { } excluded && excluded.Contains(x, y)) continue;
                    if (mask != null && (mask.GetPixel(x, y).Alpha >= 128) == excludeMask) continue;
                    var color = image.GetPixel(x, y);
                    if (color.Alpha < 16) continue;
                    var brightness = (color.Red * 54 + color.Green * 183 + color.Blue * 19) / 256.0;
                    count++; red += color.Red; green += color.Green; blue += color.Blue; luma += brightness;
                    if (x > area.Left)
                    {
                        var left = image.GetPixel(x - 1, y);
                        detail += Math.Abs(brightness - (left.Red * 54 + left.Green * 183 + left.Blue * 19) / 256.0);
                    }
                }
            return count == 0 ? default : new(count, red / count, green / count, blue / count, luma / count, detail / count);
        }
    }

    public void ApplyAiSelection(AiTaskKind task, SKBitmap mask)
    {
        if (mask.ColorType != SKColorType.Alpha8 || mask.Width != document.Width || mask.Height != document.Height)
            throw new ArgumentException("An AI selection must be a document-sized Alpha8 bitmap.");
        SetSelection("AI " + task.DisplayName(), mask);
    }

    /// <summary>Inserts every part of an AI result as one non-destructive, undoable editor transaction.</summary>
    public IReadOnlyList<Layer> InsertAiOutput(AiTaskKind task, IReadOnlyList<AiOutput> outputs, bool group = false)
    {
        if (outputs.Count == 0) return [];
        var layers = outputs.Select(output =>
        {
            if (output.Pixels.ColorType != SKColorType.Rgba8888) throw new ArgumentException("AI output must be an RGBA8888 bitmap.");
            if (output.Mask != null && (output.Mask.ColorType != SKColorType.Alpha8 || output.Mask.Width != output.Pixels.Width || output.Mask.Height != output.Pixels.Height))
                throw new ArgumentException("An AI output mask must be Alpha8 and match its image.");
            var layer = Layer.Raster(string.IsNullOrWhiteSpace(output.Name) ? "AI " + task.DisplayName() : output.Name, output.Pixels);
            layer.Mask = output.Mask;
            if (output.Bounds is { Width: > 0, Height: > 0 } bounds)
                layer.Transform = new LayerTransform { X = bounds.Left, Y = bounds.Top, Width = bounds.Width, Height = bounds.Height };
            layer.Tags.Add("ai-generated");
            if (output.Tags != null) foreach (var tag in output.Tags) if (LayerTags.Normalize(tag) is { } normalized) layer.Tags.Add(normalized);
            return layer;
        }).ToList();
        Apply("AI " + task.DisplayName(), () =>
        {
            if (group)
            {
                var folder = Layer.Group("AI " + task.DisplayName());
                folder.Tags.Add("ai-generated");
                folder.Children.AddRange(layers);
                document.InsertAboveActive(folder);
            }
            else foreach (var layer in layers) document.InsertAboveActive(layer);
            document.SelectedLayerIds.Clear();
            if (!group)
            {
                foreach (var layer in layers) document.SelectedLayerIds.Add(layer.Id);
                document.ActiveLayerId = layers[^1].Id;
            }
        });
        InvalidateAll();
        LayersChanged?.Invoke();
        return layers;
    }

    public IEnumerable<Layer> FindLayersByTag(string tag)
    {
        var normalized = LayerTags.Normalize(tag);
        return normalized == null ? [] : document.AllLayers().Where(layer => layer.Tags.Contains(normalized));
    }

    public void SetLayerTags(Layer layer, IEnumerable<string> tags)
    {
        var chosen = tags.Select(LayerTags.Normalize).Where(tag => tag != null).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (layer.Tags.SetEquals(chosen)) return;
        Apply("Edit Layer Tags", () =>
        {
            layer.Tags.Clear();
            layer.Tags.UnionWith(chosen);
        });
        LayersChanged?.Invoke();
    }

    private SKBitmap? selectionBrushShape;
    private SKBitmap? selectionBrushBase;
    private SelectionMode selectionBrushOperation;
    private SKPoint? selectionBrushLast;
    public double SelectionBrushSize { get; set; } = 64;
    public double SelectionBrushFeather { get; set; }
    public SelectionMode SelectionBrushMode { get; set; } = SelectionMode.Add;
    public bool IsPaintingSelection => selectionBrushShape != null;

    public void BeginSelectionBrush(SKPoint point, SelectionMode? mode = null)
    {
        if (selectionBrushShape != null) EndSelectionBrush();
        Begin("Selection Brush");
        selectionBrushBase = document.Selection;
        selectionBrushShape = Pixels.NewMask(document.Width, document.Height);
        selectionBrushOperation = mode ?? SelectionBrushMode;
        if (selectionBrushBase == null && selectionBrushOperation == SelectionMode.Add) selectionBrushOperation = SelectionMode.Replace;
        selectionBrushLast = point;
        PaintSelectionSegment(point, point);
    }

    public void ContinueSelectionBrush(SKPoint point)
    {
        if (selectionBrushShape == null || selectionBrushLast == null) return;
        PaintSelectionSegment(selectionBrushLast.Value, point);
        selectionBrushLast = point;
    }

    public void EndSelectionBrush()
    {
        if (selectionBrushShape == null) return;
        selectionBrushShape.Dispose();
        selectionBrushShape = null;
        selectionBrushBase = null;
        selectionBrushLast = null;
        Commit();
        SelectionChanged?.Invoke();
    }

    public void CancelSelectionBrush()
    {
        if (selectionBrushShape == null) return;
        var preview = document.Selection;
        selectionBrushShape.Dispose();
        selectionBrushShape = null;
        selectionBrushBase = null;
        selectionBrushLast = null;
        Cancel();
        if (preview != null && !ReferenceEquals(preview, document.Selection)) preview.Dispose();
        SelectionChanged?.Invoke();
    }

    private void PaintSelectionSegment(SKPoint from, SKPoint to)
    {
        var shape = selectionBrushShape!;
        using (var canvas = new SKCanvas(shape))
        using (var paint = new SKPaint
        {
            Color = SKColors.Black,
            StrokeWidth = (float)Math.Clamp(SelectionBrushSize, 1, 2000),
            StrokeCap = SKStrokeCap.Round,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
            ImageFilter = SelectionBrushFeather > 0 ? SKImageFilter.CreateBlur((float)SelectionBrushFeather / 2, (float)SelectionBrushFeather / 2) : null
        })
        {
            canvas.DrawLine(from, to, paint);
            if (from == to)
            {
                paint.Style = SKPaintStyle.Fill;
                canvas.DrawCircle(to, paint.StrokeWidth / 2, paint);
            }
        }
        Pixels.Invalidate(shape);
        var previous = document.Selection;
        document.Selection = SelectionMask.Combine(selectionBrushBase, Pixels.Clone(shape), selectionBrushOperation);
        if (previous != null && !ReferenceEquals(previous, selectionBrushBase) && !ReferenceEquals(previous, document.Selection)) previous.Dispose();
        SelectionChanged?.Invoke();
    }
}
