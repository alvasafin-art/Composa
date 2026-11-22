using Composa.Model;
using SkiaSharp;

namespace Composa.Editing;

public sealed partial class EditorSession
{
    public static bool SupportsStyle(Layer layer, LayerStyleKind kind) => kind == LayerStyleKind.Text
        ? layer.Text != null : !layer.IsAdjustment && (layer.Pixels != null || layer.IsGroup);

    public LayerStyleDefinition? LinkedStyle(Layer layer, LayerStyleKind kind) =>
        layer.StyleBinding(kind) is { } binding && document.Styles.TryGetValue(binding.Id, out var style)
        && style.Kind == kind && SupportsStyle(layer, kind) ? style : null;

    public int StyleLinkCount(LayerStyleDefinition style) => document.AllLayers().Count(layer => IsStyleLink(layer, style));
    private static bool IsStyleLink(Layer layer, LayerStyleDefinition style) =>
        SupportsStyle(layer, style.Kind) && layer.StyleBinding(style.Kind)?.Id == style.Id;

    public StyleFields StyleOverrides(Layer layer, LayerStyleKind kind)
    {
        if (LinkedStyle(layer, kind) is not { } style) return StyleFields.None;
        var differences = style.Text is { } text ? text.Differences(layer.Text!)
            : style.Appearance!.Differences(AppearanceStyleDefinition.From(layer));
        return (differences | layer.StyleBinding(kind)!.Overrides) & ApplicableFields(layer, style);
    }

    private static StyleFields ApplicableFields(Layer layer, LayerStyleDefinition style) => style.Kind == LayerStyleKind.Text
        ? StyleFields.Text : style.Fields & AppearanceStyleDefinition.From(layer).Fields;

    public bool IsStyleNameAvailable(LayerStyleKind kind, string? name, Guid? editing = null) =>
        LayerStyleDefinition.NormalizeName(name) is { } normalized && !document.Styles.Values.Any(style =>
            style.Id != editing && style.Kind == kind && string.Equals(style.Name, normalized, StringComparison.OrdinalIgnoreCase));

    public bool CanEditLayerStyle(Layer layer, LayerStyleKind kind) => SupportsStyle(layer, kind) &&
        (kind == LayerStyleKind.Text ? !PixelsLocked(layer) : !IsLocked(layer) && (layer.Shape == null || !PixelsLocked(layer)));

    public bool CanUpdateLayerStyle(LayerStyleDefinition style) =>
        document.AllLayers().Where(layer => IsStyleLink(layer, style)).All(layer => CanEditLayerStyle(layer, style.Kind));

    public LayerStyleDefinition? CreateLayerStyle(Layer layer, LayerStyleKind kind, string name)
    {
        FinishInteraction();
        if (document.Find(layer.Id) is not { } live || !CanEditLayerStyle(live, kind)
            || !IsStyleNameAvailable(kind, name) || document.Styles.Count >= DocumentLimits.MaxLayerStyles) return null;
        layer = live;
        var definition = kind == LayerStyleKind.Text
            ? new LayerStyleDefinition(Guid.NewGuid(), name, Text: TextStyleDefinition.From(layer.Text!))
            : new LayerStyleDefinition(Guid.NewGuid(), name, Appearance: AppearanceStyleDefinition.From(layer));
        var style = definition.Normalized()!;
        Apply("Save Layer Style", () =>
        {
            document.Styles.Add(style.Id, style);
            layer.SetStyleBinding(kind, new(style.Id));
        });
        LayersChanged?.Invoke();
        return style;
    }

    public bool ApplyLayerStyle(Layer layer, Guid id)
    {
        FinishInteraction();
        if (document.Find(layer.Id) is not { } live || !document.Styles.TryGetValue(id, out var style)
            || !CanEditLayerStyle(live, style.Kind) || LinkedStyle(live, style.Kind)?.Id == id) return false;
        layer = live;
        Apply("Apply Layer Style", () =>
        {
            layer.SetStyleBinding(style.Kind, new(style.Id));
            if (ApplyStyleFields(layer, style, ApplicableFields(layer, style))) ReflowLayoutAncestors(layer);
        });
        LayersChanged?.Invoke();
        return true;
    }

