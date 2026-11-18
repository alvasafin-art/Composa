using System.Text.Json.Nodes;
using Composa.AI;
using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.App.AI;

/// <summary>Native Qwen references are opaque sRGB model copies. Crops and output masks stay in document coordinates.</summary>
internal sealed class QwenImageInputs : IDisposable
{
    internal Dictionary<string, SKBitmap> Images { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal SKBitmap? Mask { get; }
    internal SKRectI Bounds { get; }
    internal (int Width, int Height) ContentSize { get; }
    internal (int Width, int Height) PaddedSize { get; }
    private readonly AiTaskInputs inputs;
    private readonly AiTaskRequest request;
    private readonly AiMaskPlan? plan;
    private readonly string prompt;
    private readonly List<SKBitmap> owned = [];

    internal QwenImageInputs(AiTaskInputs inputs, AiTaskRequest request)
    {
        this.inputs = inputs; this.request = request;
        try
        {
            var generate = request.Task == AiTaskKind.GenerateImage;
            var expand = request.Task == AiTaskKind.GenerativeExpand;
            var source = expand ? inputs.PreprocessedImage! : inputs.SourceImage;
            var mask = expand ? inputs.PreprocessedMask : inputs.SelectionMask;
            if (generate || request.Task == AiTaskKind.ImageEdit || expand && request.ExpansionMode == AiExpansionMode.WholeImage) mask = null;
            Bounds = source.Info.Rect;
            if (mask != null)
            {
                if (SelectionMask.IsEmpty(mask)) throw new InvalidOperationException("The edit mask is empty.");
                using var context = expand ? inputs.ExpandedContext() : Pixels.Clone(inputs.ContextImage);
                var core = expand ? inputs.OutputMask ?? mask : mask;
                plan = inputs.MaskPlan ?? AutomaticAiMask.Analyze(core, context);
                Bounds = plan.Bounds;
                Mask = Own(AutomaticAiMask.OutputMask(core, context, plan, expand));
                // Give the model an unambiguous visible edit region; the mask itself is never an image reference.
                var marked = RemoveObjectPreprocessor.Prepare(request.BlackEditRegion ? inputs.ContextImage : source, mask, new() { Dilation = 0, Feather = 0 });
                source = Own(marked.Image); marked.Mask.Dispose();
            }
            if (generate)
            {
                ContentSize = (inputs.CanvasWidth, inputs.CanvasHeight);
                PaddedSize = PadSize(ContentSize);
                prompt = request.Prompt.Trim();
            }
            else
            {
                var original = expand ? request.ExpansionMinimumSide == 0 : Convert.ToBoolean(request.Settings.Values.GetValueOrDefault("imageOriginalSize") ?? false);
                ContentSize = AiContextGeometry.FluxSize(Bounds, inputs.TargetBounds, inputs.CanvasWidth, inputs.CanvasHeight, original,
                    expand ? request.ExpansionMinimumSide : null).Content;
                PaddedSize = PadSize(ContentSize);
                Images["sourceImage"] = Own(Pad(source, Bounds, ContentSize, PaddedSize));
                if (mask != null)
                {
                    // Diffusion needs source latents outside the hole. The conditioning node supplies
                    // a blank sampling latent, even when its image reference contains real pixels.
                    using var clean = expand ? inputs.ExpandedContext() : Pixels.Clone(inputs.ContextImage);
                    Images["latentSource"] = Own(Pad(clean, Bounds, ContentSize, PaddedSize));
                    using var grown = SelectionMask.Expand(mask, plan!.ConditioningGrow(ContentSize.Width / (double)Bounds.Width));
                    var noiseMask = Own(Pixels.NewMask(PaddedSize.Width, PaddedSize.Height));
                    using (var draw = new SKCanvas(noiseMask))
                        draw.DrawImage(Pixels.ImageOf(grown), new SKRect(Bounds.Left, Bounds.Top, Bounds.Right, Bounds.Bottom),
                            SKRect.Create(ContentSize.Width, ContentSize.Height), new SKSamplingOptions(SKFilterMode.Linear));
                    Pixels.Invalidate(noiseMask);
                    Images["noiseMask"] = noiseMask;
                }
                prompt = request.Task switch
                {
                    AiTaskKind.GenerativeFill when request.BlackEditRegion => AiPromptDefaults.Expand,
                    AiTaskKind.GenerativeFill => "Replace the black marked region with the requested content, matching the surrounding image. " + request.Prompt,
                    AiTaskKind.RemoveObject when mask != null => "Remove the object marked in black and reconstruct the surrounding background naturally. " + request.Prompt,
                    AiTaskKind.GenerativeExpand => AiPromptDefaults.Expand,
                    _ => inputs.Prompt
                };
                if (request.Task is AiTaskKind.GenerativeFill or AiTaskKind.GenerativeExpand || request.Task == AiTaskKind.RemoveObject && mask != null)
                    if (!string.IsNullOrWhiteSpace(request.AdditionalPrompt))
                    prompt += "\n" + request.AdditionalPrompt.Trim();
            }
            for (var index = 0; index < inputs.ReferenceImages.Count; index++) Images["referenceImage" + (index + 1)] = inputs.ReferenceImages[index];
        }
        catch { Dispose(); throw; }
    }

    internal JsonObject Bind(JsonObject graph, IReadOnlyDictionary<string, string> files, long seed)
    {
        var result = (JsonObject)graph.DeepClone();
        QwenImageWorkflow.Bind(result, files, prompt, seed, PaddedSize.Width, PaddedSize.Height);
        if (Mask != null)
        {
            result["clean"] = new JsonObject { ["class_type"] = "LoadImage", ["inputs"] = new JsonObject { ["image"] = files["latentSource"] } };
            result["encode"] = new JsonObject { ["class_type"] = "VAEEncode", ["inputs"] = new JsonObject { ["pixels"] = new JsonArray("clean", 0), ["vae"] = new JsonArray("vae", 0) } };
            result["mask"] = new JsonObject { ["class_type"] = "LoadImageMask", ["inputs"] = new JsonObject { ["image"] = files["noiseMask"], ["channel"] = "red" } };
            result["masked"] = new JsonObject { ["class_type"] = "SetLatentNoiseMask", ["inputs"] = new JsonObject { ["samples"] = new JsonArray("encode", 0), ["mask"] = new JsonArray("mask", 0) } };
            result["sample"]!["inputs"]!["latent_image"] = new JsonArray("masked", 0);
        }
        return result;
    }

    internal SKBitmap Finish(SKBitmap decoded)
    {
        using (decoded)
        {
            if ((decoded.Width, decoded.Height) != PaddedSize)
                throw new InvalidDataException($"Qwen returned {decoded.Width} × {decoded.Height}; expected {PaddedSize.Width} × {PaddedSize.Height}. No edit was applied.");
            using var content = new SKBitmap();
            if (!decoded.ExtractSubset(content, SKRectI.Create(ContentSize.Width, ContentSize.Height)))
                throw new InvalidDataException("Cannot remove technical Qwen padding.");
            if (request.Task == AiTaskKind.GenerateImage) return Pixels.Clone(content);
            var result = request.Task == AiTaskKind.GenerativeExpand ? inputs.ExpandedContext() : Pixels.Clone(inputs.ContextImage);
            using (var draw = new SKCanvas(result))
            using (var paint = new SKPaint { BlendMode = SKBlendMode.Src })
                draw.DrawImage(Pixels.ImageOf(content), new SKRect(Bounds.Left, Bounds.Top, Bounds.Right, Bounds.Bottom),
                    (content.Width, content.Height) == (Bounds.Width, Bounds.Height) ? new SKSamplingOptions(SKFilterMode.Nearest) : new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
            Pixels.Invalidate(result);
            if (Mask == null) return result;
            using (result)
            using (var context = request.Task == AiTaskKind.GenerativeExpand ? inputs.ExpandedContext() : Pixels.Clone(inputs.ContextImage))
                return AiSeamlessFinisher.Match(result, context, Mask, Bounds, plan!.SeamWidth, alignPixels: request.AlignMaskedEdits);
        }
    }

    private SKBitmap Own(SKBitmap image) { owned.Add(image); return image; }
    private static (int Width, int Height) PadSize((int Width, int Height) size)
    {
        var result = (Width: checked((size.Width + 31) / 32 * 32), Height: checked((size.Height + 31) / 32 * 32));
        if (!DocumentLimits.FitsSurface(result.Width, result.Height)) throw new InvalidOperationException("Qwen input including padding exceeds document limits. Reduce image size.");
        return result;
    }
    private static SKBitmap Pad(SKBitmap source, SKRectI bounds, (int Width, int Height) content, (int Width, int Height) padded)
    {
        var result = Pixels.NewColor(padded.Width, padded.Height);
        try
        {
            result.Erase(SKColors.White);
            using var crop = new SKBitmap();
            if (!source.ExtractSubset(crop, bounds)) throw new InvalidDataException("Cannot read the Qwen context crop.");
            using var draw = new SKCanvas(result);
            draw.DrawImage(Pixels.ImageOf(crop), SKRect.Create(content.Width, content.Height),
                (crop.Width, crop.Height) == content ? new SKSamplingOptions(SKFilterMode.Nearest) : new SKSamplingOptions(SKCubicResampler.Mitchell));
            if (padded.Width > content.Width)
                draw.DrawImage(Pixels.ImageOf(crop), new SKRect(crop.Width - 1, 0, crop.Width, crop.Height), new SKRect(content.Width, 0, padded.Width, content.Height), new SKSamplingOptions(SKFilterMode.Nearest));
            if (padded.Height > content.Height)
                draw.DrawImage(Pixels.ImageOf(crop), new SKRect(0, crop.Height - 1, crop.Width, crop.Height), new SKRect(0, content.Height, padded.Width, padded.Height), new SKSamplingOptions(SKFilterMode.Nearest));
            Pixels.Invalidate(result); return result;
        }
        catch { result.Dispose(); throw; }
    }
    public void Dispose() { foreach (var image in owned) image.Dispose(); }
}
