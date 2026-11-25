using Composa.Model;
using Composa.Painting;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Editing;

public sealed partial class EditorSession
{
    private BrushStroke? stroke;
    private Layer? strokeLayer;
    private SKBitmap? strokeOriginal;
    private SKBitmap? strokeHealingSample;
    private LayerEffectsRenderer.StrokeBase? strokeEffectBase;
    private Guid? finishingStrokeEffect;
    private SKMatrix strokeToLayer;
    private BrushMode strokeMode;
    private SKPoint? cloneSource;
    private SKPoint? cloneOffset;
    private SKPoint? lastStrokeEnd;
    private SKPoint? smoothingAnchor;
    private SKPoint? smoothingPointer;
    private float strokePressure = 1;
    public SKPoint? BrushPosition => IsStroking ? smoothingAnchor : null;
    public float BrushPressure => IsStroking && PressureSensitive ? strokePressure : 1;

    public bool IsStroking => stroke != null;
    /// <summary>Screen pixels per document pixel, told by the canvas, so Smoothing feels the same at any zoom.</summary>
    public double ViewZoom { get; set; } = 1;
    /// <summary>Lets a pen's pressure vary the brush size.</summary>
    public bool PressureSensitive { get; set; } = true;
    /// <summary>Where the Clone Stamp samples from, in document space.</summary>
    public SKPoint? CloneSource => cloneSource;
    /// <summary>Where the clone source currently is while painting, for the crosshair overlay.</summary>
    public SKPoint? CloneSamplePoint(SKPoint cursor) =>
        cloneSource == null ? null : cloneOffset is { } o && (CloneAligned || stroke != null) ? new SKPoint(cursor.X + o.X, cursor.Y + o.Y) : cloneSource;
    /// <summary>Where the previous stroke ended, so Shift-click can draw a straight line from it.</summary>
    public SKPoint? LastStrokeEnd => lastStrokeEnd;

    public void SetCloneSource(SKPoint documentPoint)
    {
        cloneSource = documentPoint;
        cloneOffset = null;
    }

    /// <summary>The brush mode the current tool paints with.</summary>
    public BrushMode CurrentBrushMode => Tool switch
    {
        Tool.Eraser => BrushMode.Erase,
        Tool.SpotHealing => BrushMode.Heal,
        Tool.HealingBrush => BrushMode.Healing,
        Tool.CloneStamp => BrushMode.Clone,
        Tool.Smear => SmearMode switch
        {
            SmearMode.Liquify => BrushMode.Liquify, SmearMode.Smudge => BrushMode.Smudge, SmearMode.Dodge => BrushMode.Dodge, SmearMode.Burn => BrushMode.Burn, _ => BrushMode.Blur
        },
        _ => EraserMode ? BrushMode.Erase : BrushMode.Paint
    };

    /// <summary>
    /// Paints one whole stroke through the given points with its own brush and color, as an agent or a script does,
    /// leaving the tool, brush and colors as they were. Returns the reason when the active layer can't be painted.
    /// </summary>
    public string? PaintStroke(IReadOnlyList<SKPoint> points, BrushSettings brush, SKColor color, BrushMode mode = BrushMode.Paint)
    {
        if (points.Count == 0) return "A stroke needs at least one point.";
        var (savedBrush, savedForeground) = (Brush, Foreground);
        Brush = brush with { Smoothing = 0 };                             // The points are the stroke; nothing to smooth.
        Foreground = color;
        try
        {
            if (!BeginStroke(points[0], out var problem, mode: mode)) return problem;
            foreach (var point in points.Skip(1)) ContinueStroke(point);
            EndStroke();
            return null;
        }
        finally { (Brush, Foreground) = (savedBrush, savedForeground); }
    }

    /// <summary>
    /// Paints many strokes as one undoable step, "Brush Strokes", for an agent blocking in a picture or drawing every
    /// line it traced. Stops at the first stroke that cannot be painted and says why, with how many landed before it.
    /// </summary>
    public (int Painted, string? Problem) PaintStrokes(IReadOnlyList<PlannedStroke> strokes)
    {
        var painted = 0;
        foreach (var stroke in strokes)
        {
            var entries = History.Count;
            var problem = PaintStroke(stroke.Points, stroke.Brush, stroke.Color, stroke.Mode);
            if (problem != null) return (painted, problem);
            painted++;
            // A stroke that touched nothing left no entry; every other one folds into the first.
            if (painted > 1 && History.Count == entries + 1) FoldLastStep("Brush Strokes");
        }
        return (painted, null);
    }

