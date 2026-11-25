using Composa.Model;
using SkiaSharp;

namespace Composa.Editing;

public enum TransformHandle { None, Move, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left, Rotate }

/// <summary>
/// An interactive move/scale/rotate of the selected layers. Every change is computed from the transforms captured
/// when the drag started, so rounding never accumulates.
/// </summary>
public sealed class TransformEdit
{
    private readonly EditorSession session;
    private readonly List<(Layer Layer, LayerTransform Start)> layers;
    // Folder and adjustment masks live in document space; they follow a move so they stay over their content.
    private readonly List<(Layer Layer, LayerTransform Start)> masks;
    private readonly Layer? maskTarget;
    private readonly bool hasFrameOverride;
    private SKPoint[]? overrideCorners;
    private sealed record LayoutPlacement(Layer Layer, AutoLayout? Layout, LayoutImageFrame? ImageFrame,
        LayerTransform? MaskTransform, SKMatrix? IndependentMask);
    private LayoutPlacement[]? layoutPlacements;
    public bool IsMovingAutoLayout { get; private set; }

    /// <summary>The frame being manipulated: an unrotated rectangle plus a rotation about its center.</summary>
    public SKRect StartFrame { get; }
    public double StartRotation { get; }
    public SKRect Frame { get; private set; }
    public double Rotation { get; private set; }
    public IReadOnlyList<Layer> Layers { get; }
    public IReadOnlySet<Guid> MovingLayerIds { get; }

    internal TransformEdit(EditorSession session, List<Layer> targets, List<Layer> maskOwners, Layer? maskTarget = null, LayerTransform? frameOverride = null)
    {
        this.session = session;
        this.maskTarget = maskTarget;
        hasFrameOverride = frameOverride != null;
        Layers = maskTarget != null ? [] : targets.AsReadOnly();
        MovingLayerIds = Layers.Select(layer => layer.Id).ToHashSet();
        if (maskTarget != null)
            targets = [new Layer { Pixels = maskTarget.Mask, Transform = maskTarget.MaskTransform ?? LayerTransform.FromMatrix(maskTarget.MaskMatrix, maskTarget.Mask!.Width, maskTarget.Mask.Height) }];
        layers = targets.Select(l => (l, l.Transform)).ToList();
        masks = maskOwners.Select(l => (l, LayerTransform.FromMatrix(l.MaskMatrix, l.Mask!.Width, l.Mask.Height))).ToList();
        if (frameOverride is { } frame)
        {
            StartFrame = SKRect.Create((float)frame.X, (float)frame.Y, (float)frame.Width, (float)frame.Height); StartRotation = frame.Rotation;
        }
        else if (layers.Count == 0) StartFrame = new SKRect(0, 0, session.Document.Width, session.Document.Height);
        else if (layers.Count == 1)
        {
            var t = maskTarget != null ? layers[0].Start : layers[0].Layer.ControlTransform;
            StartFrame = SKRect.Create((float)t.X, (float)t.Y, (float)t.Width, (float)t.Height);
            StartRotation = t.Rotation;
        }
        else
        {
            var bounds = SKRect.Empty;
            foreach (var (layer, _) in layers) bounds = bounds.IsEmpty ? layer.ControlBounds : SKRect.Union(bounds, layer.ControlBounds);
            StartFrame = bounds;
        }
        Frame = StartFrame;
        Rotation = StartRotation;
    }

    /// <summary>
    /// The frame's document-space corners, top-left first. A distorted layer's frame is the distorted shape itself, so
    /// the handles sit where the corners were dragged to rather than on the rectangle they started from.
    /// </summary>
    public SKPoint[] Corners()
    {
        if (overrideCorners != null) return overrideCorners;
        if (!hasFrameOverride && layers.Count == 1 && layers[0].Layer is { Transform.Distort: not null } distorted)
            return (maskTarget != null ? distorted.Transform : distorted.ControlTransform).FrameCorners();
        return FrameCorners(Frame, Rotation);
    }

    private static SKPoint[] FrameCorners(SKRect frame, double rotation) =>
        new LayerTransform { X = frame.Left, Y = frame.Top, Width = frame.Width, Height = frame.Height, Rotation = rotation }.FrameCorners();

    public void Set(SKRect frame, double rotation) => Set(frame, rotation, null);

    private void Set(SKRect frame, double rotation, SKPoint? layoutDelta)
    {
        if (session.Transform != this) return; // Already committed or cancelled.
        var before = Area();
        Frame = frame;
        Rotation = rotation;
        overrideCorners = null;
        if (layoutDelta is { } delta)
        {
            MoveMasks();
            foreach (var (layer, start) in layers) layer.Transform = start.Translated(delta.X, delta.Y);
            SetLayoutPlacement(delta);
        }
        else
        {
            if (IsMovingAutoLayout) SetLayoutPlacement(null);
            Update();
        }
        SyncMask();
        session.Invalidate(Geometry.Union(before, Area()));
    }

