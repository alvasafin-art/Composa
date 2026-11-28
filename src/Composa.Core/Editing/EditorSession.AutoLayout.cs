using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Editing;

public sealed record LayoutElement(Layer Layer, IReadOnlyList<Layer> Companions, SKRect Bounds);
public sealed record AutoLayoutProposal(int Revision, Guid[] Selection, Guid? ExistingGroup, AutoLayout Settings,
    IReadOnlyList<(SKRect Original, SKRect Proposed)> Boxes, bool ChangesPositions);

public sealed partial class EditorSession
{
    public bool CanOfferAutoLayout => !IsEditingMask && !IsEditingChannel &&
        (document.SelectedLayerIds.Count > 1 || ActiveLayer is { IsGroup: true, AutoLayout: null, Children.Count: > 1 });

    public AutoLayoutProposal? ProposeAutoLayout(out string? problem)
    {
        problem = null;
        if (!CanOfferAutoLayout) return null;
        var roots = SelectedRoots();
        var group = roots is [{ IsGroup: true, AutoLayout: null } folder] ? folder : null;
        var siblings = group?.Children ?? document.SiblingsOf(roots[0].Id)!;
        if (group == null && roots.Any(layer => document.SiblingsOf(layer.Id) != siblings))
        {
            problem = "Select layers in the same folder.";
            return null;
        }
        var selected = (group == null ? roots : siblings).ToHashSet();
        var units = LayoutUnits(siblings).Where(unit => selected.Contains(unit.Layer) || unit.Companions.Any(selected.Contains)).ToArray();
        if (units.Any(unit => unit.Layer.Clipped || !selected.Contains(unit.Layer) || unit.Companions.Any(layer => !selected.Contains(layer))))
        {
            problem = "Select each clipping base together with all layers clipped to it.";
            return null;
        }
        if (units.Length < 2 || units.Any(unit => unit.Bounds.IsEmpty))
        {
            problem = "Select at least two elements with measurable bounds.";
            return null;
        }
        if (selected.SelectMany(layer => Document.Flatten([layer])).Any(PositionLocked))
        {
            problem = "Unlock the positions of the selected layers first.";
            return null;
        }

        var vertical = Infer(LayoutDirection.Column);
        var horizontal = Infer(LayoutDirection.Row);
        var best = horizontal.Cost < vertical.Cost ? horizontal : vertical;
        return new(Revision, roots.Select(layer => layer.Id).ToArray(), group?.Id, best.Settings, best.Boxes,
            best.Boxes.Any(box => Math.Max(Math.Abs(box.Original.Left - box.Proposed.Left), Math.Abs(box.Original.Top - box.Proposed.Top)) > 1));

        (AutoLayout Settings, (SKRect Original, SKRect Proposed)[] Boxes, double Cost) Infer(LayoutDirection direction)
        {
            var row = direction == LayoutDirection.Row;
            var ordered = units.OrderBy(unit => MainStart(unit.Bounds, row)).ThenBy(unit => CrossStart(unit.Bounds, row)).ToArray();
            var distances = ordered.Skip(1).Select((unit, i) => MainStart(unit.Bounds, row) - MainEnd(ordered[i].Bounds, row)).Order().ToArray();
            var middle = distances.Length / 2;
            var gap = distances.Length % 2 == 0 ? (distances[middle - 1] + distances[middle]) / 2 : distances[middle];
            var settings = new AutoLayout(units.Min(unit => unit.Bounds.Left), units.Min(unit => unit.Bounds.Top),
                direction, LayoutAlignment.Start, Math.Max(0, gap), new());
            var choices = Enum.GetValues<LayoutAlignment>().Select(alignment =>
            {
                var layout = settings with { Alignment = alignment };
                var boxes = PlaceUnits(ordered, layout).Select(pair => (Original: pair.Unit.Bounds, Proposed: pair.Target)).ToArray();
                var cost = boxes.Sum(pair => Math.Abs(pair.Original.Left - pair.Proposed.Left) + Math.Abs(pair.Original.Top - pair.Proposed.Top));
                return (Settings: layout, Boxes: boxes, Cost: (double)cost);
            });
            return choices.MinBy(choice => choice.Cost);
        }
    }

