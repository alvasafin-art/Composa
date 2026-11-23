using System.Text.Json.Nodes;
using Composa.AI;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.App.AI;

/// <summary>Exports decoded pixels, not a baked stitch. The saved ROI and local layer mask share source coordinates.</summary>
internal sealed class EditableMaskedWorkflow : IDisposable
{
    internal SKRectI Bounds { get; }
    internal SKBitmap Mask { get; }
    internal SKBitmap? ConditioningImage { get; }
    private readonly AiTaskInputs inputs;
    private readonly AiTaskRequest request;
    private readonly AiMaskPlan plan;
    private bool nativePixels;
    internal (int Width, int Height) ContentSize { get; private set; }
    internal (int Width, int Height) GenerationSize { get; private set; }

    internal EditableMaskedWorkflow(AiTaskInputs inputs, AiTaskRequest request)
    {
        this.inputs = inputs; this.request = request;
        var sourceMask = request.Task == AiTaskKind.GenerativeExpand ? inputs.PreprocessedMask!
            : request.Task == AiTaskKind.ChangeBackground ? inputs.BackgroundMask! : inputs.PreprocessedMask ?? inputs.SelectionMask!;
        using var context = request.Task == AiTaskKind.GenerativeExpand ? inputs.ExpandedContext() : Pixels.Clone(inputs.ContextImage);
        var core = request.Task == AiTaskKind.GenerativeExpand ? inputs.OutputMask ?? sourceMask : sourceMask;
        plan = inputs.MaskPlan ?? AutomaticAiMask.Analyze(core, context);
        Bounds = plan.Bounds;
        if (Bounds.IsEmpty) throw new InvalidOperationException("The edit mask is empty.");
        Mask = AutomaticAiMask.OutputMask(core, context, plan, request.Task == AiTaskKind.GenerativeExpand);
        // Expansion already has a black frame that matches its prompt and carries
        // the known scene in its final position.
        if (request.Task != AiTaskKind.GenerativeExpand && (request.Task == AiTaskKind.RemoveObject || request.BlackEditRegion))
            ConditioningImage = AiConditioningImage.Continue(context, core);
    }