    public void MoveBy(float dx, float dy)
    {
        layoutPlacements ??= maskTarget != null || hasFrameOverride || session.IsEditingMask || session.IsEditingChannel
            ? []
            : session.LayoutMovePlacementTargets().Select(layer => new LayoutPlacement(layer, layer.AutoLayout, layer.LayoutFrame,
                layer.MaskTransform, layer.Mask != null && !layer.MaskLinked ? layer.MaskMatrix : null)).ToArray();
        Set(SKRect.Create(StartFrame.Left + dx, StartFrame.Top + dy, StartFrame.Width, StartFrame.Height),
            layoutPlacements.Length > 0 ? StartRotation : Rotation, layoutPlacements.Length > 0 ? new SKPoint(dx, dy) : null);
    }

    private void SetLayoutPlacement(SKPoint? delta)
    {
        IsMovingAutoLayout = delta != null;
        foreach (var (layer, layout, frame, mask, placement) in layoutPlacements!)
        {
            layer.AutoLayout = layout;
            layer.LayoutFrame = frame;
            if (delta is { } offset) layer.TranslateLayout(offset.X, offset.Y);
            if (placement is not { } matrix) continue;
            if (delta is { } shift && shift != SKPoint.Empty) layer.PlaceMask(matrix.PostConcat(SKMatrix.CreateTranslation(shift.X, shift.Y)));
            else layer.MaskTransform = mask;
        }
    }

