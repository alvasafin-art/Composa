using Composa.ColorManagement;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Editing;

public sealed partial class EditorSession
{
    private static readonly LayerStyleKind[] LibraryStyleKinds = Enum.GetValues<LayerStyleKind>();

    public bool CanSaveLibraryElement => document.SelectedLayerIds.Count switch
    {
        0 => false,
        1 => ActiveLayer is { Clipped: false },
        _ => SelectedRoots() is [{ Clipped: false }]
    };

    /// <summary>Captures one object, including any clipping layers that belong to it.</summary>
    public Document CaptureLibraryElement(out bool embeddedLinkedObjects)
    {
        FinishInteraction();
        if (SelectedRoots() is not [{ Clipped: false } root])
            throw new InvalidOperationException("Select one layer or group. For a clipping mask, select its base layer.");
        var siblings = document.SiblingsOf(root.Id)!;
        var unit = LayoutUnits(siblings).First(item => item.Layer == root);
        var snapshot = new Document(document.Width, document.Height)
        {
            Depth = document.Depth, Profile = document.Profile, Resolution = document.Resolution, ColorMode = document.ColorMode
        };
        snapshot.Layers.Add(root.Clone());
        snapshot.Layers.AddRange(unit.Companions.Select(layer => layer.Clone()));
        snapshot.SetActive(root.Id);
        foreach (var layer in snapshot.AllLayers())
            foreach (var kind in LibraryStyleKinds)
                if (layer.StyleBinding(kind) is { } link && document.Styles.TryGetValue(link.Id, out var definition))
                    snapshot.Styles[link.Id] = definition;
        embeddedLinkedObjects = EmbedLibrarySources(snapshot, newIdentity: false);
        return snapshot;
    }

    private static bool EmbedLibrarySources(Document snapshot, bool newIdentity)
    {
        var copies = new Dictionary<SmartObjectSource, SmartObjectSource>(ReferenceEqualityComparer.Instance);
        bool embeddedLink = false;
        SmartObjectSource Copy(SmartObjectSource source)
        {
            if (copies.TryGetValue(source, out var existing)) return existing;
            embeddedLink |= source.Link != null;
            return copies[source] = source.CopyEmbedded(newIdentity, Copy);
        }
        foreach (var layer in snapshot.AllLayers())
            if (layer.SmartObject is { } source) layer.SmartObject = Copy(source);
        return embeddedLink;
    }

    public static Document PrepareLibraryElement(Document element, RgbProfile profile)
    {
        if (element.Profile.Equals(profile)) return element;
        var conversion = new EditorSession(element);
        conversion.ChangeColorProfile(profile, assign: false, flatten: false, preserveLivePixels: true);
        return conversion.Document;
    }

    /// <summary>Inserts editable copies at the canvas root, with library styles remapped in the same undo step.</summary>
    public Layer InsertLibraryElement(Document element, SKPoint? center = null)
    {
        if (element.Layers is not [{ Clipped: false }, ..] || element.Layers.Skip(1).Any(layer => !layer.Clipped))
            throw new InvalidDataException("The library element must contain one layer or group with its clipping layers.");
        FinishInteraction();
        element = PrepareLibraryElement(element, document.Profile);
        var copy = new Document(element.Width, element.Height);
        copy.Layers.AddRange(element.Layers.Select(layer => layer.Clone(newIds: true)));
        EmbedLibrarySources(copy, newIdentity: true);
        if (document.RasterPixels() + copy.RasterPixels() > DocumentLimits.DocumentPixelBudget)
            throw new InvalidOperationException("The library element exceeds the document raster memory budget.");

        var imported = new List<LayerStyleDefinition>();
        var remapped = new Dictionary<Guid, Guid>();
        foreach (var definition in element.Styles.Values)
        {
            var candidates = document.Styles.Values.Concat(imported).Where(style => style.Kind == definition.Kind).ToArray();
            var match = candidates.Where(style => style.Text == definition.Text && style.Appearance == definition.Appearance)
                .OrderByDescending(style => string.Equals(style.Name, definition.Name, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            if (match != null) { remapped.Add(definition.Id, match.Id); continue; }
            if (document.Styles.Count + imported.Count == DocumentLimits.MaxLayerStyles)
                throw new InvalidOperationException("The document has reached its shared style limit.");
            var names = candidates.Select(style => style.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var name = definition.Name;
            for (int number = 2; names.Contains(name); number++)
            {
                var suffix = $" ({number})";
                int length = Math.Min(definition.Name.Length, LayerStyleDefinition.MaxNameLength - suffix.Length);
                if (char.IsHighSurrogate(definition.Name[length - 1])) length--;
                name = definition.Name[..length].TrimEnd() + suffix;
            }
            var local = definition with { Id = Guid.NewGuid(), Name = name };
            imported.Add(local);
            remapped.Add(definition.Id, local.Id);
        }

        var root = copy.Layers[0];
        var bounds = LayoutBounds(root);
        var location = center ?? new SKPoint(document.Width / 2f, document.Height / 2f);
        var dx = bounds.IsEmpty ? 0 : Math.Round(location.X - bounds.MidX);
        var dy = bounds.IsEmpty ? 0 : Math.Round(location.Y - bounds.MidY);
        foreach (var layer in copy.AllLayers())
        {
            layer.TranslateContent(dx, dy);
            foreach (var kind in LibraryStyleKinds)
                if (layer.StyleBinding(kind) is { } link)
                    layer.SetStyleBinding(kind, remapped.TryGetValue(link.Id, out var id) ? link with { Id = id } : null);
        }
        Apply("Insert Library Element", () =>
        {
            foreach (var style in imported) document.Styles.Add(style.Id, style);
            document.Layers.AddRange(copy.Layers);
            document.SetActive(root.Id);
        });
        EditingMask = false;
        InvalidateAll();
        LayersChanged?.Invoke();
        return root;
    }
}
