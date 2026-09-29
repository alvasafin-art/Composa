using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.AI;

public enum RemoveObjectFillMode { Neutral, Solid, Transparent }

public sealed record RemoveObjectSettings
{
    public int Dilation { get; init; } = 8;
    public float Feather { get; init; } = 4;
    public RemoveObjectFillMode FillMode { get; init; } = RemoveObjectFillMode.Neutral;
    public SKColor FillColor { get; init; } = new(127, 127, 127);
}

public sealed record AiGenerationSettings
{
    public int Width { get; init; }
    public int Height { get; init; }
    public long Seed { get; init; } = -1;
    public Dictionary<string, object?> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record AiTaskRequest
{
    public AiTaskKind Task { get; init; }
    public string Prompt { get; init; } = "";
    public string NegativePrompt { get; init; } = "";
    public AiGenerationSettings Settings { get; init; } = new();
    public SKRectI? ExpansionBounds { get; init; }
    public RemoveObjectSettings RemoveObject { get; init; } = new();
}

/// <summary>Canonical editor assets from which an engine binding selects the inputs it needs.</summary>
public sealed class AiTaskInputs : IDisposable
{
    public SKBitmap SourceImage { get; init; } = null!;
    public SKBitmap ContextImage { get; init; } = null!;
    public SKBitmap? ActiveLayerImage { get; init; }
    public SKBitmap? SelectionMask { get; init; }
    public SKBitmap? AlphaMask { get; init; }
    public SKBitmap? PreprocessedImage { get; init; }
    public SKBitmap? PreprocessedMask { get; init; }
    public int CanvasWidth { get; init; }
    public int CanvasHeight { get; init; }
    public SKRectI? ExpansionBounds { get; init; }
    public string Prompt { get; init; } = "";
    public string NegativePrompt { get; init; } = "";
    public long Seed { get; init; }

    public IReadOnlyDictionary<string, SKBitmap> Images()
    {
        var images = new Dictionary<string, SKBitmap>(StringComparer.OrdinalIgnoreCase)
        {
            ["sourceImage"] = SourceImage,
            ["contextImage"] = ContextImage
        };
        if (ActiveLayerImage != null) images["activeLayerImage"] = ActiveLayerImage;
        if (SelectionMask != null) images["selectionMask"] = SelectionMask;
        if (AlphaMask != null) images["alphaMask"] = AlphaMask;
        if (PreprocessedImage != null) images["preprocessedImage"] = PreprocessedImage;
        if (PreprocessedMask != null) images["preprocessedMask"] = PreprocessedMask;
        return images;
    }

    public Dictionary<string, object?> Values(IReadOnlyDictionary<string, string> uploadedImages)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["prompt"] = Prompt,
            ["negativePrompt"] = NegativePrompt,
            ["seed"] = Seed,
            ["width"] = CanvasWidth,
            ["height"] = CanvasHeight
        };
        foreach (var image in uploadedImages) values[image.Key] = image.Value;
        if (ExpansionBounds is { } bounds)
        {
            values["expansionX"] = bounds.Left;
            values["expansionY"] = bounds.Top;
            values["expansionWidth"] = bounds.Width;
            values["expansionHeight"] = bounds.Height;
        }
        return values;
    }

    public void Dispose()
    {
        var disposed = new HashSet<SKBitmap>(ReferenceEqualityComparer.Instance);
        foreach (var image in Images().Values) if (disposed.Add(image)) image.Dispose();
    }
}