    /// <summary>Drags a resize handle, optionally snapping to the canvas edges and layout guides.</summary>
    public (float? SnapX, float? SnapY) Resize(TransformHandle handle, SKPoint point, bool free, bool fromCenter, float snapTolerance = 0)
    {
        // Work in the frame's own unrotated space.
        var unrotate = SKMatrix.CreateRotationDegrees((float)-StartRotation, StartFrame.MidX, StartFrame.MidY);
        var p = unrotate.MapPoint(point);
        float left = StartFrame.Left, top = StartFrame.Top, right = StartFrame.Right, bottom = StartFrame.Bottom;
        var movesLeft = handle is TransformHandle.TopLeft or TransformHandle.Left or TransformHandle.BottomLeft;
        var movesRight = handle is TransformHandle.TopRight or TransformHandle.Right or TransformHandle.BottomRight;
        var movesTop = handle is TransformHandle.TopLeft or TransformHandle.Top or TransformHandle.TopRight;
        var movesBottom = handle is TransformHandle.BottomLeft or TransformHandle.Bottom or TransformHandle.BottomRight;
        if (movesLeft) left = p.X;
        if (movesRight) right = p.X;
        if (movesTop) top = p.Y;
        if (movesBottom) bottom = p.Y;

        if (!free && StartFrame.Width > 0 && StartFrame.Height > 0)
        {
            var aspect = StartFrame.Width / StartFrame.Height;
            float w = right - left, h = bottom - top;
            var corner = (movesLeft || movesRight) && (movesTop || movesBottom);
            if (corner)
            {
                if (Math.Abs(w) / aspect > Math.Abs(h)) h = Math.Abs(w) / aspect * Math.Sign(h == 0 ? 1 : h);
                else w = Math.Abs(h) * aspect * Math.Sign(w == 0 ? 1 : w);
                if (movesLeft) left = right - w; else right = left + w;
                if (movesTop) top = bottom - h; else bottom = top + h;
            }
            else if (movesLeft || movesRight)
            {
                h = Math.Abs(w) / aspect;
                top = StartFrame.MidY - h / 2; bottom = StartFrame.MidY + h / 2;
            }
            else
            {
                w = Math.Abs(h) * aspect;
                left = StartFrame.MidX - w / 2; right = StartFrame.MidX + w / 2;
            }
        }
        if (fromCenter)
        {
            float cx = StartFrame.MidX, cy = StartFrame.MidY;
            var halfW = movesLeft ? cx - left : movesRight ? right - cx : (right - left) / 2;
            var halfH = movesTop ? cy - top : movesBottom ? bottom - cy : (bottom - top) / 2;
            left = cx - halfW; right = cx + halfW; top = cy - halfH; bottom = cy + halfH;
        }
        if (Math.Abs(right - left) < 1) right = left + 1;
        if (Math.Abs(bottom - top) < 1) bottom = top + 1;

        // Resizing moves the center; keep the opposite edge fixed on screen by rotating the new center back.
        var rotate = SKMatrix.CreateRotationDegrees((float)StartRotation, StartFrame.MidX, StartFrame.MidY);
        (float? SnapX, float? SnapY) guides = default;
        var horizontal = movesLeft || movesRight;
        var vertical = movesTop || movesBottom;
        if (snapTolerance > 0 && (horizontal || vertical) && session.View.Snap)
        {
            var midX = (left + right) / 2;
            var midY = (top + bottom) / 2;
            var grab = new SKPoint(movesLeft ? left : movesRight ? right : midX, movesTop ? top : movesBottom ? bottom : midY);
            var anchor = fromCenter ? new SKPoint(midX, midY)
                : new SKPoint(movesLeft ? right : movesRight ? left : midX, movesTop ? bottom : movesBottom ? top : midY);
            var freeCorner = free && horizontal && vertical;
            var frame = new SKRect(left, top, right, bottom);
            var grip = rotate.MapPoint(grab);
            var ray = rotate.MapVector(grab - anchor);
            var (snapped, x, y) = session.SnapLayoutPoint(grip, snapTolerance, freeCorner ? null : ray);
            if (session.View.SnapToDocumentBounds && maskTarget == null && !hasFrameOverride)
            {
                var (canvasPoint, canvasX, canvasY) = SnapResizeToCanvas(frame, anchor, grip, ray, rotate,
                    snapTolerance, free, horizontal, vertical);
                if (freeCorner)
                {
                    if (canvasX.HasValue && (!x.HasValue || MathF.Abs(canvasPoint.X - grip.X) <= MathF.Abs(snapped.X - grip.X)))
                        (snapped.X, x) = (canvasPoint.X, canvasX);
                    if (canvasY.HasValue && (!y.HasValue || MathF.Abs(canvasPoint.Y - grip.Y) <= MathF.Abs(snapped.Y - grip.Y)))
                        (snapped.Y, y) = (canvasPoint.Y, canvasY);
                }
                else if ((canvasX.HasValue || canvasY.HasValue) &&
                    ((!x.HasValue && !y.HasValue) || SKPoint.Distance(canvasPoint, grip) <= SKPoint.Distance(snapped, grip)))
                {
                    (snapped, x, y) = (canvasPoint, canvasX, canvasY);
                }
            }
            if (x.HasValue || y.HasValue)
            {
                var target = unrotate.MapPoint(snapped);
                var next = frame;
                if (freeCorner)
                {
                    var delta = target - grab;
                    if (movesLeft) { next.Left += delta.X; if (fromCenter) next.Right -= delta.X; }
                    else { next.Right += delta.X; if (fromCenter) next.Left -= delta.X; }
                    if (movesTop) { next.Top += delta.Y; if (fromCenter) next.Bottom -= delta.Y; }
                    else { next.Bottom += delta.Y; if (fromCenter) next.Top -= delta.Y; }
                }
                else
                {
                    var factor = horizontal ? (target.X - anchor.X) / (grab.X - anchor.X) : (target.Y - anchor.Y) / (grab.Y - anchor.Y);
                    if (!free || horizontal) { next.Left = anchor.X + (left - anchor.X) * factor; next.Right = anchor.X + (right - anchor.X) * factor; }
                    if (!free || vertical) { next.Top = anchor.Y + (top - anchor.Y) * factor; next.Bottom = anchor.Y + (bottom - anchor.Y) * factor; }
                }
                if (Math.Abs(next.Width) >= 1 && Math.Abs(next.Height) >= 1 && next.Width * (right - left) > 0 && next.Height * (bottom - top) > 0)
                {
                    (left, top, right, bottom) = (next.Left, next.Top, next.Right, next.Bottom);
                    guides = (x, y);
                }
            }
        }
        var center = rotate.MapPoint((left + right) / 2, (top + bottom) / 2);
        float width = right - left, height = bottom - top;
        Set(new SKRect(center.X - width / 2, center.Y - height / 2, center.X + width / 2, center.Y + height / 2), StartRotation);
        return guides;
    }

