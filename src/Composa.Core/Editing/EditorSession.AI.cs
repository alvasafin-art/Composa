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
    /// <summary>Commits all scene variants and source visibility together. Original layers remain recoverable.</summary>
    public void InsertSceneEdit(AiTaskKind task, Guid? sourceLayer, IReadOnlyList<SceneEditResult> results, bool cutout, bool wholeImage,IReadOnlyList<Guid>? previousResults=null,SKMatrix? sourceFrame=null, bool layerOnly=false)
    {
        if(results.Count==0) throw new ArgumentException("No scene results.");
        var incoming=results.SelectMany(result=>result.Layers).SelectMany(output=>output.Mask==null ? new[] {output.Pixels} : new[] {output.Pixels,output.Mask}).Distinct<SKBitmap>(ReferenceEqualityComparer.Instance);
        if(document.RasterPixels()+incoming.Sum(bitmap=>(long)bitmap.Width*bitmap.Height)>DocumentLimits.DocumentPixelBudget)
            throw new InvalidOperationException($"Scene results exceed the document's {DocumentLimits.DocumentBudgetMegapixels} MP budget.");
        var roots=wholeImage && !layerOnly ? document.Layers.ToArray() : [];
        var anchor=layerOnly ? null : document.ActiveLayer;
        while(anchor!=null&&document.ParentOf(anchor.Id) is { } parent&&(parent.Tags.Contains("ai-generated")||parent.Tags.Contains("ai-variants"))) anchor=parent;
        if(!layerOnly && sourceLayer is { } originalId&&!wholeImage)
            results=results.Select(result=>result with {Layers=result.Layers.Select(output=>output with {Tags=(output.Tags??[]).Append(SceneSource.Tag(originalId)).Concat(SceneSource.FrameTags(sourceFrame??document.Find(originalId)?.Matrix??SKMatrix.Identity)).ToArray()}).ToArray()}).ToArray();
        RunTransaction("AI "+task.DisplayName(),_=>
        {
            if(layerOnly) document.SetActive(null);
            else if(wholeImage && roots.Length>0) document.SetActive(roots[^1].Id);
            else if(anchor!=null) document.SetActive(anchor.Id);
            var collapsed=anchor?.Collapsed;
            if(anchor?.IsGroup==true) anchor.Collapsed=true; // Insert beside the previous result, never inside it.
            if(results.Count==1) InsertAiOutput(task,results[0].Layers,group:true);
            else
            {
                var folder=Layer.Group("AI "+task.DisplayName()+" Variants"); folder.Tags.Add("ai-variants");
                document.InsertAboveActive(folder);
                for(var index=0;index<results.Count;index++)
                {
                    document.SetActive(folder.Id);
                    var variant=Layer.Group("Variant "+(index+1)); variant.Visible=index==0; folder.Children.Add(variant);
                    document.SetActive(variant.Id); InsertAiOutput(task,results[index].Layers);
                }
                document.SetActive(folder.Id);
            }
            if(anchor!=null&&collapsed is { } wasCollapsed) anchor.Collapsed=wasCollapsed;
            if(layerOnly) return;
            if(previousResults!=null) foreach(var id in previousResults) if(document.Find(id) is { } previous) previous.Visible=false;
            if(wholeImage) foreach(var layer in roots) layer.Visible=false;
            else if((cutout || task==AiTaskKind.ChangeBackground) && sourceLayer is { } id && document.Find(id) is { } source) source.Visible=false;
        });
        InvalidateAll(); LayersChanged?.Invoke();
    }
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

    public void ApplyAiSelection(AiTaskKind task, SKBitmap mask, SelectionMode mode = SelectionMode.Replace, bool recoverForeground = true)
    {
        if (mask.ColorType != SKColorType.Alpha8 || mask.Width != document.Width || mask.Height != document.Height)
            throw new ArgumentException("An AI selection must be a document-sized Alpha8 bitmap.");
        if (recoverForeground && task is (AiTaskKind.ObjectSelection or AiTaskKind.SelectSubject))
        {
            SelectForeground(mask, mode, "AI " + task.DisplayName());
            return;
        }
        var combined = SelectionMask.Combine(document.Selection, mask, mode);
        if (!ReferenceEquals(combined, mask) && !ReferenceEquals(document.Selection, mask)) mask.Dispose();
        SetSelection("AI " + task.DisplayName(), combined);
    }

    /// <summary>Inserts every part of an AI result as one non-destructive, undoable editor transaction.</summary>
    public IReadOnlyList<Layer> InsertAiOutput(AiTaskKind task, IReadOnlyList<AiOutput> outputs, bool group = false, bool variants = false, bool atTop = false)
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
            if (atTop) document.SetActive(null);
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
    private SKBitmap? selectionBrushPreview;
    private int selectionBrushWitness;
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
        selectionBrushPreview = selectionBrushOperation switch
        {
            SelectionMode.Replace => selectionBrushShape,
            SelectionMode.Add or SelectionMode.Subtract when selectionBrushBase != null => Pixels.Clone(selectionBrushBase),
            SelectionMode.Intersect when selectionBrushBase != null => Pixels.NewLike(selectionBrushShape),
            _ => null
        };
        selectionBrushWitness = selectionBrushOperation is SelectionMode.Add or SelectionMode.Subtract && selectionBrushPreview != null
            ? selectionBrushPreview.GetPixelSpan().IndexOfAnyExcept((byte)0) : -1;
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
        CloseSelectionBrush(keep: document.Selection);
        Commit();
        SelectionChanged?.Invoke();
    }

    public void CancelSelectionBrush()
    {
        if (selectionBrushShape == null) return;
        document.Selection = selectionBrushBase;
        CloseSelectionBrush(keep: null);
        Cancel();
        SelectionChanged?.Invoke();
    }

    private void CloseSelectionBrush(SKBitmap? keep)
    {
        if (!ReferenceEquals(selectionBrushShape, keep)) selectionBrushShape?.Dispose();
        if (!ReferenceEquals(selectionBrushPreview, selectionBrushShape) && !ReferenceEquals(selectionBrushPreview, keep))
            selectionBrushPreview?.Dispose();
        selectionBrushShape = selectionBrushBase = selectionBrushPreview = null;
        selectionBrushLast = null;
    }

    private void PaintSelectionSegment(SKPoint from, SKPoint to)
    {
        var shape = selectionBrushShape!;
        var preview = selectionBrushPreview;
        if (preview == null)
        {
            document.Selection = null;
            SelectionChanged?.Invoke();
            return;
        }
        var width = (float)Math.Clamp(SelectionBrushSize, 1, 2000);
        var sigma = (float)SelectionBrushFeather / 2;
        using (var canvas = new SKCanvas(shape))
        using (var blur = sigma > 0 ? SKImageFilter.CreateBlur(sigma, sigma) : null)
        using (var paint = new SKPaint
        {
            Color = SKColors.Black,
            StrokeWidth = width,
            StrokeCap = SKStrokeCap.Round,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
            ImageFilter = blur
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
        var padding = width / 2 + 3 * Math.Max(0, sigma) + 2;
        var bounds = Geometry.Intersect(Geometry.RoundOut(new SKRect(
            Math.Min(from.X, to.X) - padding, Math.Min(from.Y, to.Y) - padding,
            Math.Max(from.X, to.X) + padding, Math.Max(from.Y, to.Y) + padding)), shape.Info.Rect);
        if (!bounds.IsEmpty && !ReferenceEquals(preview, shape))
        {
            // Recombine against the original selection, including on repeated passes.
            // Only this segment can have changed the accumulated brush shape.
            using var canvas = new SKCanvas(preview);
            canvas.ClipRect(new SKRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawBitmap(selectionBrushBase!, 0, 0, paint);
            paint.BlendMode = selectionBrushOperation switch
            {
                SelectionMode.Add => SKBlendMode.SrcOver,
                SelectionMode.Subtract => SKBlendMode.DstOut,
                _ => SKBlendMode.DstIn
            };
            canvas.DrawBitmap(shape, 0, 0, paint);
            canvas.Flush();
            Pixels.Invalidate(preview);
        }
        var bytes = preview.GetPixelSpan();
        if (selectionBrushWitness < 0 || bytes[selectionBrushWitness] == 0)
        {
            // Subtraction can erase the last known pixel anywhere in an existing mask.
            // The other modes only add coverage, so an empty result can change locally.
            selectionBrushWitness = -1;
            if (selectionBrushOperation == SelectionMode.Subtract)
                selectionBrushWitness = bytes.IndexOfAnyExcept((byte)0);
            else if (!bounds.IsEmpty)
                for (var y = bounds.Top; y < bounds.Bottom && selectionBrushWitness < 0; y++)
                {
                    var start = y * preview.RowBytes + bounds.Left;
                    var offset = bytes.Slice(start, bounds.Width).IndexOfAnyExcept((byte)0);
                    if (offset >= 0) selectionBrushWitness = start + offset;
                }
        }
        document.Selection = selectionBrushWitness >= 0 ? preview : null;
        SelectionChanged?.Invoke();
    }
}
