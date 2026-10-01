using System.Text.Json.Nodes;
using Composa.AI;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.App.AI;

/// <summary>API inputs use original canvas coordinates. No latent-size crop, model weights or GPU buffers.</summary>
internal sealed class PartnerImageInputs : IDisposable
{
    public Dictionary<string, SKBitmap> Images { get; } = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SKBitmap> owned = [];
    private readonly AiTaskInputs original;
    private readonly AiTaskRequest request;
    private readonly SKRectI crop;
    private readonly SKBitmap? blendMask;
    private readonly (int Width, int Height) generationSize;
    public bool MaskAsReference { get; }

    public PartnerImageInputs(AiTaskInputs inputs, AiTaskRequest request)
    {
        original = inputs; this.request = request;
        var source = request.Task is AiTaskKind.RemoveObject or AiTaskKind.ChangeBackground or AiTaskKind.GenerativeExpand
            ? inputs.PreprocessedImage ?? inputs.SourceImage : inputs.SourceImage;
        var mask = request.Task == AiTaskKind.ChangeBackground ? inputs.BackgroundMask
            : request.Task == AiTaskKind.GenerativeExpand ? inputs.PreprocessedMask : inputs.SelectionMask;
        if (request.Task is AiTaskKind.GenerateImage or AiTaskKind.ImageEdit || request.Task == AiTaskKind.GenerativeExpand && request.ExpansionMode == AiExpansionMode.WholeImage) mask = null;
        if (mask != null && SelectionMask.IsEmpty(mask)) throw new InvalidOperationException("The edit mask is empty. For Expand, expose empty canvas or choose Whole image.");
        crop = new SKRectI(0, 0, source.Width, source.Height);
        if (mask != null)
        {
            int Setting(string name, int fallback) => request.Settings.Values.TryGetValue(name, out var value) ? Math.Clamp(Convert.ToInt32(value), 0, 64) : fallback;
            var grow = request.Task is AiTaskKind.GenerativeFill or AiTaskKind.GenerativeExpand ? 0 : Setting("maskGrow", 8);
            var blend = Setting("maskBlend", 32); var blur = Setting("maskBlur", 4);
            using var expanded = SelectionMask.Expand(mask, grow);
            var conditioning = Own(blur == 0 ? Pixels.Clone(expanded) : SelectionMask.Feather(expanded, blur));
            blendMask = Own(AiResultPostprocessor.EditMask(mask, grow, blend));
            if (request.Task == AiTaskKind.RemoveObject)
            {
                var removal = RemoveObjectPreprocessor.Prepare(inputs.SourceImage, mask, new RemoveObjectSettings { Dilation = grow, Feather = 0 });
                source = Own(removal.Image); removal.Mask.Dispose();
            }
            if (request.Task is AiTaskKind.GenerativeFill or AiTaskKind.RemoveObject or AiTaskKind.Harmonize or AiTaskKind.Relight or AiTaskKind.GenerativeExpand)
            {
                var bounds = SelectionMask.Bounds(mask, 1);
                var factor = request.Settings.Values.TryGetValue("maskContext", out var context) ? Math.Clamp(Convert.ToDouble(context), 1, 8) : 2;
                var margin = Math.Max(grow + 4 * blend + 3 * blur, (int)Math.Ceiling(Math.Max(bounds.Width, bounds.Height) * (factor - 1) / 2));
                crop = SKRectI.Intersect(crop, new SKRectI(bounds.Left - margin, bounds.Top - margin, bounds.Right + margin, bounds.Bottom + margin));
            }
            Images["apiMask"] = Own(Crop(conditioning, crop));
        }
        if (request.Task != AiTaskKind.GenerateImage) Images["apiSource"] = Own(Crop(source, crop));
        for (var i = 0; i < inputs.ReferenceImages.Count; i++) Images[$"referenceImage{i + 1}"] = inputs.ReferenceImages[i];
        MaskAsReference = Images.ContainsKey("apiMask") && inputs.ReferenceImages.Count > 0;
        var desired = (Width: crop.Width, Height: crop.Height);
        if (request.Task == AiTaskKind.GenerateImage) desired = (inputs.CanvasWidth, inputs.CanvasHeight);
        else if (request.Task == AiTaskKind.GenerativeExpand) desired = AiDimensions.FromMinimumSide(request.ExpansionMinimumSide, crop.Width, crop.Height);
        else if (!Convert.ToBoolean(request.Settings.Values.GetValueOrDefault("imageOriginalSize") ?? false) && request.Settings.Width > 0 && request.Settings.Height > 0)
        {
            var scale = Math.Sqrt((double)request.Settings.Width * request.Settings.Height / crop.Width / crop.Height);
            desired = ((int)Math.Round(crop.Width * scale), (int)Math.Round(crop.Height * scale));
        }
        try { generationSize = PartnerImageSize.Plan(desired.Width, desired.Height); }
        catch { Dispose(); throw; }
    }

