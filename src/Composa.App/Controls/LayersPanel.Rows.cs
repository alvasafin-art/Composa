using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media.Imaging;
using Composa.ColorManagement;
using Composa.IO;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Controls;

public sealed partial class LayersPanel
{
    // The collection contains filtered display order, including off-screen items. Only realized
    // entries own controls. Undo restores new Layer objects with the same document IDs.
    private sealed class Entry(Layer layer)
    {
        internal Layer Layer = layer;
        internal int Depth;
        internal bool ParentVisible;
        internal long Seen;
        internal bool InList;
        internal RowHost? Host;
    }

    private sealed class RowHost : Decorator
    {
        internal Border? Main;
        internal Appearance? Shown;
        internal FilterPreview? Preview;
    }

    private sealed record FilterPreview(object Pixels, object Source, object Mask, int PadX, int PadY,
        double X, double Y, byte Outside, SKMatrix MaskMatrix, SKMatrix Frame, RgbProfile Profile, Bitmap Image);

    private readonly record struct Appearance(Composa.Editing.EditorSession Session, Layer? CommandOwner, LayerKind Kind, string Name, string? Tags, LayerLocks Locks, int Depth,
        bool ParentVisible, bool Visible, bool Collapsed, bool FilteredGroup, bool Clipped, bool Live, bool VectorMask,
        bool Selected, bool ActivePixels, bool ActiveMask, bool ActiveFilterMask, bool Renaming,
        Bitmap? Pixels, Bitmap? Mask, bool MaskEnabled, bool MaskLinked, bool SmartObject, SmartObjectLink? Link,
        LinkedSourceStatus? LinkStatus, LayerEffects? Effects, LayerEffectKind? SelectedEffect, SmartFilter[] Filters,
        Bitmap? FilterMask, bool FilterMaskEnabled);

    private readonly AvaloniaList<Entry> entries = [];
    private readonly Dictionary<Guid, Entry> entriesById = [];
    private readonly List<Entry> desired = [];
    private long listVersion;