    private (SKPoint Point, float? X, float? Y) SnapResizeToCanvas(SKRect frame, SKPoint anchor, SKPoint grip,
        SKPoint ray, SKMatrix rotation, float tolerance, bool free, bool horizontal, bool vertical)
    {
        var width = session.Document.Width;
        var height = session.Document.Height;
        if (free && horizontal && vertical)
        {
            var x = BoundaryNear(grip.X, width, tolerance);
            var y = BoundaryNear(grip.Y, height, tolerance);
            return (new SKPoint(x ?? grip.X, y ?? grip.Y), x, y);
        }

        // Along a one-dimensional resize, each corner travels in proportion to the handle's movement.
        // Consider the frame's actual outer edges, including the perpendicular edges of a proportional side drag.
        var length = SKPoint.Distance(ray, SKPoint.Empty);
        if (length < 1e-6f) return (grip, null, null);
        var bounds = rotation.MapRect(frame.Standardized);
        var maxChange = tolerance / length;
        var change = 0f;
        float? snapX = null, snapY = null;
        ReadOnlySpan<SKPoint> corners =
        [
            new(frame.Left, frame.Top), new(frame.Right, frame.Top),
            new(frame.Right, frame.Bottom), new(frame.Left, frame.Bottom)
        ];
        foreach (var corner in corners)
        {
            var point = rotation.MapPoint(corner);
            var offset = corner - anchor;
            if (free && !horizontal) offset.X = 0;
            if (free && !vertical) offset.Y = 0;
            var velocity = rotation.MapVector(offset);
            if (MathF.Abs(point.X - bounds.Left) < .001f || MathF.Abs(point.X - bounds.Right) < .001f)
                Consider(point.X, velocity.X, width, ref snapX, ref snapY);
            if (MathF.Abs(point.Y - bounds.Top) < .001f || MathF.Abs(point.Y - bounds.Bottom) < .001f)
                Consider(point.Y, velocity.Y, height, ref snapY, ref snapX);
        }
        return (new SKPoint(grip.X + change * ray.X, grip.Y + change * ray.Y), snapX, snapY);

        void Consider(float position, float velocity, float extent, ref float? line, ref float? other)
        {
            if (MathF.Abs(velocity) < 1e-6f ||
                BoundaryNear(position, extent, maxChange * MathF.Abs(velocity)) is not { } boundary) return;
            var candidate = (boundary - position) / velocity;
            if (1 + candidate <= 0 || MathF.Abs(candidate) > maxChange) return;
            if ((snapX.HasValue || snapY.HasValue) && MathF.Abs(candidate - change) * length < .001f)
            {
                line ??= boundary;
                return;
            }
            change = candidate;
            maxChange = MathF.Abs(candidate);
            line = boundary;
            other = null;
        }
    }

    private static float? BoundaryNear(float position, float extent, float tolerance)
    {
        var boundary = position < extent / 2 ? 0 : extent;
        return MathF.Abs(position - boundary) <= tolerance ? boundary : null;
    }

    public void RotateTo(SKPoint start, SKPoint point, bool snap)
    {
        var center = new SKPoint(StartFrame.MidX, StartFrame.MidY);
        var a0 = Math.Atan2(start.Y - center.Y, start.X - center.X);
        var a1 = Math.Atan2(point.Y - center.Y, point.X - center.X);
        var rotation = StartRotation + (a1 - a0) * 180 / Math.PI;
        if (snap) rotation = Math.Round(rotation / 15) * 15;
        Set(Frame, Normalize(rotation));
    }

    private static double Normalize(double degrees)
    {
        degrees %= 360;
        if (degrees > 180) degrees -= 360;
        if (degrees <= -180) degrees += 360;
        return Math.Round(degrees, 2);
    }

    private void MoveMasks()
    {
        foreach (var (layer, start) in masks)
        {
            var transform = MapControlFrame(start, StartFrame, StartRotation, Frame.Standardized, Rotation, Frame.Width < 0, Frame.Height < 0);
            layer.PlaceMask(transform.Matrix(layer.Mask!.Width, layer.Mask.Height));
        }
    }

    private void SyncMask()
    {
        if (maskTarget != null) maskTarget.MaskTransform = layers[0].Layer.Transform;
        session.UpdateChannelTransformPreview(this);
    }

    private SKRectI Area()
    {
        if (masks.Count > 0) return session.Document.Bounds;
        if (maskTarget != null) return session.AffectedArea(maskTarget);
        if (Layers.Count == 1) return session.AffectedArea(Layers[0]);
        return session.AffectedArea(Layers, MovingLayerIds);
    }