    internal void Bind(JsonObject graph, string? conditioningFile = null)
    {
        var pixaroma = graph["crop"]!["class_type"]!.GetValue<string>() == "PixaromaInpaintCrop";
        var crop = graph["crop"]!["inputs"]!.AsObject();
        var image = crop["image"]!.DeepClone(); var mask = crop["mask"]!.DeepClone();
        var original = Convert.ToBoolean(request.Settings.Values.GetValueOrDefault("imageOriginalSize") ?? false);
        nativePixels = request.Task == AiTaskKind.GenerativeExpand ? request.ExpansionMinimumSide == 0 : original;
        (ContentSize, GenerationSize) = AiContextGeometry.FluxSize(Bounds, inputs.TargetBounds, inputs.CanvasWidth, inputs.CanvasHeight,
            nativePixels, request.Task == AiTaskKind.GenerativeExpand ? request.ExpansionMinimumSide : null);
        nativePixels = nativePixels && ContentSize == (Bounds.Width, Bounds.Height);
        var (width,height) = GenerationSize;
        var grow = plan.ConditioningGrow(Math.Min((double)ContentSize.Width / Bounds.Width, (double)ContentSize.Height / Bounds.Height));
        const int blur = AiMaskPlan.ModelBlur;
        JsonObject Node(string type, JsonObject values) => new() { ["class_type"] = type, ["inputs"] = values };
        if (ConditioningImage != null)
        {
            var instruction = request.Task == AiTaskKind.RemoveObject
                ? "Restore the empty background where the object was removed. Continue visible surfaces, edges and lighting through the masked area."
                : "Extend the existing scene through the masked area. Continue the adjacent colors, surfaces, perspective and lighting.";
            var guidance = request.Prompt.Trim();
            graph["prompt"]!["inputs"]!["text"] = string.Join("\n\n", new[] { instruction + " Keep smooth surfaces smooth and retain existing texture.",
                guidance == AiPromptDefaults.Expand ? "" : guidance, request.AdditionalPrompt.Trim() }.Where(value => value.Length > 0));
            graph["composa_edit_source"] = Node("LoadImage", new() { ["image"] = conditioningFile ?? "composa-conditioning-preflight.png" });
            image = new JsonArray("composa_edit_source", 0);
        }
        graph["composa_edit_crop"] = Node("ImageCrop", new() { ["image"] = image, ["x"] = Bounds.Left, ["y"] = Bounds.Top, ["width"] = Bounds.Width, ["height"] = Bounds.Height });
        if (!nativePixels)
            graph["composa_edit_resize"] = Node("ImageScale", new() { ["image"] = new JsonArray("composa_edit_crop", 0),
                ["width"] = ContentSize.Width, ["height"] = ContentSize.Height, ["upscale_method"] = "lanczos", ["crop"] = "disabled" });
        graph["composa_edit_size"] = Node("ImagePadForOutpaint", new() { ["image"] = new JsonArray(nativePixels ? "composa_edit_crop" : "composa_edit_resize",0),
            ["left"] = 0, ["top"] = 0, ["right"] = width-ContentSize.Width, ["bottom"] = height-ContentSize.Height, ["feathering"] = 0 });
        graph["composa_edit_mask_grow"] = Node("GrowMask", new() { ["mask"] = mask, ["expand"] = grow, ["tapered_corners"] = true });
        graph["composa_edit_mask_crop"] = Node("CropMask", new() { ["mask"] = new JsonArray("composa_edit_mask_grow", 0), ["x"] = Bounds.Left, ["y"] = Bounds.Top, ["width"] = Bounds.Width, ["height"] = Bounds.Height });
        graph["composa_edit_mask_image"] = Node("MaskToImage", new() { ["mask"] = new JsonArray("composa_edit_mask_crop", 0) });
        var last = "composa_edit_mask_image";
        if (!nativePixels)
        {
            graph["composa_edit_mask_size"] = Node("ImageScale", new() { ["image"] = new JsonArray(last,0),
                ["width"] = ContentSize.Width, ["height"] = ContentSize.Height, ["upscale_method"] = "nearest-exact", ["crop"] = "disabled" });
            last = "composa_edit_mask_size";
        }
        if (blur > 0 && Math.Min(ContentSize.Width,ContentSize.Height) > 1)
        {
            // Like Pixaroma, blur is in MODEL pixels, AFTER nearest-neighbour resizing.
            // Multiple Gaussian passes preserve
            // the requested variance without silently clipping ImageBlur's sigma/radius limits.
            var sigma = (double)blur; var passes = (int)Math.Ceiling(sigma * sigma / 100);
            sigma /= Math.Sqrt(passes);
            for (var i = 0; i < passes; i++)
            {
                var id = i == 0 ? "composa_edit_mask_blur" : $"composa_edit_mask_blur_{i}";
                graph[id] = Node("ImageBlur", new() { ["image"] = new JsonArray(last,0),
                    ["blur_radius"] = Math.Clamp((int)Math.Ceiling(3 * sigma), 1, Math.Min(31, Math.Min(ContentSize.Width,ContentSize.Height)-1)), ["sigma"] = sigma });
                last = id;
            }
        }
        graph["composa_edit_mask"] = Node("ImageToMask", new() { ["image"] = new JsonArray(last,0), ["channel"] = "red" });
        var paddingMask = new JsonArray("composa_edit_size", 1);
        if (request.Task != AiTaskKind.GenerativeExpand)
        {
            graph["composa_edit_empty_mask"] = Node("SolidMask", new() { ["value"] = 0.0, ["width"] = width, ["height"] = height });
            paddingMask = new JsonArray("composa_edit_empty_mask", 0);
        }
        // For outpainting, denoise the technical padding too. Frozen padding
        // gives the model an artificial gray border next to the new content.
        graph["composa_edit_padded_mask"] = Node("MaskComposite", new() { ["destination"] = paddingMask,
            ["source"] = new JsonArray("composa_edit_mask",0), ["x"] = 0, ["y"] = 0, ["operation"] = "add" });
        foreach (var (_, node) in graph)
            if (node?["inputs"] is JsonObject fields)
                foreach (var key in fields.Select(pair => pair.Key).ToArray())
                    if (fields[key] is JsonArray link && link[0]?.GetValue<string>() == "crop")
                    {
                        var slot = link[1]!.GetValue<int>();
                        fields[key] = new JsonArray(slot == (pixaroma ? 0 : 1) ? "composa_edit_size" : "composa_edit_padded_mask", 0);
                    }
        // Both latents must show the source in the same frame. A tightly cropped
        // reference can make FLUX reframe the subject and draw it a second time.
        graph["sourceEncode"]!["inputs"]!["pixels"] = new JsonArray("composa_edit_size", 0);
        var conditioning = graph["composa_condition"]!["inputs"]!;
        var sampler = graph["sampler"]!["inputs"]!;
        if (request.Task == AiTaskKind.GenerativeExpand && sampler["cfg"] is JsonValue cfg
            && cfg.TryGetValue<double>(out var guidanceScale) && guidanceScale == 1)
        {
            // Klein at CFG 1 only uses the positive conditioning. Share the encoded
            // canvas between the reference and sampling branches and add the noise
            // mask to the sampling copy.
            var positive = conditioning["positive"]!.DeepClone();
            graph["composa_edit_zero_conditioning"] = Node("ConditioningZeroOut", new() { ["conditioning"] = positive.DeepClone() });
            graph["composa_edit_masked_latent"] = Node("SetLatentNoiseMask", new()
            {
                ["samples"] = new JsonArray("sourceEncode", 0),
                ["mask"] = new JsonArray("composa_edit_padded_mask", 0)
            });
            graph["latent"]!["inputs"]!["samples"] = new JsonArray("composa_edit_masked_latent", 0);
            sampler["positive"] = positive;
            sampler["negative"] = new JsonArray("composa_edit_zero_conditioning", 0);
            foreach (var id in new[] { "composa_condition", "baseNeg", "negative" }) graph.Remove(id);
        }
        else
        {
            conditioning["mask"] = new JsonArray("composa_edit_padded_mask", 0);
            conditioning["pixels"] = new JsonArray("composa_edit_size", 0);
        }
        foreach (var id in new[] { "blackPatch", "black", "blackSize", "composa_black_mask" }) graph.Remove(id);
        graph.Remove("composa_sampling_mask");
        graph["save"]!["inputs"]!["images"] = new JsonArray("decode", 0);
        graph.Remove("stitch"); graph.Remove("crop");
    }