    private void InitializeRows()
    {
        rows.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel { CacheLength = 0.5 });
        rows.ItemTemplate = new FuncDataTemplate<Entry>((item, _) =>
        {
            if (item is not {} entry) return null;
            var host = new RowHost();
            host.AttachedToVisualTree += (_, _) =>
            {
                entry.Host = host;
                RefreshHost(entry, host);
                if (host.Main != null) rowFor[entry.Layer.Id] = host.Main;
            };
            host.DetachedFromVisualTree += (_, _) =>
            {
                if (entry.Host != host) return;
                entry.Host = null;
                if (rowFor.GetValueOrDefault(entry.Layer.Id) == host.Main) rowFor.Remove(entry.Layer.Id);
            };
            return host;
        });
        rows.ItemsSource = entries;
    }

    private void RefreshRows()
    {
        PrepareFilter();
        desired.Clear();
        listVersion++;
        if (session != null) Collect(session.Document.Layers, 0, true);

        // Remove contiguous hidden/deleted ranges before moving survivors. A plain edit or
        // selection change leaves the items collection and scroll position alone.
        if (entries.Count != desired.Count || !entries.SequenceEqual(desired))
        {
            for (var end = entries.Count; end > 0;)
            {
                var start = end;
                while (start > 0 && entries[start - 1].Seen != listVersion)
                    entriesById.Remove(entries[--start].Layer.Id);
                if (start < end) entries.RemoveRange(start, end - start);
                end = start - 1;
            }
            for (var i = 0; i < desired.Count; i++)
            {
                if (i < entries.Count && entries[i] == desired[i]) continue;
                if (!desired[i].InList)
                {
                    var end = i + 1;
                    while (end < desired.Count && !desired[end].InList) end++;
                    entries.InsertRange(i, desired.GetRange(i, end - i));
                    for (var added = i; added < end; added++) desired[added].InList = true;
                    i = end - 1;
                }
                else
                {
                    var from = entries.IndexOf(desired[i]);
                    var count = 1;
                    while (from + count < entries.Count && i + count < desired.Count && entries[from + count] == desired[i + count]) count++;
                    entries.MoveRange(from, count, i);
                    i += count - 1;
                }
            }
        }
        foreach (var entry in desired)
            if (entry.Host is { } host) RefreshHost(entry, host);
    }

    private void Collect(List<Layer> layers, int depth, bool parentVisible)
    {
        for (var i = layers.Count - 1; i >= 0; i--)
        {
            var layer = layers[i];
            if (filterRows != null && !filterRows.Contains(layer.Id)) continue;
            if (!entriesById.TryGetValue(layer.Id, out var entry)) entriesById.Add(layer.Id, entry = new(layer));
            entry.Layer = layer;
            entry.Depth = depth;
            entry.ParentVisible = parentVisible;
            entry.Seen = listVersion;
            desired.Add(entry);
            if (layer.IsGroup && (!layer.Collapsed || IsFiltering))
                Collect(layer.Children, depth + 1, parentVisible && layer.Visible);
        }
    }

    private void RefreshHost(Entry entry, RowHost host)
    {
        if (session == null) return;
        var layer = entry.Layer;
        var active = layer.Id == session.Document.ActiveLayerId;
        var selected = session.Document.SelectedLayerIds.Contains(layer.Id);
        var maskTarget = active && session.IsEditingMask;
        var filterTarget = active && session.IsEditingSmartFilterMask;
        var preview = PreviewImage(layer, host);
        var tags = layer.Tags.Count == 0 ? null : string.Join(", ", layer.Tags.Order(StringComparer.OrdinalIgnoreCase));
        // The base row resolves commands through its stable entry. Effect/filter children
        // and an open rename editor capture the layer they were created for.
        var owner = layer.Effects is { IsEmpty: false } || layer.SmartFilters.Length > 0 || renaming == layer.Id ? layer : null;
        var state = new Appearance(session, owner, layer.Kind, layer.Name, tags, layer.Locks, entry.Depth, entry.ParentVisible, layer.Visible,
            layer.Collapsed, layer.IsGroup && IsFiltering, layer.Clipped, layer.IsLive, layer.VectorMask != null, selected,
            layer.IsAdjustment ? selected && !session.IsEditingMask : active && !maskTarget, maskTarget && !filterTarget,
            filterTarget, renaming == layer.Id, preview, layer.Mask is {} mask ? Thumbnail(mask) : null, layer.MaskEnabled,
            layer.MaskLinked, layer.IsSmartObject, layer.SmartObject?.Link, layer.SmartObject?.Link != null ? LinkedSourceStatus?.Invoke(layer) : null,
            layer.Effects, session.SelectedEffect is {} effect && effect.LayerId == layer.Id ? effect.Kind : null,
            layer.SmartFilters, layer.SmartFilterMask is {} filterMask ? Thumbnail(filterMask) : null, layer.SmartFilterMaskEnabled);
        if (host.Main is {} existing && !ReferenceEquals(existing.Tag, layer))
        {
            if (existing.ContextMenu?.IsOpen == true) existing.ContextMenu.Close();
            existing.Tag = layer;
        }
        if (host.Shown == state) return;
        host.Shown = state;
        var main = BuildRow(entry, state);
        host.Main = rowFor[layer.Id] = main;
        if (layer.Effects is not { IsEmpty: false } && layer.SmartFilters.Length == 0)
        {
            host.Child = main;
            return;
        }
        var group = new StackPanel { Children = { main } };
        if (layer.Effects is { } effects)
            foreach (var kind in effects.Kinds) group.Children.Add(BuildEffectRow(layer, kind, entry.Depth, entry.ParentVisible && layer.Visible));
        if (layer.SmartFilters.Length > 0) group.Children.Add(BuildSmartFilterMaskRow(layer, entry.Depth));
        for (var i = layer.SmartFilters.Length - 1; i >= 0; i--)
            group.Children.Add(BuildSmartFilterRow(layer, layer.SmartFilters[i], entry.Depth));
        host.Child = group;
    }

    private Bitmap? PreviewImage(Layer layer, RowHost host)
    {
        if (layer.Pixels == null) { host.Preview = null; return null; }
        var appearance = layer.HasWarp ? layer.WarpAppearance(Math.Min(1, 68d / Math.Max(layer.Pixels.Width, layer.Pixels.Height))) : layer;
        if (!appearance.HasSmartFilterMask) { host.Preview = null; return Thumbnail(appearance.Pixels!); }

        // In-place dabs replace the thumbnail generation in Pixels.Invalidated. Use its
        // identity here too, including both source planes and the editable filter mask.
        object Generation(SKBitmap bitmap) => Thumbnails.GetValue(bitmap, static _ => new());
        var pixels = Generation(appearance.Pixels!);
        var source = Generation(appearance.FilterSource!);
        var mask = Generation(appearance.SmartFilterMask!);
        var maskMatrix = layer.HasWarp ? layer.SmartFilterMaskMatrix : SKMatrix.Identity;
        var frame = layer.HasWarp ? appearance.Matrix : SKMatrix.Identity;
        if (host.Preview is {} cached && cached.Pixels == pixels && cached.Source == source && cached.Mask == mask
            && cached.X == appearance.SmartFilterMaskX && cached.Y == appearance.SmartFilterMaskY
            && cached.PadX == appearance.FilterPaddingX && cached.PadY == appearance.FilterPaddingY
            && cached.Outside == appearance.SmartFilterMaskDefault && cached.MaskMatrix == maskMatrix && cached.Frame == frame
            && cached.Profile.Equals(displayProfile)) return cached.Image;
        using var small = appearance.RenderFilterPreview(68);
        var image = Ui.ToAvaloniaBitmap(small, 68, displayProfile);
        host.Preview = new(pixels, source, mask, appearance.FilterPaddingX, appearance.FilterPaddingY,
            appearance.SmartFilterMaskX, appearance.SmartFilterMaskY, appearance.SmartFilterMaskDefault, maskMatrix, frame, displayProfile, image);
        return image;
    }

    private Border? LayerRowAt(double y)
    {
        foreach (var row in rowFor.Values)
            if (row.TranslatePoint(default, rows) is {} top && y >= top.Y && y < top.Y + row.Bounds.Height) return row;
        return null;
    }
}