    /// <summary>Starts painting at a document point. Returns false (with a reason) when the active layer can't be painted.</summary>
    /// <param name="mode">The brush to use; the current tool's when left out.</param>
    public bool BeginStroke(SKPoint point, out string? problem, bool lineFromLast = false, BrushMode? mode = null, float pressure = 1)
    {
        problem = null;
        if (PixelEditLayer is not { } layer) { problem = "Select a layer to paint on."; return false; }
        if (PixelsLocked(layer)) { problem = "Unlock the layer's pixels before painting."; return false; }
        if (!IsEditingMask && layer.Pixels == null) { problem = layer.IsGroup ? "Folders can't be painted on. Select a layer inside." : "Adjustment layers have no pixels. Add a mask to paint on."; return false; }
        if (!IsEditingMask && layer.IsLive) { problem = layer.IsSmartObject ? "Open the smart object's contents to paint, or rasterize it (Layer menu)." : $"This is live {(layer.Text != null ? "text" : "shape")}. Rasterize it (Layer menu) to paint on it."; return false; }
        if (!document.IsEffectivelyVisible(layer)) { problem = "The layer is hidden."; return false; }
        mode ??= CurrentBrushMode;
        var channelErase = IsEditingComponent && mode == BrushMode.Erase;
        if (channelErase) mode = BrushMode.Paint;
        if (mode is BrushMode.Clone or BrushMode.Healing && cloneSource == null) { problem = "Alt-click to set the source first."; return false; }
        if (mode is BrushMode.Heal or BrushMode.Healing && IsEditingMask) { problem = "Healing brushes work on pixels, not masks."; return false; }

        var effectBase = ViewZoom < 1 && document.WorkingDepth == ColorDepth.Eight && mode is BrushMode.Paint or BrushMode.Erase
            ? LayerEffectsRenderer.CaptureStrokeBase(layer) : null;
        Begin(channelErase ? "Eraser" : mode.Value switch
        {
            BrushMode.Erase => "Eraser", BrushMode.Clone => "Clone Stamp", BrushMode.Heal => "Spot Healing Brush", BrushMode.Healing => "Healing Brush",
            BrushMode.Liquify => "Liquify", BrushMode.Blur => "Blur", BrushMode.Smudge => "Smudge", BrushMode.Dodge => "Dodge", BrushMode.Burn => "Burn", _ => "Brush"
        });
        // A brush on a mask can paint anywhere on the canvas, as Photoshop's does, growing the mask past its layer. The
        // smearing brushes work the mask's own pixels and stay within it.
        SKBitmap target;
        try
        {
#if CANVAS_PERFORMANCE
            var preparing = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            if ((!IsEditingMask || mode is BrushMode.Paint or BrushMode.Erase) && !EffectiveLocks(layer).HasFlag(LayerLocks.Transparency)) GrowToCanvas(layer);
            target = Target(layer);
#if CANVAS_PERFORMANCE
            CanvasPerformanceTrace.StageCompleted("brush target preparation", preparing);
#endif
        }
        catch (InvalidOperationException error) { Cancel(); problem = error.Message; return false; }
        var matrix = TargetMatrix(layer);
        if (!matrix.TryInvert(out strokeToLayer)) { Cancel(); problem = "The layer is too small to paint on."; return false; }
        var scale = Math.Sqrt(Math.Abs(matrix.ScaleX * matrix.ScaleY - matrix.SkewX * matrix.SkewY));

        SKBitmap? source = null;
        var offset = SKPointI.Empty;
        var fraction = SKPoint.Empty;
        void SetOffset(SKPoint delta)
        {
            if (mode == BrushMode.Clone)
            {
                offset = new((int)MathF.Floor(delta.X), (int)MathF.Floor(delta.Y));
                fraction = new(delta.X - offset.X, delta.Y - offset.Y);
            }
            else offset = new((int)Math.Round(delta.X), (int)Math.Round(delta.Y));
        }
        if (mode is BrushMode.Clone or BrushMode.Healing)
        {
            if (!CloneAligned || cloneOffset == null) cloneOffset = new SKPoint(cloneSource!.Value.X - point.X, cloneSource.Value.Y - point.Y);
            var o = cloneOffset.Value;
            if (SampleAllLayers && !IsEditingMask && matrix.ScaleX == 1 && matrix.ScaleY == 1 && matrix.SkewX == 0 && matrix.SkewY == 0)
            {
                source = CompositePixels();
                SetOffset(new SKPoint(o.X + matrix.TransX, o.Y + matrix.TransY));
            }
            else if (SampleAllLayers && !IsEditingMask)
            {
                // Sample doc = ToDocument(target pixel) + source offset. Baking the translation
                // here also allows a donor outside the active layer's transformed bounds.
                source = CompositeInStrokeSpace(target, o);
            }
            else
            {
                source = target;
                var v = strokeToLayer.MapVector(o.X, o.Y);
                SetOffset(v);
            }
        }
        var color = mode == BrushMode.Paint ? ChannelPaintColor(channelErase ? Background : Foreground) : SKColors.Black;
#if CANVAS_PERFORMANCE
        var snapshotting = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        stroke = new BrushStroke(target, Brush, mode.Value, color, scale, LayerPixelStorage.CopyForStroke(target))
        {
            Selection = document.Selection, ToDocument = matrix, CloneSource = source, CloneOffset = offset, CloneFraction = fraction,
            LockTransparency = !IsEditingMask && (IsEditingComponent || EffectiveLocks(layer).HasFlag(LayerLocks.Transparency))
        };
#if CANVAS_PERFORMANCE
        CanvasPerformanceTrace.StageCompleted("brush snapshot and setup", snapshotting);
        CanvasPerformanceTrace.SurfaceAllocated("stroke working surface", stroke.Working);
#endif
        strokeLayer = layer;
        strokeOriginal = target;
        strokeEffectBase = effectBase;
        if (mode == BrushMode.Heal && SampleAllLayers)
        {
            // Snapshot before the dark drag overlay, mapping the visible document into
            // the active layer's local grid (including transformed retouch layers).
            strokeHealingSample = CompositeInStrokeSpace(target, SKPoint.Empty);
        }
        strokeMode = mode.Value;
        SetTarget(layer, stroke.Working, constrainChannels: false);
        Pixels.SetLive(stroke.Working, true);
        if (lineFromLast && lastStrokeEnd is { } from)
        {
            var changed = stroke.AddPoint(strokeToLayer.MapPoint(from));
            strokeEffectBase?.AddDamage(changed);
        }
        // The first dab always lands; from here on Smoothing decides how the brush follows.
        smoothingAnchor = smoothingPointer = point;
        strokePressure = pressure;
        Paint(point, pressure);
        if (lineFromLast) Invalidate(AffectedArea(layer));
        return true;
    }