public static class AiTaskInputPreparer
{
    public static AiTaskInputs Prepare(EditorSession session, AiTaskRequest request)
    {
        if (request.Task.RequiresSelection() && session.Selection == null)
            throw new InvalidOperationException($"{request.Task.DisplayName()} requires a selection.");

        var source = session.Flatten();
        var context = Pixels.Clone(source);
        var active = RenderActiveLayer(session);
        var selection = session.Selection == null ? null : Pixels.Clone(session.Selection);
        var alpha = session.ActiveLayer is { Pixels: not null } layer ? SelectionMask.FromLayer(session.Document, layer, fromMask: false) : null;
        SKBitmap? preprocessed = null, preprocessedMask = null;
        if (request.Task == AiTaskKind.RemoveObject && selection != null)
            (preprocessed, preprocessedMask) = RemoveObjectPreprocessor.Prepare(source, selection, request.RemoveObject);

        return new AiTaskInputs
        {
            SourceImage = source,
            ContextImage = context,
            ActiveLayerImage = active,
            SelectionMask = selection,
            AlphaMask = alpha,
            PreprocessedImage = preprocessed,
            PreprocessedMask = preprocessedMask,
            CanvasWidth = request.Settings.Width > 0 ? request.Settings.Width : session.Document.Width,
            CanvasHeight = request.Settings.Height > 0 ? request.Settings.Height : session.Document.Height,
            ExpansionBounds = request.ExpansionBounds,
            Prompt = request.Prompt,
            NegativePrompt = request.NegativePrompt,
            Seed = request.Settings.Seed
        };
    }

    private static SKBitmap? RenderActiveLayer(EditorSession session)
    {
        if (session.ActiveLayer is not { Pixels: not null } active) return null;
        var overrides = new Dictionary<Guid, Layer>();
        var keep = new HashSet<Guid> { active.Id };
        for (var parent = session.Document.ParentOf(active.Id); parent != null; parent = session.Document.ParentOf(parent.Id)) keep.Add(parent.Id);
        foreach (var layer in session.Document.AllLayers())
        {
            if (keep.Contains(layer.Id)) continue;
            var hidden = layer.Clone();
            hidden.Visible = false;
            overrides[layer.Id] = hidden;
        }
        return DocumentRenderer.Flatten(session.Document, new RenderOptions { Overrides = overrides });
    }
}

public static class RemoveObjectPreprocessor
{
    public static (SKBitmap Image, SKBitmap Mask) Prepare(SKBitmap source, SKBitmap rawMask, RemoveObjectSettings settings)
    {
        if (source.Width != rawMask.Width || source.Height != rawMask.Height)
            throw new ArgumentException("The source image and remove-object mask must have the same dimensions.");
        var dilation = Math.Clamp(settings.Dilation, 0, 512);
        var feather = Math.Clamp(settings.Feather, 0, 256);
        var expanded = dilation > 0 ? SelectionMask.Expand(rawMask, dilation) : Pixels.Clone(rawMask);
        var mask = feather > 0 ? SelectionMask.Feather(expanded, feather) : Pixels.Clone(expanded);
        expanded.Dispose();
        var image = Pixels.Clone(source);
        Fill(image, mask, settings.FillMode == RemoveObjectFillMode.Neutral ? new SKColor(127, 127, 127) : settings.FillColor,
            settings.FillMode == RemoveObjectFillMode.Transparent);
        return (image, mask);
    }

    private static unsafe void Fill(SKBitmap image, SKBitmap mask, SKColor color, bool transparent)
    {
        var pixels = (byte*)image.GetPixels();
        var coverage = (byte*)mask.GetPixels();
        for (var y = 0; y < image.Height; y++)
        {
            var row = pixels + (long)y * image.RowBytes;
            var selected = coverage + (long)y * mask.RowBytes;
            for (var x = 0; x < image.Width; x++)
            {
                var amount = selected[x];
                if (amount == 0) continue;
                var offset = x * 4;
                byte r = transparent ? (byte)0 : color.Red, g = transparent ? (byte)0 : color.Green,
                    b = transparent ? (byte)0 : color.Blue, a = transparent ? (byte)0 : color.Alpha;
                row[offset] = Blend(row[offset], r, amount);
                row[offset + 1] = Blend(row[offset + 1], g, amount);
                row[offset + 2] = Blend(row[offset + 2], b, amount);
                row[offset + 3] = Blend(row[offset + 3], a, amount);
            }
        }
        Pixels.Invalidate(image);
    }

    private static byte Blend(byte from, byte to, byte amount) => (byte)((from * (255 - amount) + to * amount + 127) / 255);
}
