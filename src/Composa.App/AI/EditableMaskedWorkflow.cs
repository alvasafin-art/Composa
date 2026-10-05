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
    private readonly AiTaskInputs inputs;
    private readonly AiTaskRequest request;
    private readonly int grow;
    private readonly int blend;
    private readonly int blur;
    private bool nativePixels;
    internal (int Width, int Height) ContentSize { get; private set; }
    internal (int Width, int Height) GenerationSize { get; private set; }

    internal EditableMaskedWorkflow(AiTaskInputs inputs, AiTaskRequest request)
    {
        this.inputs = inputs; this.request = request;
        grow = Value("maskGrow", 16);
        blend = Value("maskBlend", 48);
        blur = Value("maskBlur", 16);
        var sourceMask = request.Task == AiTaskKind.GenerativeExpand ? inputs.PreprocessedMask!
            : request.Task == AiTaskKind.ChangeBackground ? inputs.BackgroundMask! : inputs.PreprocessedMask ?? inputs.SelectionMask!;
        var area = request.Task == AiTaskKind.GenerativeExpand ? inputs.PreprocessedImage!.Info.Rect : inputs.ContextImage.Info.Rect;
        Bounds = AiContextGeometry.Flux(SelectionMask.Bounds(sourceMask, 1), area, grow, blend,
            Convert.ToDouble(request.Settings.Values.GetValueOrDefault("maskContext") ?? 2), blur);
        if (Bounds.IsEmpty) throw new InvalidOperationException("The edit mask is empty.");
        if (request.Task == AiTaskKind.GenerativeExpand)
        {
            using var context = inputs.ExpandedContext();
            Mask = AiResultPostprocessor.ExpansionEditMask(inputs.OutputMask ?? sourceMask, context, blend);
        }
        else if (request.Task == AiTaskKind.RemoveObject && inputs.OutputMask != null) Mask = Pixels.Clone(inputs.OutputMask);
        else Mask = AiResultPostprocessor.EditMask(sourceMask, request.Task == AiTaskKind.GenerativeFill ? 0 : grow, blend);
    }

    private int Value(string name, int fallback) => Math.Clamp(Convert.ToInt32(request.Settings.Values.GetValueOrDefault(name) ?? fallback), 0, 64);

    internal void Bind(JsonObject graph)
    {
        var pixaroma = graph["crop"]!["class_type"]!.GetValue<string>() == "PixaromaInpaintCrop";
        var crop = graph["crop"]!["inputs"]!.AsObject();
        var image = crop["image"]!.DeepClone(); var mask = crop["mask"]!.DeepClone();
        var original = Convert.ToBoolean(request.Settings.Values.GetValueOrDefault("imageOriginalSize") ?? false);
        nativePixels = request.Task == AiTaskKind.GenerativeExpand ? request.ExpansionMinimumSide == 0 : original;
        (ContentSize, GenerationSize) = AiContextGeometry.FluxSize(Bounds, inputs.TargetBounds, inputs.CanvasWidth, inputs.CanvasHeight,
            nativePixels, request.Task == AiTaskKind.GenerativeExpand ? request.ExpansionMinimumSide : null);
        var (width,height) = GenerationSize;
        JsonObject Node(string type, JsonObject values) => new() { ["class_type"] = type, ["inputs"] = values };
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
        if (blur > 0 && Math.Min(Bounds.Width,Bounds.Height) > 1)
        {
            // Blur is in source pixels, BEFORE resizing. Multiple Gaussian passes preserve
            // the requested variance without silently clipping ImageBlur's sigma/radius limits.
            var sigma = blur / 3.0; var passes = (int)Math.Ceiling(sigma * sigma / 100);
            sigma /= Math.Sqrt(passes);
            for (var i = 0; i < passes; i++)
            {
                var id = i == 0 ? "composa_edit_mask_blur" : $"composa_edit_mask_blur_{i}";
                graph[id] = Node("ImageBlur", new() { ["image"] = new JsonArray(last,0),
                    ["blur_radius"] = Math.Clamp((int)Math.Ceiling(3 * sigma), 1, Math.Min(31, Math.Min(Bounds.Width,Bounds.Height)-1)), ["sigma"] = sigma });
                last = id;
            }
        }
        if (!nativePixels)
        {
            graph["composa_edit_mask_size"] = Node("ImageScale", new() { ["image"] = new JsonArray(last,0),
                ["width"] = ContentSize.Width, ["height"] = ContentSize.Height, ["upscale_method"] = "bilinear", ["crop"] = "disabled" });
            last = "composa_edit_mask_size";
        }
        graph["composa_edit_mask"] = Node("ImageToMask", new() { ["image"] = new JsonArray(last,0), ["channel"] = "red" });
        graph["composa_edit_empty_mask"] = Node("SolidMask", new() { ["value"] = 0.0, ["width"] = width, ["height"] = height });
        graph["composa_edit_padded_mask"] = Node("MaskComposite", new() { ["destination"] = new JsonArray("composa_edit_empty_mask",0),
            ["source"] = new JsonArray("composa_edit_mask",0), ["x"] = 0, ["y"] = 0, ["operation"] = "add" });
        foreach (var (_, node) in graph)
            if (node?["inputs"] is JsonObject fields)
                foreach (var key in fields.Select(pair => pair.Key).ToArray())
                    if (fields[key] is JsonArray link && link[0]?.GetValue<string>() == "crop")
                    {
                        var slot = link[1]!.GetValue<int>();
                        fields[key] = new JsonArray(slot == (pixaroma ? 0 : 1) ? "composa_edit_size" : "composa_edit_padded_mask", 0);
                    }
        // Denoising coverage starts from positive source selection support, not blurred
        // amplitude: even a faint/tiny selection must receive a fully finished latent.
        graph["composa_noise_source"] = Node("ThresholdMask", new() { ["mask"] = new JsonArray("composa_edit_mask_crop",0), ["value"] = 0.001 });
        graph["composa_noise_image"] = Node("MaskToImage", new() { ["mask"] = new JsonArray("composa_noise_source",0) });
        var noiseImage = "composa_noise_image";
        if (!nativePixels)
        {
            graph["composa_noise_resize"] = Node("ImageScale", new() { ["image"] = new JsonArray(noiseImage,0),
                ["width"] = ContentSize.Width, ["height"] = ContentSize.Height, ["upscale_method"] = "nearest-exact", ["crop"] = "disabled" });
            noiseImage = "composa_noise_resize";
        }
        graph["composa_noise_mask"] = Node("ImageToMask", new() { ["image"] = new JsonArray(noiseImage,0), ["channel"] = "red" });
        graph["composa_noise_padded"] = Node("MaskComposite", new() { ["destination"] = new JsonArray("composa_edit_empty_mask",0),
            ["source"] = new JsonArray("composa_noise_mask",0), ["x"] = 0, ["y"] = 0, ["operation"] = "add" });
        graph["composa_sampling_mask"]!["inputs"]!["mask"] = new JsonArray("composa_noise_padded",0);
        // Sampling is larger than final coverage. Its halo/conditioning never becomes layer alpha.
        graph["composa_sampling_mask"]!["inputs"]!["expand"] = (int)Math.Ceiling((blend + 3 * blur)
            * Math.Max((double)ContentSize.Width / Bounds.Width, (double)ContentSize.Height / Bounds.Height));
        // Soft noise masks repeatedly mix unfinished noisy latents into a four-step edit.
        // Fully denoise its halo; only the independent source-space layer mask feathers once.
        // Comfy resizes noise masks to latent resolution. Binarizing only image pixels
        // would leave partially noisy cells at a thin edge or at technical padding.
        // Area pooling followed by threshold acts as max pooling over each 16x16 cell.
        graph["composa_sampling_image"] = Node("MaskToImage", new() { ["mask"] = new JsonArray("composa_sampling_mask",0) });
        graph["composa_latent_mask_image"] = Node("ImageScale", new() { ["image"] = new JsonArray("composa_sampling_image",0),
            ["width"] = width/16, ["height"] = height/16, ["upscale_method"] = "area", ["crop"] = "disabled" });
        graph["composa_latent_mask"] = Node("ImageToMask", new() { ["image"] = new JsonArray("composa_latent_mask_image",0), ["channel"] = "red" });
        graph["composa_denoise_mask"] = Node("ThresholdMask", new() { ["mask"] = new JsonArray("composa_latent_mask",0), ["value"] = 0.001 });
        // Klein edits use reference latents, not inpainting's extra concat channels.
        // Reuse the already encoded source instead of encoding it twice more.
        var conditioning = graph["composa_condition"]!["inputs"]!;
        graph["sampler"]!["inputs"]!["positive"] = conditioning["positive"]!.DeepClone();
        graph["sampler"]!["inputs"]!["negative"] = conditioning["negative"]!.DeepClone();
        graph.Remove("composa_condition");
        graph["composa_noise_latent"] = Node("SetLatentNoiseMask", new() { ["samples"] = new JsonArray("sourceEncode",0), ["mask"] = new JsonArray("composa_denoise_mask",0) });
        graph["latent"]!["inputs"]!["samples"] = new JsonArray("composa_noise_latent",0);
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
                else canvas.DrawImage(Pixels.ImageOf(decoded), SKRect.Create(ContentSize.Width,ContentSize.Height), new SKRect(Bounds.Left, Bounds.Top, Bounds.Right, Bounds.Bottom),
                    new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
            }
            Pixels.Invalidate(result);
            if (request.Settings.Values.GetValueOrDefault("colorMatch")?.ToString() is "subtle" or "strong"
                && request.Task != AiTaskKind.ChangeBackground)
            {
                using var context = request.Task == AiTaskKind.GenerativeExpand ? inputs.ExpandedContext() : Pixels.Clone(inputs.ContextImage);
                var matched = AiResultPostprocessor.MatchRemoval(result,context,Mask,inputs.Seed,Bounds,
                    request.Settings.Values["colorMatch"]?.ToString() == "subtle" ? 0.5 : 1);
                result.Dispose(); return matched;
            }
            return result;
        }
    }
    public void Dispose() => Mask.Dispose();
}
