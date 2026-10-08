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
    /// <summary>Matches visible masked layer pixels to their local scene with editable, noise-free curves.</summary>
    public IReadOnlyList<Layer> MatchActiveLayerToScene()
    {
        var active=ActiveLayer;
        if(active?.Pixels==null || active.IsAdjustment) throw new InvalidOperationException("Select an image layer first.");
        if (document.Selection != null && document.RasterPixels() + 2L * document.Width * document.Height > DocumentLimits.DocumentPixelBudget)
            throw new InvalidOperationException($"These adjustment masks exceed the document's {DocumentLimits.DocumentBudgetMegapixels} MP budget.");
        var visible=active.VisibleBounds;
        var bounds=document.Selection is { } selection ? SelectionMask.Bounds(selection)
            : new SKRectI((int)Math.Floor(visible.Left),(int)Math.Floor(visible.Top),(int)Math.Ceiling(visible.Right),(int)Math.Ceiling(visible.Bottom));
        bounds=SKRectI.Intersect(bounds,document.Bounds);
        if(bounds.IsEmpty) throw new InvalidOperationException("The layer has no visible pixels inside the canvas.");
        var margin=Math.Clamp(Math.Max(bounds.Width,bounds.Height)/3,16,256);
        var area=SKRectI.Intersect(new(bounds.Left-margin,bounds.Top-margin,bounds.Right+margin,bounds.Bottom+margin),document.Bounds);
        var scale=Math.Min(1f,512f/Math.Max(area.Width,area.Height));
        var width=Math.Max(1,(int)Math.Ceiling(area.Width*scale)); var height=Math.Max(1,(int)Math.Ceiling(area.Height*scale));
        using var subject=Pixels.NewColor(width,height); using var scene=Pixels.NewColor(width,height);
        var foreground=document.Clone(); var layer=foreground.Find(active.Id)!;
        foreground.Layers.Clear(); foreground.Layers.Add(layer); layer.Clipped=false;
        var view=new RenderView(scale,new(area.Left,area.Top));
        DocumentRenderer.Render(foreground,subject,subject.Info.Rect,view);
        var backdrop=document.Clone();
        if(document.Selection==null) backdrop.Find(active.Id)!.Visible=false;
        DocumentRenderer.Render(backdrop,scene,scene.Info.Rect,view);
        bool Selected(int x,int y) => document.Selection==null || document.Selection.GetPixel(
            Math.Min(document.Width-1,area.Left+(int)((x+.5)/scale)),Math.Min(document.Height-1,area.Top+(int)((y+.5)/scale))).Alpha>=128;
        var source=SceneMatcher.Measure(subject,Selected);
        var reference=SceneMatcher.Measure(scene,(x,y)=>document.Selection!=null ? !Selected(x,y) : subject.GetPixel(x,y).Alpha<16);
        if(reference.Count==0 && document.Selection==null) reference=SceneMatcher.Measure(scene,(_,_)=>true);
        var (light,color)=SceneMatcher.Match(source,reference);
        var layers=new[] { ("Match Light",light),("Match Color",color) }.Select(item=>
        {
            var adjustment=Layer.ForAdjustment(item.Item2); adjustment.Name=item.Item1; adjustment.Clipped=true;
            adjustment.Tags.Add("editable");
            if(document.Selection!=null) adjustment.Mask=Pixels.Clone(document.Selection);
            return adjustment;
        }).ToList();
        Apply("AI Match to Scene",()=>
        {
            document.SetActive(active.Id);
            foreach(var adjustment in layers) document.InsertAboveActive(adjustment);
        });
        InvalidateAll(); LayersChanged?.Invoke(); return layers;
    }

    public void ApplyAiSelection(AiTaskKind task, SKBitmap mask, SelectionMode mode = SelectionMode.Replace)
    {
        if (mask.ColorType != SKColorType.Alpha8 || mask.Width != document.Width || mask.Height != document.Height)
            throw new ArgumentException("An AI selection must be a document-sized Alpha8 bitmap.");
        var combined = SelectionMask.Combine(document.Selection, mask, mode);
        if (!ReferenceEquals(combined, mask) && !ReferenceEquals(document.Selection, mask)) mask.Dispose();
        SetSelection("AI " + task.DisplayName(), combined);
    }

    /// <summary>Inserts every part of an AI result as one non-destructive, undoable editor transaction.</summary>
    public IReadOnlyList<Layer> InsertAiOutput(AiTaskKind task, IReadOnlyList<AiOutput> outputs, bool group = false, bool variants = false)
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
        var incoming = new HashSet<SKBitmap>(ReferenceEqualityComparer.Instance);
        foreach (var layer in layers) { incoming.Add(layer.Pixels!); if (layer.Mask != null) incoming.Add(layer.Mask); }
        if (document.RasterPixels() + incoming.Sum(bitmap => (long)bitmap.Width * bitmap.Height) > DocumentLimits.DocumentPixelBudget)
            throw new InvalidOperationException($"These AI layers exceed the document's {DocumentLimits.DocumentBudgetMegapixels} MP budget.");
        Apply("AI " + task.DisplayName(), () =>
        {
            if (group || variants)
            {
                var folder = Layer.Group("AI " + task.DisplayName());
                folder.Tags.Add("ai-generated");
                if (variants)
                {
                    folder.Tags.Add("ai-variants");
                    for (var i = 0; i < layers.Count; i++) layers[i].Visible = i == 0;
                }
                folder.Children.AddRange(layers);
                document.InsertAboveActive(folder);
            }
            else foreach (var layer in layers) document.InsertAboveActive(layer);
            if (!group && !variants)
            {
                document.SelectedLayerIds.Clear();
                foreach (var layer in layers) document.SelectedLayerIds.Add(layer.Id);
                document.SelectionOrder.Clear(); document.SelectionOrder.AddRange(layers.Select(l => l.Id));
                document.SelectionAnchorId = layers[0].Id;
                document.ActiveLayerId = layers[^1].Id;
            }
        });
        InvalidateAll();
        LayersChanged?.Invoke();
        return layers;
    }

    public Layer? AiVariantGroup => ActiveLayer is { } active
        ? active.Tags.Contains("ai-variants") ? active : document.ParentOf(active.Id) is { } parent && parent.Tags.Contains("ai-variants") ? parent : null
        : null;

    public void SelectAiVariant(Layer group, int index)
    {
        if (document.Find(group.Id) != group || !group.Tags.Contains("ai-variants") || index < 0 || index >= group.Children.Count)
            throw new ArgumentException("Select a valid AI variant group.");
        if (group.Children.Select((layer, i) => layer.Visible == (i == index)).All(value => value)) return;
        Apply("Choose AI Variant", () =>
        {
            for (var i = 0; i < group.Children.Count; i++) group.Children[i].Visible = i == index;
            document.SetActive(group.Id);
        });
        InvalidateAll(); LayersChanged?.Invoke();
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