    private void Update()
    {
        MoveMasks();
        if (layers.Count == 0) return;
        // A negative width or height means the frame was dragged through itself: a flip.
        bool flipX = Frame.Width < 0, flipY = Frame.Height < 0;
        var frame = Frame.Standardized;
        if (layers.Count == 1)
        {
            var (layer, start) = layers[0];
            if (hasFrameOverride)
            {
                var original = new LayerTransform { X = StartFrame.Left, Y = StartFrame.Top, Width = StartFrame.Width, Height = StartFrame.Height, Rotation = StartRotation }.Matrix(1, 1);
                var next = new LayerTransform { X = frame.Left, Y = frame.Top, Width = frame.Width, Height = frame.Height, Rotation = Rotation, FlipHorizontal = flipX, FlipVertical = flipY }.Matrix(1, 1);
                if (Frame == StartFrame && Rotation == StartRotation) layer.Transform = start;
                else if (original.TryInvert(out var inverse))
                    layer.Transform = LayerTransform.FromMatrix(start.Matrix(layer.Pixels!.Width, layer.Pixels.Height).PostConcat(inverse.PostConcat(next)), layer.Pixels.Width, layer.Pixels.Height);
                return;
            }
            layer.Transform = MapControlFrame(start, StartFrame, StartRotation, frame, Rotation, flipX, flipY);
            return;
        }
        double sx = frame.Width / Math.Max(1e-6, StartFrame.Width), sy = frame.Height / Math.Max(1e-6, StartFrame.Height);
        var delta = Rotation - StartRotation;
        var spin = SKMatrix.CreateRotationDegrees((float)delta, frame.MidX, frame.MidY);
        foreach (var (layer, start) in layers)
        {
            // Each layer's center follows the group frame; its own box scales and turns with it.
            var c = start.Center;
            var nx = (flipX ? StartFrame.Right - c.X : c.X - StartFrame.Left) * sx + frame.Left;
            var ny = (flipY ? StartFrame.Bottom - c.Y : c.Y - StartFrame.Top) * sy + frame.Top;
            var center = spin.MapPoint((float)nx, (float)ny);
            double w = start.Width * sx, h = start.Height * sy;
            layer.Transform = start with
            {
                X = center.X - w / 2, Y = center.Y - h / 2, Width = w, Height = h, Rotation = Normalize(start.Rotation * (flipX ^ flipY ? -1 : 1) + delta),
                FlipHorizontal = start.FlipHorizontal ^ flipX, FlipVertical = start.FlipVertical ^ flipY,
                Distort = start.Distort == null ? null : ScaleDistort(start.Distort, sx, sy)
            };
        }
    }

    /// <summary>Maps a content frame without changing, cropping or resampling its padded source bitmap or mask.</summary>
    internal static LayerTransform MapControlFrame(LayerTransform start, SKRect original, double originalRotation, SKRect frame, double rotation, bool flipX = false, bool flipY = false)
    {
        if (frame == original && rotation == originalRotation && !flipX && !flipY) return start;
        double sx = frame.Width / Math.Max(1e-6, original.Width), sy = frame.Height / Math.Max(1e-6, original.Height);
        var local = SKMatrix.CreateRotationDegrees((float)-originalRotation, original.MidX, original.MidY).MapPoint(start.Center);
        var x = (flipX ? original.Right - local.X : local.X - original.Left) * sx + frame.Left;
        var y = (flipY ? original.Bottom - local.Y : local.Y - original.Top) * sy + frame.Top;
        var center = SKMatrix.CreateRotationDegrees((float)rotation, frame.MidX, frame.MidY).MapPoint((float)x, (float)y);
        return start with
        {
            X = center.X - start.Width * sx / 2, Y = center.Y - start.Height * sy / 2,
            Width = start.Width * sx, Height = start.Height * sy, Rotation = rotation,
            FlipHorizontal = start.FlipHorizontal ^ flipX, FlipVertical = start.FlipVertical ^ flipY,
            Distort = start.Distort == null ? null : ScaleDistort(start.Distort, sx, sy)
        };
    }

    /// <summary>True when any layer ended up somewhere other than where it started.</summary>
    public bool HasChanges => layers.Any(l => !Same(l.Layer.Transform, l.Start))
        || masks.Any(m => !Same(LayerTransform.FromMatrix(m.Layer.MaskMatrix, m.Layer.Mask!.Width, m.Layer.Mask.Height), m.Start));

    /// <summary>Record equality compares the distort array by reference, so it is compared separately.</summary>
    public static bool Same(LayerTransform a, LayerTransform b) =>
        a with { Distort = null } == b with { Distort = null }
        && (a.Distort ?? []).AsSpan().SequenceEqual(b.Distort ?? []);

    private static float[] ScaleDistort(float[] distort, double sx, double sy) =>
        distort.Select((v, i) => (float)(v * (i % 2 == 0 ? sx : sy))).ToArray();

    /// <summary>Moves one corner freely (Ctrl-drag), distorting a single layer.</summary>
    public void DistortCorner(int corner, SKPoint point)
    {
        if (layers.Count != 1 || session.Transform != this) return;
        var original = DistortionStartCorners();
        var target = (SKPoint[])original.Clone();
        target[corner] = point;
        ApplyDistortion(original, target);
    }