    private SKBitmap CompositeInStrokeSpace(SKBitmap target, SKPoint documentOffset)
    {
        using var visible = CompositePixels();
        var sample = Pixels.NewLike(target);
        using var canvas = new SKCanvas(sample);
        canvas.SetMatrix(strokeToLayer);
        canvas.DrawImage(Pixels.ImageOf(visible), -documentOffset.X, -documentOffset.Y, new SKSamplingOptions(SKFilterMode.Linear));
        return sample;
    }

    public void ContinueStroke(SKPoint point, float pressure = 1)
    {
        if (stroke == null || strokeLayer == null) return;
        if (TrackStrokePointer(point, pressure) is { } painted) Paint(painted, pressure);
    }

    /// <summary>Applies the ordered samples delivered together in a pointer event.</summary>
    public void ContinueStrokeSamples(ReadOnlySpan<(SKPoint Point, float Pressure)> points)
    {
        if (stroke is not { } current || strokeLayer == null) return;
        if (points.Length < 2 || strokeMode is not (BrushMode.Paint or BrushMode.Erase))
        {
            foreach (var (point, pressure) in points) ContinueStroke(point, pressure);
            return;
        }

        Span<(SKPoint Point, float Pressure)> paintedPoints = points.Length <= 64
            ? stackalloc (SKPoint, float)[points.Length] : new (SKPoint, float)[points.Length];
        var count = 0;
        foreach (var (point, pressure) in points)
        {
            if (TrackStrokePointer(point, pressure) is not { } painted) continue;
            lastStrokeEnd = painted;
            paintedPoints[count++] = (strokeToLayer.MapPoint(painted), PressureSensitive ? pressure : 1);
        }
        if (count == 0) return;
#if CANVAS_PERFORMANCE
        var dabbing = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        var changed = current.AddPoints(paintedPoints[..count]);
#if CANVAS_PERFORMANCE
        CanvasPerformanceTrace.StageCompleted("brush dabs", dabbing);
#endif
        PublishStrokeDamage(current, changed);
    }

    private SKPoint? TrackStrokePointer(SKPoint point, float pressure)
    {
        strokePressure = pressure;
        smoothingPointer = point;
        if (!IsSmoothing) smoothingAnchor = point;
        return Smoothed(point);
    }