    public bool UnlinkLayerStyle(Layer layer, LayerStyleKind kind)
    {
        FinishInteraction();
        if (document.Find(layer.Id) is not { } live || LinkedStyle(live, kind) == null || !CanEditLayerStyle(live, kind)) return false;
        layer = live;
        Apply("Unlink Layer Style", () => layer.SetStyleBinding(kind, null));
        LayersChanged?.Invoke();
        return true;
    }

    public bool ResetStyleFields(Layer layer, LayerStyleKind kind, StyleFields fields)
    {
        FinishInteraction();
        if (document.Find(layer.Id) is not { } live || LinkedStyle(live, kind) is not { } style || !CanEditLayerStyle(live, kind)) return false;
        layer = live;
        var overrides = StyleOverrides(layer, kind);
        fields &= overrides;
        if (fields == StyleFields.None) return false;
        Apply("Reset Style Override", () =>
        {
            layer.SetStyleBinding(kind, new(style.Id, overrides & ~fields));
            if (ApplyStyleFields(layer, style, fields)) ReflowLayoutAncestors(layer);
        });
        LayersChanged?.Invoke();
        return true;
    }

    public bool UpdateLayerStyle(LayerStyleDefinition definition)
    {
        FinishInteraction();
        if (definition.Normalized() is not { } next || !document.Styles.TryGetValue(next.Id, out var old)
            || old.Kind != next.Kind || old.Fields != next.Fields || next == old
            || !IsStyleNameAvailable(next.Kind, next.Name, next.Id)) return false;
        var links = document.AllLayers().Where(layer => IsStyleLink(layer, old)).ToArray();
        if (links.Any(layer => !CanEditLayerStyle(layer, next.Kind)))
        {
            Problem?.Invoke("Unlock the linked layers before editing their style.");
            return false;
        }
        Apply("Edit Shared Style", () =>
        {
            var changes = new Dictionary<Layer, StyleFields>();
            foreach (var layer in links) changes.Add(layer, StyleOverrides(layer, next.Kind));
            document.Styles[next.Id] = next;
            if (next.Text == old.Text && next.Appearance == old.Appearance) return;
            var groups = new HashSet<Layer>();
            foreach (var (layer, overrides) in changes)
            {
                layer.SetStyleBinding(next.Kind, new(next.Id, overrides));
                if (!ApplyStyleFields(layer, next, ApplicableFields(layer, next) & ~overrides)) continue;
                for (var group = document.ParentOf(layer.Id); group != null; group = document.ParentOf(group.Id))
                    if (group.AutoLayout != null) groups.Add(group);
            }
            // Descendants are visited first, so enclosing groups see the final size of their nested groups.
            if (groups.Count > 0)
                foreach (var layer in document.AllLayers())
                    if (groups.Contains(layer)) ReflowAutoLayout(layer);
        });
        LayersChanged?.Invoke();
        return true;
    }

    /// <returns>Whether the layer's geometry changed and its enclosing layout needs to reflow.</returns>
    private bool ApplyStyleFields(Layer layer, LayerStyleDefinition definition, StyleFields fields)
    {
        if (fields == StyleFields.None) return false;
        if (definition.Text is { } text && layer.Text is { } current)
        {
            var style = text.Apply(current, fields);
            if (style == current) return false;
            var before = layer.ControlBounds;
            SetTextCore(layer, style, preservePlacement: true);
            return before != layer.ControlBounds;
        }
        if (definition.Appearance is not { } appearance) return false;
        if (fields.HasFlag(StyleFields.Opacity) && layer.Opacity != appearance.Opacity) SetOpacity(layer, appearance.Opacity);
        if ((fields & StyleFields.Shape) != 0 && appearance.Paint is { } paint && layer.Shape is { } original && layer.Pixels is { } pixels)
        {
            var shape = paint.Apply(original, fields);
            if (shape != original)
            {
                var area = AffectedArea(layer);
                layer.Pixels = RenderWorkingShape(shape, pixels.Width, pixels.Height);
                layer.Shape = shape;
                Invalidate(Geometry.Union(area, AffectedArea(layer)));
            }
        }
        if ((fields & StyleFields.Effects) != 0)
        {
            var old = layer.Effects ?? LayerEffects.Empty;
            var effects = appearance.ApplyEffects(old, fields);
            if (effects != old) SetEffects(layer, effects);
        }
        return false;
    }
}