    /// <summary>Moves both corners of an edge by the same document-space offset.</summary>
    public (float? SnapX, float? SnapY) DistortEdge(TransformHandle handle, SKPoint offset, bool skew, bool fromCenter, float snapTolerance)
    {
        if (session.Transform != this) return default;
        var first = handle switch
        {
            TransformHandle.Top => 0, TransformHandle.Right => 1, TransformHandle.Bottom => 2, TransformHandle.Left => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(handle))
        };
        var second = (first + 1) % 4;
        var original = DistortionStartCorners();
        var edge = original[second] - original[first];
        var midpoint = original[first] + new SKPoint(edge.X / 2, edge.Y / 2);
        if (skew)
        {
            var lengthSquared = edge.X * edge.X + edge.Y * edge.Y;
            var along = lengthSquared > 0 ? (offset.X * edge.X + offset.Y * edge.Y) / lengthSquared : 0;
            offset = new SKPoint(along * edge.X, along * edge.Y);
        }
        var (snapped, x, y) = session.SnapLayoutPoint(midpoint + offset, snapTolerance, skew ? edge : null);
        var target = (SKPoint[])original.Clone();
        offset = snapped - midpoint;
        target[first] += offset;
        target[second] += offset;
        if (fromCenter)
        {
            target[(first + 2) % 4] -= offset;
            target[(first + 3) % 4] -= offset;
        }
        ApplyDistortion(original, target);
        return (x, y);
    }

    private SKPoint[] DistortionStartCorners()
    {
        if (hasFrameOverride || layers.Count != 1) return FrameCorners(StartFrame, StartRotation);
        var (layer, start) = layers[0];
        var pixels = layer.Pixels!;
        return DistortionSourceCorners(SourceBounds(layer, pixels), start).Select(start.Matrix(pixels.Width, pixels.Height).MapPoint).ToArray();
    }

    private SKRectI SourceBounds(Layer layer, SKBitmap pixels) =>
        maskTarget != null ? new(0, 0, pixels.Width, pixels.Height) : layer.ControlSourceBounds;

    private static SKPoint[] DistortionSourceCorners(SKRectI source, LayerTransform start)
    {
        // Controls stay in geometric order when the source has been flipped.
        float l = start.FlipHorizontal ? source.Right : source.Left, r = start.FlipHorizontal ? source.Left : source.Right;
        float t = start.FlipVertical ? source.Bottom : source.Top, b = start.FlipVertical ? source.Top : source.Bottom;
        return [new(l, t), new(r, t), new(r, b), new(l, b)];
    }

    private void ApplyDistortion(SKPoint[] original, SKPoint[] target)
    {
        var before = Area();
        var unchanged = original.AsSpan().SequenceEqual(target);
        if (hasFrameOverride || layers.Count != 1 || masks.Count > 0)
        {
            var from = Geometry.RectToQuad(1, 1, original);
            if (!from.TryInvert(out var inverse)) return;
            var delta = inverse.PostConcat(Geometry.RectToQuad(1, 1, target));
            if (hasFrameOverride || layers.Count != 1)
            {
                foreach (var (item, initial) in layers)
                {
                    var pixels = item.Pixels!;
                    item.Transform = unchanged ? initial
                        : LayerTransform.FromMatrix(initial.Matrix(pixels.Width, pixels.Height).PostConcat(delta), pixels.Width, pixels.Height);
                }
                overrideCorners = unchanged ? null : target;
            }
            foreach (var (owner, initial) in masks)
            {
                var mask = owner.Mask!;
                var placement = initial.Matrix(mask.Width, mask.Height);
                owner.PlaceMask(unchanged ? placement : placement.PostConcat(delta));
            }
        }
        if (!hasFrameOverride && layers.Count == 1) ApplyLayerDistortion(original, target, unchanged);
        SyncMask();
        session.Invalidate(Geometry.Union(before, Area()));
    }

    private void ApplyLayerDistortion(SKPoint[] original, SKPoint[] target, bool unchanged)
    {
        var (layer, start) = layers[0];
        if (unchanged) { layer.Transform = start; return; }
        var pixels = layer.Pixels!;
        var source = SourceBounds(layer, pixels);
        var unrotate = SKMatrix.CreateRotationDegrees((float)-start.Rotation, start.Center.X, start.Center.Y);
        if (source == new SKRectI(0, 0, pixels.Width, pixels.Height))
        {
            // Preserve exact offsets of untouched corners when the source already fills its controls.
            var offsets = (float[])(start.Distort ?? new float[8]).Clone();
            for (var i = 0; i < 4; i++)
            {
                if (target[i] == original[i]) continue;
                SetOffset(offsets, i, unrotate.MapPoint(target[i]));
            }
            layer.Transform = start with { Distort = offsets.All(v => Math.Abs(v) < .01f) ? null : offsets };
            return;
        }
        var corners = DistortionSourceCorners(source, start);
        var matrix = SKMatrix.CreateTranslation(-corners[0].X, -corners[0].Y)
            .PostConcat(Geometry.RectToQuad(corners[2].X - corners[0].X, corners[2].Y - corners[0].Y, target));
        var full = DistortionSourceCorners(new SKRectI(0, 0, pixels.Width, pixels.Height), start);
        var distort = new float[8];
        for (var i = 0; i < 4; i++) SetOffset(distort, i, unrotate.MapPoint(matrix.MapPoint(full[i])));
        layer.Transform = start with { Distort = distort.All(v => Math.Abs(v) < 0.01f) ? null : distort };

        void SetOffset(float[] offsets, int i, SKPoint p)
        {
            offsets[i * 2] = p.X - (float)(start.X + (i is 1 or 2 ? start.Width : 0));
            offsets[i * 2 + 1] = p.Y - (float)(start.Y + (i is 2 or 3 ? start.Height : 0));
        }
    }
}