    /// <summary>
    /// Where the brush actually is with Smoothing on: it trails the pointer on a string and only moves once the
    /// pointer pulls that string taut, the model Photoshop uses. The string's length is in screen points, so it feels
    /// the same however far the canvas is zoomed in. Null while the string is still slack, which is the whole point:
    /// those jitters never reach the stroke.
    /// </summary>
    private SKPoint? Smoothed(SKPoint point)
    {
        if (!IsSmoothing || smoothingAnchor is not { } anchor) return point;
        var radius = Brush.Smoothing / Math.Max(0.01, ViewZoom);
        float dx = point.X - anchor.X, dy = point.Y - anchor.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance <= radius) return null;
        var step = (float)((distance - radius) / distance);
        var moved = new SKPoint(anchor.X + dx * step, anchor.Y + dy * step);
        smoothingAnchor = moved;
        return moved;
    }

    /// <summary>Smoothing is offered for Paint and Erase; healing, cloning and smearing keep their own feel.</summary>
    private bool IsSmoothing => strokeMode is BrushMode.Paint or BrushMode.Erase && Brush.Smoothing > 0;

    private void Paint(SKPoint point, float pressure)
    {
        if (stroke is not { } current) return;
#if CANVAS_PERFORMANCE
        var dabbing = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        var changed = current.AddPoint(strokeToLayer.MapPoint(point), PressureSensitive ? pressure : 1);
#if CANVAS_PERFORMANCE
        CanvasPerformanceTrace.StageCompleted("brush dabs", dabbing);
#endif
        if (!ReferenceEquals(stroke, current)) return;
        lastStrokeEnd = point;
        PublishStrokeDamage(current, changed);
    }

    private void PublishStrokeDamage(BrushStroke current, SKRectI changed)
    {
        if (changed.IsEmpty || !ReferenceEquals(stroke, current) || strokeLayer is not { } layer) return;
        if (IsEditingComponent)
        {
            Pixels.ConstrainChannels(current.Working, strokeOriginal!, EditingRgbChannels, changed);
            if (!ReferenceEquals(stroke, current)) return;
        }
        strokeEffectBase?.AddDamage(changed);
        var area = Geometry.RoundOut(current.ToDocument.MapRect(SKRect.Create(changed.Left, changed.Top, changed.Width, changed.Height)));
        if (document.WorkingDepth == ColorDepth.Eight && StrokeCanUsePatch(layer) is { } effects)
        {
            // A reduced view can sample over the changed source pixels from an adjacent pixel.
            var halo = LayerEffectsRenderer.PatchHalo(effects) + (int)Math.Ceiling(2 / Math.Clamp(ViewZoom, .001, 1));
            area.Inflate(halo, halo);
        }
        area.Inflate(1, 1);
        // A clipping base or a folder/adjustment mask can affect other layers within this area.
        Invalidate(area);
    }

    private LayerEffects? StrokeCanUsePatch(Layer layer) =>
        strokeMode is BrushMode.Paint or BrushMode.Erase ? LayerEffectsRenderer.PatchEffects(layer) : null;

    private LayerEffectsRenderer.Patch? StrokeEffectPatch(SKRectI targetArea, RenderView view)
    {
        var finishing = finishingStrokeEffect;
        finishingStrokeEffect = null;
        var layer = strokeLayer ?? (finishing is { } id ? document.Find(id) : null);
        if (document.WorkingDepth != ColorDepth.Eight || layer == null
            || StrokeCanUsePatch(layer) is not { } effects)
            return null;
        var matrix = layer.Matrix;
        var x = view.Origin.X - matrix.TransX;
        var y = view.Origin.Y - matrix.TransY;
        var halo = (int)Math.Ceiling(DocumentRenderer.SamplingMargin(document) * view.Scale);
        targetArea.Inflate(halo, halo);
        var localArea = Geometry.RoundOut(new SKRect(
            x + targetArea.Left / view.Scale, y + targetArea.Top / view.Scale,
            x + targetArea.Right / view.Scale, y + targetArea.Bottom / view.Scale));
        var nativeGrid = view.Scale == 1 && x == MathF.Round(x) && y == MathF.Round(y);
        return LayerEffectsRenderer.RenderPatch(layer, localArea, effects, view.Scale, nativeGrid, strokeEffectBase);
    }

    public void EndStroke()
    {
        if (stroke is not { } current || strokeLayer == null) return;
        var layer = strokeLayer;
        // Smoothing leaves the brush short of the pointer; the stroke ends where the hand did.
        if (IsSmoothing && smoothingPointer is { } pointer && smoothingAnchor is { } anchor && pointer != anchor) Paint(pointer, strokePressure);
        if (!ReferenceEquals(stroke, current)) return;
        if (stroke.Touched.IsEmpty)
        {
            SetTarget(layer, strokeOriginal!, constrainChannels: false);
            CloseStroke();
            Cancel();
            return;
        }
        if (strokeMode is BrushMode.Heal or BrushMode.Healing)
        {
            var touched = stroke.Touched;
            using var mask = stroke.CoverageMask();
            if (strokeMode == BrushMode.Healing)
            {
                var coverage = mask.GetPixelSpan();
                var opacity = Math.Clamp(Brush.Opacity, 0, 1);
                var stride = mask.RowBytes;
                for (var y = touched.Top; y < touched.Bottom; y++)
                {
                    var row = coverage.Slice(y * stride + touched.Left, touched.Width);
                    for (var x = 0; x < row.Length; x++) row[x] = (byte)(row[x] * opacity);
                }
            }
            if ((long)stroke.Touched.Width * stroke.Touched.Height > Inpaint.MaxArea)
            {
                CloseStroke();
                Cancel();
                Problem?.Invoke("That area is too large to heal in one stroke. Heal it in smaller strokes.");
                return;
            }
            var selection = SelectionInTargetSpace(layer);
            using var donorExclusion = selection != null && strokeMode == BrushMode.Heal ? Pixels.Clone(mask) : null;
            if (selection != null)
            {
                // Search for donors for the ACTUAL repair area, not the entire brush footprint
                // outside a selection. Keep partial coverage for the final selection mix, once.
                var coverage = mask.GetPixelSpan(); var allowed = selection.GetPixelSpan();
                int maskStride = mask.RowBytes, selectionStride = selection.RowBytes;
                for (var y = touched.Top; y < touched.Bottom; y++)
                for (var x = touched.Left; x < touched.Right; x++)
                    if (allowed[y * selectionStride + x] == 0) coverage[y * maskStride + x] = 0;
                Pixels.Invalidate(mask);
            }
            // A single translated donor serves a dab. A long stroke may cross several different materials.
            bool singleDab = Math.Max(touched.Width, touched.Height) <= Brush.Size * 2;
            SKBitmap repair;
            if (strokeMode == BrushMode.Healing) repair = PatchBlend.Blend(strokeOriginal!, stroke.CloneSource!, mask, stroke.CloneOffset);
            else if (strokeHealingSample is { } sample)
            {
                using var hard = Pixels.Clone(mask);
                var coverage = hard.GetPixelSpan();
                var stride = hard.RowBytes;
                for (var y = touched.Top; y < touched.Bottom; y++)
                {
                    var row = coverage.Slice(y * stride + touched.Left, touched.Width);
                    for (var x = 0; x < row.Length; x++) if (row[x] > 0) row[x] = 255;
                }
                Pixels.Invalidate(hard);
                repair = Inpaint.Fill(sample, hard, donorExclusion, coherentSpot: singleDab);
                // Write only the stroke onto the retouch layer. Coverage is applied once,
                // not first to the composite and then again when transferring its pixels.
                repair = MixBySelection(strokeOriginal!, repair, mask);
            }
            else repair = Inpaint.Fill(strokeOriginal!, mask, donorExclusion, coherentSpot: singleDab);
            var healed = MixBySelection(strokeOriginal!, repair, selection);
            if (EffectiveLocks(layer).HasFlag(LayerLocks.Transparency))
            {
                var locked = Pixels.WithAlphaOf(healed, strokeOriginal!); healed.Dispose(); healed = locked;
            }
            if (selection != document.Selection) selection?.Dispose();
            SetTarget(layer, healed);
            Invalidate(AffectedArea(layer));
        }
        using var effectBase = strokeEffectBase;
        strokeEffectBase = null;
        CloseStroke();
        if (document.WorkingDepth == ColorDepth.Eight && StrokeCanUsePatch(layer) != null
            && (effectBase == null || !LayerEffectsRenderer.CacheStroke(layer, effectBase)))
            finishingStrokeEffect = layer.Id;
        Commit();
        LayersChanged?.Invoke();
    }

    public void CancelStroke()
    {
        if (stroke == null) return;
        finishingStrokeEffect = null;
        CloseStroke();
        Cancel();
    }

    private void CloseStroke()
    {
        if (stroke != null) { Pixels.SetLive(stroke.Working, false); Pixels.InvalidateCaches(stroke.Working); }
        if (stroke?.CloneSource != null && !ReferenceEquals(stroke.CloneSource, strokeOriginal)) stroke.CloneSource.Dispose();
        stroke?.Dispose();
        stroke = null;
        strokeLayer = null;
        strokeOriginal = null;
        strokeEffectBase?.Dispose();
        strokeEffectBase = null;
        strokeHealingSample?.Dispose(); strokeHealingSample = null;
        smoothingAnchor = smoothingPointer = null;
    }
}