    public JsonObject Bind(JsonObject graph, EngineProfile engine, IReadOnlyDictionary<string, string> files, long seed)
    {
        var result = (JsonObject)graph.DeepClone(); var node = result["gpt"]!["inputs"]!.AsObject();
        node["model"] = engine.ApiModel; node["seed"] = seed % int.MaxValue;
        node["model.quality"] = request.Settings.Values.GetValueOrDefault("apiQuality")?.ToString() ?? "low";
        node["model.size"] = "Custom";
        node["model.custom_width"] = generationSize.Width;
        node["model.custom_height"] = generationSize.Height;
        var number = 0;
        foreach (var (semantic, filename) in files.Where(pair => pair.Key != "apiMask"))
        {
            var id = "composa_api_image_" + ++number;
            result[id] = Load(filename); node[$"model.images.image_{number}"] = new JsonArray(id, 0);
        }
        var prompt = original.Prompt;
        if (files.TryGetValue("apiMask", out var mask))
        {
            result["composa_api_mask"] = Load(mask);
            if (MaskAsReference)
            {
                node[$"model.images.image_{++number}"] = new JsonArray("composa_api_mask", 0);
                prompt += $"\nImage 1 is the source to edit. Images 2 through {number - 1} are visual references, in order. Image {number} is ONLY a grayscale editing mask for image 1: change the white area, preserve the black area. Do not reproduce this mask as an output image. Keep image 1's framing and dimensions.";
            }
            else
            {
                result["composa_api_mask_convert"] = new JsonObject { ["class_type"] = "ImageToMask", ["inputs"] = new JsonObject
                    { ["image"] = new JsonArray("composa_api_mask", 0), ["channel"] = "red" } };
                node["model.mask"] = new JsonArray("composa_api_mask_convert", 0);
            }
        }
        node["prompt"] = prompt;
        return result;
    }

    public SKBitmap Finish(SKBitmap generated)
    {
        if (request.Task == AiTaskKind.GenerateImage)
        {
            using (generated) return Resize(generated, original.CanvasWidth, original.CanvasHeight);
        }
        using (generated)
        {
            using var fitted = Resize(generated, crop.Width, crop.Height);
            if (request.Task == AiTaskKind.GenerativeExpand)
            {
                if (request.ExpansionMode == AiExpansionMode.WholeImage) return Pixels.Clone(fitted);
                var expanded = original.ExpandedContext();
                using var coverage = AiResultPostprocessor.ExpansionEditMask(original.PreprocessedMask!, expanded,
                    Convert.ToInt32(request.Settings.Values.GetValueOrDefault("maskBlend") ?? 32));
                Blend(expanded, fitted, coverage, crop); return expanded;
            }
            if (request.Task == AiTaskKind.ChangeBackground) return Pixels.Clone(fitted);
            var composite = Pixels.Clone(original.ContextImage);
            Blend(composite, fitted, blendMask, crop);
            return composite;
        }
    }

    private SKBitmap Own(SKBitmap bitmap) { owned.Add(bitmap); return bitmap; }
    private static JsonObject Load(string filename) => new() { ["class_type"] = "LoadImage", ["inputs"] = new JsonObject { ["image"] = filename } };
    private static SKBitmap Crop(SKBitmap source, SKRectI bounds)
    {
        var result = source.ColorType == SKColorType.Alpha8 ? Pixels.NewMask(bounds.Width, bounds.Height) : Pixels.NewColor(bounds.Width, bounds.Height);
        using var canvas = new SKCanvas(result); canvas.DrawImage(Pixels.ImageOf(source), -bounds.Left, -bounds.Top); return result;
    }
    private static SKBitmap Resize(SKBitmap source, int width, int height)
    {
        var mismatch = Math.Abs((double)source.Width / source.Height / ((double)width / height) - 1);
        if (mismatch > 0.025) throw new InvalidDataException($"GPT returned {source.Width} × {source.Height} with different proportions from {width} × {height}. Refusing to stretch or misalign it with the mask; no edit was applied.");
        var result = Pixels.NewColor(width, height); using var canvas = new SKCanvas(result);
        var scale = Math.Max((double)width / source.Width, (double)height / source.Height);
        var w = (float)(source.Width * scale); var h = (float)(source.Height * scale);
        canvas.DrawImage(Pixels.ImageOf(source), new SKRect((width - w) / 2, (height - h) / 2, (width + w) / 2, (height + h) / 2), new SKSamplingOptions(SKCubicResampler.Mitchell)); return result;
    }
    private static unsafe void Blend(SKBitmap destination, SKBitmap image, SKBitmap? mask, SKRectI bounds)
    {
        var target = (byte*)destination.GetPixels(); var source = (byte*)image.GetPixels(); var coverage = mask == null ? null : (byte*)mask.GetPixels();
        for (var y = bounds.Top; y < bounds.Bottom; y++)
        for (var x = bounds.Left; x < bounds.Right; x++)
        {
            var amount = coverage == null ? 255 : coverage[(long)y * mask!.RowBytes + x];
            for (var c = 0; c < 4; c++)
            {
                var offset = (long)y * destination.RowBytes + x * 4 + c;
                target[offset] = (byte)((target[offset] * (255 - amount) + source[(long)(y - bounds.Top) * image.RowBytes + (x - bounds.Left) * 4 + c] * amount + 127) / 255);
            }
        }
        Pixels.Invalidate(destination);
    }
    public void Dispose() { foreach (var bitmap in owned) bitmap.Dispose(); }
}