    public bool CreateAutoLayout(AutoLayoutProposal proposal, string name = "Auto Layout")
    {
        FinishInteraction();
        if (proposal.Revision != Revision || !SelectedRoots().Select(layer => layer.Id).SequenceEqual(proposal.Selection)) return false;
        var roots = SelectedRoots();
        var group = proposal.ExistingGroup is { } id ? document.Find(id) : Layer.Group(document.UniqueName(name));
        if (group == null) return false;
        Apply("Auto Layout", () =>
        {
            if (proposal.ExistingGroup == null)
            {
                var siblings = document.SiblingsOf(roots[^1].Id)!;
                siblings.Insert(siblings.IndexOf(roots[^1]) + 1, group);
                foreach (var root in roots)
                {
                    siblings.Remove(root);
                    group.Children.Add(root);
                }
            }

            var row = proposal.Settings.Direction == LayoutDirection.Row;
            var ordered = LayoutUnits(group.Children).OrderBy(unit => MainStart(unit.Bounds, row)).ThenBy(unit => CrossStart(unit.Bounds, row)).ToArray();
            group.Children.Clear();
            foreach (var unit in ordered.Reverse())
            {
                group.Children.Add(unit.Layer);
                group.Children.AddRange(unit.Companions);
            }
            group.AutoLayout = proposal.Settings;
            document.SetActive(group.Id);
            ReflowAutoLayout(group);
            ReflowLayoutAncestors(group);
        });
        InvalidateAll();
        LayersChanged?.Invoke();
        return true;
    }

    public bool CanEditAutoLayout(Layer group) => group.AutoLayout != null
        && !PositionLocked(group) && !Document.Flatten(group.Children).Any(layer => layer.Locks.HasFlag(LayerLocks.Position));

    public void DisableAutoLayout(Layer group)
    {
        if (!CanEditAutoLayout(group)) return;
        Apply("Disable Auto Layout", () =>
        {
            group.AutoLayout = null;
            foreach (var child in group.Children) child.AutoLayoutItem = null;
        });
        LayersChanged?.Invoke();
    }

    public void SetAutoLayout(Layer group, AutoLayout settings)
    {
        settings = settings.Normalized();
        if (!CanEditAutoLayout(group) || group.AutoLayout == settings) return;
        Apply("Change Auto Layout", () => SetAutoLayoutCore(group, settings));
        LayersChanged?.Invoke();
    }

    public bool BeginLayoutGap(Layer group)
    {
        if (HasPendingEdit || !CanEditAutoLayout(group)) return false;
        Begin("Change Layout Gap");
        return true;
    }

    public void PreviewLayoutGap(Layer group, double gap)
    {
        if (pendingBefore == null || pendingName != "Change Layout Gap" || group.AutoLayout is not { } settings) return;
        gap = LayoutInsets.Size(gap);
        if (settings.Gap != gap) SetAutoLayoutCore(group, settings with { Gap = gap });
    }

    public void FinishLayoutGap(Layer group, bool keep)
    {
        if (pendingBefore == null || pendingName != "Change Layout Gap") return;
        if (keep && group.AutoLayout != pendingBefore.Find(group.Id)?.AutoLayout) Commit(); else Cancel();
        LayersChanged?.Invoke();
    }

    private void SetAutoLayoutCore(Layer group, AutoLayout settings)
    {
        if (group.AutoLayout?.Direction != settings.Direction)
        {
            // A former cross-axis fill cannot become a dependency on its own parent's main size.
            foreach (var child in group.Children)
                if (child.AutoLayoutItem is { } item)
                    child.AutoLayoutItem = item with
                    {
                        WidthMode = settings.Direction == LayoutDirection.Row && item.WidthMode == LayoutSizeMode.Fill ? LayoutSizeMode.Content : item.WidthMode,
                        HeightMode = settings.Direction == LayoutDirection.Column && item.HeightMode == LayoutSizeMode.Fill ? LayoutSizeMode.Content : item.HeightMode
                    };
            settings = settings with { FillReference = 0 };
        }
        group.AutoLayout = settings;
        ReflowAutoLayout(group);
        ReflowLayoutAncestors(group);
    }