public sealed partial class EditorSession
{
    public TransformEdit? Transform { get; private set; }
    public Layer? IndependentMaskTarget => IsEditingMask && !IsEditingAlphaChannel && !IsEditingSmartFilterMask && ActiveLayer is { MaskLinked: false, Mask: not null } layer && !PositionLocked(layer) ? layer : null;
    public LayerTransform EditingControlTransform(Layer layer) => IsEditingChannel && (layer == ChannelControlLayer || layer == ActiveLayer) ? ChannelControlTransform : IndependentMaskTarget == layer
        ? layer.MaskTransform ?? LayerTransform.FromMatrix(layer.MaskMatrix, layer.Mask!.Width, layer.Mask.Height) : layer.ControlTransform;

    /// <summary>The raster layers a transform applies to: the selection, including everything inside selected folders.</summary>
    public List<Layer> TransformTargets() => IsEditingChannel ? ChannelControlLayer is { } channel ? [channel] : []
        : CollectTransformTargets(null);

    private List<Layer> CollectTransformTargets(List<Layer>? maskOwners)
    {
        var targets = new List<Layer>();
        Collect(document.Layers, parentSelected: false);
        return targets;

        void Collect(List<Layer> siblings, bool parentSelected)
        {
            foreach (var layer in siblings)
            {
                if (layer.Locks.HasFlag(LayerLocks.Position)) continue;
                var selected = parentSelected || document.SelectedLayerIds.Contains(layer.Id);
                if (layer.Children.Count > 0) Collect(layer.Children, selected);
                if (!selected) continue;
                if (layer.Pixels != null) targets.Add(layer);
                else if (layer.Mask != null && layer.MaskLinked) maskOwners?.Add(layer);
            }
        }
    }

    public TransformEdit? BeginTransform(string name = "Transform", bool duplicate = false)
    {
        if (IsEditingChannel) return BeginChannelTransform(name, duplicate);
        var maskOwners = new List<Layer>();
        var targets = CollectTransformTargets(maskOwners);
        var maskTarget = IndependentMaskTarget;
        if (targets.Count == 0 && maskOwners.Count == 0 && maskTarget == null) return null;
        if (maskTarget != null) duplicate = false;
        Begin(duplicate ? "Duplicate and Move" : name);
        if (duplicate)
        {
            InsertLayerCopies(SelectedRoots());
            maskOwners.Clear();
            targets = CollectTransformTargets(maskOwners);
        }
        Transform = new TransformEdit(this, targets, maskTarget == null ? maskOwners : [], maskTarget);
        if (duplicate) { InvalidateAll(); LayersChanged?.Invoke(); }
        return Transform;
    }

    public void CommitTransform()
    {
        if (EndChannelTransform(keep: true)) return;
        if (Transform == null) return;
        var changed = Transform.HasChanges;
        if (!Transform.IsMovingAutoLayout)
        {
            foreach (var layer in Transform.Layers)
            {
                // Live shapes are redrawn at their new size instead of being stretched.
                if (layer.Text != null && layer.Pixels != null) RescaleText(layer);
                if (layer.Shape != null && layer.Pixels != null)
                {
                    int w = Math.Max(1, (int)Math.Round(layer.Transform.Width)), h = Math.Max(1, (int)Math.Round(layer.Transform.Height));
                    if (w != layer.Pixels.Width || h != layer.Pixels.Height) ReplaceLivePixels(layer, RenderWorkingShape(layer.Shape, w, h));
                }
            }
            if (changed && !IsEditingMask && !IsEditingChannel)
                ReflowAfterTransform(Transform.Layers, pendingBefore);
        }
        Transform = null;
        if (changed) Commit(); else Cancel();
        InvalidateAll();
        LayersChanged?.Invoke();
    }

    public void CancelTransform()
    {
        if (EndChannelTransform(keep: false)) return;
        if (Transform == null) return;
        Transform = null;
        Cancel();
    }