    internal SKBitmap Finish(SKBitmap decoded)
    {
        using (decoded)
        {
            if ((decoded.Width,decoded.Height) != GenerationSize)
                throw new InvalidDataException($"FLUX returned {decoded.Width} × {decoded.Height}; expected {GenerationSize.Width} × {GenerationSize.Height}. No result was applied.");
            var result = request.Task == AiTaskKind.GenerativeExpand ? inputs.ExpandedContext() : Pixels.Clone(inputs.ContextImage);
            using (var canvas = new SKCanvas(result))
            using (var paint = new SKPaint { BlendMode = SKBlendMode.Src })
            {
                if (nativePixels)
                {
                    canvas.ClipRect(new SKRect(Bounds.Left,Bounds.Top,Bounds.Right,Bounds.Bottom));
                    canvas.DrawImage(Pixels.ImageOf(decoded),Bounds.Left,Bounds.Top,paint);
                }
                else
                {
                    // A source rectangle alone can still sample technical padding through
                    // a cubic kernel. Give it an image whose edge is the content edge.
                    using var content = new SKBitmap();
                    if (!decoded.ExtractSubset(content, SKRectI.Create(ContentSize.Width, ContentSize.Height)))
                        throw new InvalidDataException("The generated content crop could not be read.");
                    using var image = SKImage.FromBitmap(content);
                    canvas.DrawImage(image, new SKRect(Bounds.Left, Bounds.Top, Bounds.Right, Bounds.Bottom),
                        new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
                }
            }
            Pixels.Invalidate(result);
            using (result)
            using (var context = request.Task == AiTaskKind.GenerativeExpand ? inputs.ExpandedContext() : Pixels.Clone(inputs.ContextImage))
                return AiSeamlessFinisher.Match(result, context, Mask, Bounds, plan.SeamWidth,
                    request.Task is not (AiTaskKind.Relight or AiTaskKind.Harmonize or AiTaskKind.ChangeBackground),
                    request.Task == AiTaskKind.GenerativeExpand && (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt == AiPromptDefaults.Expand)
                    || request.BlackEditRegion && request.Prompt == AiPromptDefaults.Expand,
                    alignPixels: request.AlignMaskedEdits && request.Task != AiTaskKind.GenerativeExpand,
                    keepGeneratedContext: request.Task == AiTaskKind.GenerativeFill);
        }
    }
    public void Dispose() { Mask.Dispose(); ConditioningImage?.Dispose(); }
}