    public Layer? AutoLayoutParent(Layer layer) => document.ParentOf(layer.Id) is { AutoLayout: not null } parent ? parent : null;

    public AutoLayoutItem LayoutItemSettings(Layer layer)
    {
        var bounds = LayoutBounds(layer);
        return layer.AutoLayoutItem ?? new(LayoutSizeMode.Content, LayoutSizeMode.Content, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
    }

    public bool CanSetLayoutSize(Layer layer) => layer.Transform.Rotation == 0 && layer.Transform.Distort == null
        && (layer.Text != null || layer.Shape != null || layer.SupportsLayoutImageFrame)
        && (layer.Text == null && layer.Shape == null || !PixelsLocked(layer));

    public bool CanReplaceLayoutImage(Layer layer) => layer.SupportsLayoutImageFrame && layer.SmartObject == null
        && layer.FilterSource == null && AutoLayoutParent(layer) != null && !PixelsLocked(layer) && !PositionLocked(layer);

    /// <summary>Replaces the image source, adopting the supplied bitmap on success.</summary>
    public bool ReplaceLayoutImage(Layer layer, SKBitmap image)
    {
        if (!CanReplaceLayoutImage(layer) || !DocumentLimits.FitsSurface(image.Width, image.Height, image.BytesPerPixel)) return false;
        var converted = (image.ColorType != SKColorType.Rgba8888 && image.ColorType != SKColorType.RgbaF32)
            || !Pixels.ProfileOf(image).Equals(document.Profile);
        var pixels = converted ? Pixels.ConvertWorking(image, Pixels.DepthOf(image), document.Profile) : image;
        var candidate = document.Clone();
        candidate.Find(layer.Id)!.Pixels = pixels;
        if (candidate.RasterPixels() > DocumentLimits.DocumentPixelBudget)
        {
            if (converted) pixels.Dispose();
            throw new InvalidOperationException("The imported layer exceeds the document raster memory budget.");
        }
        Apply("Replace Layout Image", () =>
        {
            var mask = layer.MaskMatrix;
            var frame = layer.LayoutFrame;
            layer.Pixels = pixels;
            if (frame == null)
                layer.Transform = layer.Transform with { Height = layer.Transform.Width / PhotoAspect(layer) };
            if (layer.Mask != null) layer.PlaceMask(mask);
            ReflowLayoutAncestors(layer);
        });
        if (converted) image.Dispose();
        InvalidateAll();
        LayersChanged?.Invoke();
        return true;
    }

    public bool SetAutoLayoutItem(Layer layer, AutoLayoutItem settings)
    {
        if (!CanSetLayoutSize(layer) || layer.Clipped || AutoLayoutParent(layer) is not { AutoLayout: { } layout } parent || !CanEditAutoLayout(parent)) return false;
        settings = settings.Normalized();
        if ((layout.Direction == LayoutDirection.Row ? settings.WidthMode : settings.HeightMode) == LayoutSizeMode.Fill) return false;
        if (settings == LayoutItemSettings(layer)) return false;
        Apply("Change Layout Size", () =>
        {
            var cross = LayoutElements(parent).Select(unit => CrossSize(unit.Bounds, layout.Direction == LayoutDirection.Row)).DefaultIfEmpty(1).Max();
            parent.AutoLayout = layout with { FillReference = cross };
            layer.AutoLayoutItem = settings;
            ReflowAutoLayout(parent);
            ReflowLayoutAncestors(parent);
        });
        LayersChanged?.Invoke();
        return true;
    }

    /// <summary>Changes the order of complete clipping units. Index is in visual order, from top/left to bottom/right.</summary>
    public bool ReorderAutoLayout(Layer group, Guid id, int index)
    {
        if (!CanEditAutoLayout(group)) return false;
        var units = LayoutElements(group).ToList();
        var old = units.FindIndex(unit => unit.Layer.Id == id);
        if (old < 0 || index < 0 || index >= units.Count || old == index) return false;
        Apply("Reorder Layout", () =>
        {
            var moved = units[old];
            units.RemoveAt(old);
            units.Insert(index, moved);
            // Geometry-less layers can arise after editing a group. Keep their stack positions.
            var orderedLayers = units.AsEnumerable().Reverse().SelectMany(unit => new[] { unit.Layer }.Concat(unit.Companions)).ToArray();
            var included = orderedLayers.ToHashSet();
            for (int i = 0, next = 0; i < group.Children.Count; i++)
                if (included.Contains(group.Children[i])) group.Children[i] = orderedLayers[next++];
            ReflowAutoLayout(group);
            ReflowLayoutAncestors(group);
        });
        LayersChanged?.Invoke();
        return true;
    }

    public IReadOnlyList<LayoutElement> LayoutElements(Layer group) =>
        LayoutUnits(group.Children).Where(unit => !unit.Bounds.IsEmpty).Reverse().ToArray();

    public static SKRect LayoutBounds(Layer layer)
    {
        if (layer.LayoutFrame is { } frame) return frame.Bounds;
        if (!layer.IsGroup) return layer.Text != null && !layer.HasWarp ? layer.Bounds : layer.ControlBounds;
        var bounds = SKRect.Empty;
        foreach (var unit in LayoutUnits(layer.Children))
            if (!unit.Bounds.IsEmpty) bounds = UnionLayout(bounds, unit.Bounds);
        if (bounds.IsEmpty || layer.AutoLayout is not { } layout) return bounds;
        var padding = layout.Padding;
        return new((float)layout.X, (float)layout.Y, (float)(bounds.Right + padding.Right), (float)(bounds.Bottom + padding.Bottom));
    }

    private static SKRect UnionLayout(SKRect a, SKRect b) => a.IsEmpty ? b : b.IsEmpty ? a
        : new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

    private static IEnumerable<LayoutElement> LayoutUnits(List<Layer> layers)
    {
        for (int i = 0; i < layers.Count; i++)
        {
            var root = layers[i];
            var start = i + 1;
            while (i + 1 < layers.Count && layers[i + 1].Clipped) i++;
            yield return new(root, i < start ? [] : layers.GetRange(start, i - start + 1), LayoutBounds(root));
        }
    }

    private void ReflowLayoutAncestors(Layer child)
    {
        ReflowLayoutParents(document.ParentOf(child.Id));
    }

    private void ReflowLayoutParents(Layer? parent)
    {
        for (var group = parent; group != null; group = document.ParentOf(group.Id))
            if (group.AutoLayout != null) ReflowAutoLayout(group);
    }

    private void InsertAndReflow(Layer layer)
    {
        document.InsertAboveActive(layer);
        ReflowLayoutAncestors(layer);
    }

    internal Layer[] LayoutMovePlacementTargets()
    {
        if (document.SelectedLayerIds.Count == 1 && ActiveLayer?.IsGroup != true) return [];
        var roots = SelectedRoots();
        if (!roots.Any(layer => layer.IsGroup)) return [];
        foreach (var root in roots)
            for (var parent = document.ParentOf(root.Id); parent != null; parent = document.ParentOf(parent.Id))
                if (parent.AutoLayout != null) return [];
        var layers = Document.Flatten(roots).ToArray();
        if (!layers.Any(layer => layer.AutoLayout != null) || layers.Any(layer => layer.Locks.HasFlag(LayerLocks.Position))
            || roots.Any(PositionLocked)) return [];
        return layers.Where(layer => layer.AutoLayout != null || layer.LayoutFrame != null || layer.Mask != null && !layer.MaskLinked).ToArray();
    }

    private void ReflowAfterTransform(IReadOnlyList<Layer> transformed, Document? before)
    {
        if (before == null) return;
        var roots = SelectedRoots();
        foreach (var layer in transformed)
        {
            if (layer.LayoutFrame == null || before.Find(layer.Id) is not { LayoutFrame: { } frame } original) continue;
            var old = original.Transform;
            var next = layer.Transform;
            var sx = next.Width / old.Width;
            var sy = next.Height / old.Height;
            layer.LayoutFrame = new(next.X + (frame.X - old.X) * sx, next.Y + (frame.Y - old.Y) * sy, frame.Width * sx, frame.Height * sy);
            if (layer.AutoLayoutItem is { } item)
                layer.AutoLayoutItem = item with { Width = layer.LayoutFrame.Width, Height = layer.LayoutFrame.Height };
        }
        foreach (var root in roots)
        {
            foreach (var group in Document.Flatten([root]).Where(layer => layer.AutoLayout != null).Reverse())
            {
                if (before.Find(group.Id) is not { } original) continue;
                var from = LayoutElements(original).FirstOrDefault()?.Bounds;
                var to = LayoutElements(group).FirstOrDefault()?.Bounds;
                if (from != null && to != null) group.TranslateLayout(to.Value.Left - from.Value.Left, to.Value.Top - from.Value.Top);
                ReflowAutoLayout(group);
            }
            ReflowLayoutAncestors(root);
        }
    }

    private void ReflowAutoLayout(Layer group)
    {
        if (group.AutoLayout is not { } layout || !CanEditAutoLayout(group)) return;
        var elements = LayoutElements(group);
        if (elements.Count == 0) return;
        var row = layout.Direction == LayoutDirection.Row;
        var initialDamage = LayoutDamage(elements);
        var crossReference = elements.Where(unit => !FillsCross(unit.Layer, row)).Select(unit => RequestedCrossSize(unit, row)).DefaultIfEmpty((float)layout.FillReference).Max();
        foreach (var unit in elements)
            if (unit.Layer.AutoLayoutItem is { } item) ApplyLayoutSize(unit.Layer, item, row, Math.Max(1, crossReference));

        var sized = elements.Select(unit => unit with { Bounds = LayoutBounds(unit.Layer) }).ToArray();
        foreach (var (unit, target) in PlaceUnits(sized, layout))
        {
            var dx = target.Left - unit.Bounds.Left;
            var dy = target.Top - unit.Bounds.Top;
            if (Math.Abs(dx) < .001 && Math.Abs(dy) < .001) continue;
            foreach (var node in Document.Flatten([unit.Layer, .. unit.Companions])) node.TranslateContent(dx, dy);
        }
        var damage = Geometry.Union(initialDamage, LayoutDamage(elements));
        if (damage.IsEmpty) return;
        var margin = DamageMargin;
        damage.Inflate(margin, margin);
        Invalidate(damage);
    }

    private SKRectI LayoutDamage(IReadOnlyList<LayoutElement> elements)
    {
        var damage = SKRectI.Empty;
        foreach (var unit in elements)
            foreach (var node in Document.Flatten([unit.Layer, .. unit.Companions]))
            {
                if (node.IsAdjustment) return document.Bounds;
                damage = Geometry.Union(damage, Geometry.RoundOut(node.VisibleBounds));
            }
        return damage;
    }

    private static bool FillsCross(Layer layer, bool row) =>
        (row ? layer.AutoLayoutItem?.HeightMode : layer.AutoLayoutItem?.WidthMode) == LayoutSizeMode.Fill;

    private static float RequestedCrossSize(LayoutElement unit, bool row)
    {
        var item = unit.Layer.AutoLayoutItem;
        if (item != null && (row ? item.HeightMode : item.WidthMode) == LayoutSizeMode.Fixed)
            return (float)(row ? item.Height : item.Width);
        return CrossSize(unit.Bounds, row);
    }

    private void ApplyLayoutSize(Layer layer, AutoLayoutItem item, bool row, double crossReference)
    {
        if (layer.Pixels is not { } pixels) return;
        var bounds = LayoutBounds(layer);
        var width = item.WidthMode == LayoutSizeMode.Fixed ? item.Width : item.WidthMode == LayoutSizeMode.Fill && !row ? crossReference : (double?)null;
        var height = item.HeightMode == LayoutSizeMode.Fixed ? item.Height : item.HeightMode == LayoutSizeMode.Fill && row ? crossReference : (double?)null;
        if (layer.Text is { } text && !PixelsLocked(layer))
        {
            var scaleX = layer.Transform.Width / pixels.Width;
            var scaleY = layer.Transform.Height / pixels.Height;
            var boxWidth = width / scaleX ?? (height != null ? (text.BoxWidth ?? pixels.Width) : (double?)null);
            var style = (text with { BoxWidth = boxWidth, BoxHeight = boxWidth != null ? height / scaleY ?? TextStyle.MinBox : null,
                AutoBoxHeight = boxWidth != null && height == null }).Clamped();
            if (style != text) SetTextCore(layer, style);
        }
        else if (layer.Shape is { } shape && !PixelsLocked(layer) && layer.Transform.Rotation == 0 && layer.Transform.Distort == null)
        {
            var w = (int)Math.Round(width ?? bounds.Width);
            var h = (int)Math.Round(height ?? bounds.Height);
            if ((w != pixels.Width || h != pixels.Height) && DocumentLimits.FitsSurface(w, h, pixels.BytesPerPixel))
            {
                layer.Transform = layer.Transform with { Width = w, Height = h };
                ReplaceLivePixels(layer, RenderWorkingShape(shape, w, h));
            }
        }
        else if (layer.SupportsLayoutImageFrame)
        {
            var transform = layer.Transform;
            if (width == null && height == null)
            {
                if (layer.LayoutFrame is { } former)
                    layer.Transform = transform with { X = former.X, Y = former.Y, Width = former.Width, Height = former.Width / PhotoAspect(layer) };
                layer.LayoutFrame = null;
                return;
            }
            var aspect = PhotoAspect(layer);
            var w = width ?? height!.Value * aspect;
            var h = height ?? width!.Value / aspect;
            w = Math.Clamp(w, 1, DocumentLimits.MaxSide);
            h = Math.Clamp(h, 1, DocumentLimits.MaxSide);
            var imageWidth = item.ImageFit == LayoutImageFit.Crop ? Math.Max(w, h * aspect) : Math.Min(w, h * aspect);
            var imageHeight = imageWidth / aspect;
            layer.LayoutFrame = new(bounds.Left, bounds.Top, w, h);
            layer.Transform = transform with { X = bounds.Left + (w - imageWidth) / 2, Y = bounds.Top + (h - imageHeight) / 2,
                Width = imageWidth, Height = imageHeight };
        }
    }

    private static double PhotoAspect(Layer layer) => layer.SmartObject is { } source
        ? (double)source.Width / source.Height : (double)layer.Pixels!.Width / layer.Pixels.Height;

    private static IEnumerable<(LayoutElement Unit, SKRect Target)> PlaceUnits(IReadOnlyList<LayoutElement> units, AutoLayout layout)
    {
        if (units.Count == 0) yield break;
        var row = layout.Direction == LayoutDirection.Row;
        var padding = layout.Padding;
        var main = row ? layout.X + padding.Left : layout.Y + padding.Top;
        var cross = row ? layout.Y + padding.Top : layout.X + padding.Left;
        var max = units.Max(unit => CrossSize(unit.Bounds, row));
        foreach (var unit in units)
        {
            var remaining = max - CrossSize(unit.Bounds, row);
            var offset = layout.Alignment switch { LayoutAlignment.Center => remaining / 2, LayoutAlignment.End => remaining, _ => 0 };
            var bounds = unit.Bounds;
            yield return (unit, SKRect.Create((float)(row ? main : cross + offset), (float)(row ? cross + offset : main), bounds.Width, bounds.Height));
            main += (row ? bounds.Width : bounds.Height) + layout.Gap;
        }
    }

    private static float MainStart(SKRect bounds, bool row) => row ? bounds.Left : bounds.Top;
    private static float MainEnd(SKRect bounds, bool row) => row ? bounds.Right : bounds.Bottom;
    private static float CrossStart(SKRect bounds, bool row) => row ? bounds.Top : bounds.Left;
    private static float CrossSize(SKRect bounds, bool row) => row ? bounds.Height : bounds.Width;
}