    /// <summary>Swaps in regenerated pixels for a live layer; its mask is resampled to stay the same size as the pixels.</summary>
    private static void ReplaceLivePixels(Layer layer, SKBitmap pixels)
    {
        layer.Pixels = pixels;
        // Freshly drawn pixels are only sharp when they sit on the pixel grid at their own size.
        var t = layer.Transform;
        if (t.Rotation == 0 && t.Distort == null && Math.Abs(t.Width - pixels.Width) < 1 && Math.Abs(t.Height - pixels.Height) < 1)
            layer.Transform = t with { X = Math.Round(t.X), Y = Math.Round(t.Y), Width = pixels.Width, Height = pixels.Height };
        if (layer.MaskTransform == null && layer.Mask is { } mask && (mask.Width != pixels.Width || mask.Height != pixels.Height)) layer.Mask = Resample(mask, pixels.Width, pixels.Height);
    }

    /// <summary>Nudges the selected layers with the arrow keys.</summary>
    public void Nudge(int dx, int dy)
    {
        if (BeginTransform("Nudge") is not { } edit) return;
        edit.MoveBy(dx, dy);
        CommitTransform();
    }

    private const string InspectorEditName = "Transform";
    private (Guid LayerId, int Revision)? inspectorEdit;
    private (Guid LayerId, int Revision)? maskInspectorEdit;

    public void SetControlTransform(Layer layer, LayerTransform control)
    {
        if (IsEditingChannel && (layer == ChannelControlLayer || layer == ActiveLayer)) { SetChannelControlTransform(control); return; }
        if (IndependentMaskTarget == layer) { SetMaskTransform(layer, control); return; }
        var before = layer.ControlTransform;
        SetTransform(layer, TransformEdit.MapControlFrame(layer.Transform, SKRect.Create((float)before.X, (float)before.Y, (float)before.Width, (float)before.Height), before.Rotation,
            SKRect.Create((float)control.X, (float)control.Y, (float)control.Width, (float)control.Height), control.Rotation));
    }

    public void SetMaskTransform(Layer layer, LayerTransform transform)
    {
        if (layer.Mask == null || PositionLocked(layer)) return;
        if (!new[] { transform.X, transform.Y, transform.Width, transform.Height, transform.Rotation }.All(double.IsFinite)
            || transform.Width <= 0 || transform.Height <= 0 || transform.Distort is { } distort && (distort.Length != 8 || !distort.All(float.IsFinite)))
            throw new ArgumentException("A mask transform must be finite and have a positive size.", nameof(transform));
        var before = !layer.MaskLinked && layer.MaskTransform != null ? layer.MaskTransform : LayerTransform.FromMatrix(layer.MaskMatrix, layer.Mask.Width, layer.Mask.Height);
        if (TransformEdit.Same(before, transform)) return;
        Apply("Transform Layer Mask", () =>
        {
            if (!layer.MaskLinked) layer.MaskTransform = transform;
            else layer.PlaceMask(transform.Matrix(layer.Mask.Width, layer.Mask.Height));
        });
        if (maskInspectorEdit is { } last && last.LayerId == layer.Id && last.Revision == Revision - 1) FoldLastStep("Transform Layer Mask");
        maskInspectorEdit = (layer.Id, Revision);
        InvalidateAll(); LayersChanged?.Invoke();
    }

    /// <summary>
    /// Sets exact values from the transform inspector. Every keystroke in a field, and every pixel of a drag on its
    /// label, is a change of its own, so a run of them on one layer undoes as one step.
    /// </summary>
    public void SetTransform(Layer layer, LayerTransform transform)
    {
        if (layer.Pixels == null || PositionLocked(layer) || TransformEdit.Same(transform, layer.Transform)) return;
        Apply(InspectorEditName, () =>
        {
            layer.Transform = transform;
            if (layer.Text != null) RescaleText(layer);
            if (layer.Shape != null)
            {
                int width = Math.Max(1, (int)Math.Round(transform.Width)), height = Math.Max(1, (int)Math.Round(transform.Height));
                var pixels = layer.Pixels;
                if (width != pixels.Width || height != pixels.Height) pixels = RenderWorkingShape(layer.Shape, width, height);
                ReplaceLivePixels(layer, pixels);
            }
            ReflowLayoutAncestors(layer);
        });
        // Exactly one revision on: the commit above, with no other edit (or undo) between the two changes.
        if (inspectorEdit is { } last && last.LayerId == layer.Id && last.Revision == Revision - 1) FoldLastStep(InspectorEditName);
        inspectorEdit = (layer.Id, Revision);
        InvalidateAll();
        LayersChanged?.Invoke();
    }
}
