using System.Text.Json.Nodes;
using Composa.Model;
using Composa.AI;

namespace Composa.App.AI;

/// <summary>Execution-only graph changes; the engine's output ids remain stable.</summary>
internal static class WorkflowExecution
{
    /// <summary>Match the user's mask-aware edit pipeline, keeping crop metadata tied to the ORIGINAL image.</summary>
    public static void MaskedEdit(JsonObject graph, AiTaskInputs inputs, AiTaskRequest request, ComfyServerCapabilities capabilities)
    {
        if (graph["crop"]?["class_type"]?.GetValue<string>() != "InpaintCropImproved"
            || graph["sampler"]?["class_type"]?.GetValue<string>() != "KSampler") return;
        var crop = graph["crop"]!["inputs"]!.AsObject();
        var sampler = graph["sampler"]!["inputs"]!.AsObject();
        var pixaroma = capabilities.NodeTypes.Contains("PixaromaInpaintCrop") && capabilities.NodeTypes.Contains("PixaromaInpaintStitch")
            && HasSolidCore(inputs.PreprocessedMask ?? inputs.SelectionMask);
        var blend = crop["mask_blend_pixels"]!.GetValue<int>();
        var grow = crop["mask_expand_pixels"]!.GetValue<int>();
        var blurPixels = request.Settings.Values.TryGetValue("maskBlur", out var blur) ? Math.Clamp(Convert.ToInt32(blur), 0, 64) : 4;
        var samplingMargin = blend;
        if (pixaroma)
        {
            var bounds = inputs.TargetBounds;
            var context = Math.Clamp((int)Math.Ceiling(Math.Max(bounds.Width, bounds.Height)
                * (crop["context_from_mask_extend_factor"]!.GetValue<double>() - 1) / 2), 0, 1024);
            context = Math.Max(context, 2 * blend + grow);
            var halo = Math.Max(context, blend) + grow;
            var width = Math.Min(inputs.ContextImage.Width, Math.Max(1, bounds.Width + 2 * halo));
            var height = Math.Min(inputs.ContextImage.Height, Math.Max(1, bounds.Height + 2 * halo));
            var pixels = (long)crop["output_target_width"]!.GetValue<int>() * crop["output_target_height"]!.GetValue<int>();
            var target = Math.Clamp((int)Math.Round(Math.Max(width, height) * Math.Sqrt((double)pixels / width / height) / 16) * 16, 64, 8192);
            if (request.Task == AiTaskKind.GenerativeExpand && request.ExpansionMinimumSide > 0)
                target = Math.Clamp((int)Math.Round((double)Math.Max(width, height) * request.ExpansionMinimumSide / Math.Min(width, height) / 16) * 16, 64, 8192);
            // Pixaroma also ensures a 256 px short side. Cover the stitch's SOURCE-pixel
            // feather in model pixels, including the blur tail, without growing its final mask.
            var scale = Math.Max((double)target / Math.Max(width, height), 256.0 / Math.Min(width, height));
            samplingMargin = (int)Math.Ceiling(blend * scale) + 3 * blurPixels;
            graph["crop"] = new JsonObject { ["class_type"] = "PixaromaInpaintCrop", ["inputs"] = new JsonObject
            {
                ["image"] = crop["image"]!.DeepClone(), ["mask"] = crop["mask"]!.DeepClone(),
                ["size_mode"] = "keep shape (long side)", ["target"] = target, ["multiple"] = 16,
                ["context_px"] = context, ["mask_grow"] = grow,
                ["mask_blur"] = blurPixels,
                ["softness"] = blend, ["blend_mode"] = "mask", ["invert_mask"] = false
            } };
            // Improved: metadata,image,mask. Pixaroma: image,mask,metadata.
            foreach (var (_, node) in graph)
                if (node?["inputs"] is JsonObject nodeInputs)
                    foreach (var (_, value) in nodeInputs)
                        if (value is JsonArray link && link.Count == 2 && link[0]?.GetValue<string>() == "crop")
                            link[1] = link[1]!.GetValue<int>() switch { 0 => 2, 1 => 0, 2 => 1, var slot => slot };
            var colorMatch = request.Settings.Values.TryGetValue("colorMatch", out var match) ? Convert.ToString(match) : "subtle";
            if (colorMatch is not ("off" or "subtle" or "strong")) throw new ArgumentException("Color match must be off, subtle or strong.");
            graph["stitch"] = new JsonObject { ["class_type"] = "PixaromaInpaintStitch", ["inputs"] = new JsonObject
            { ["image"] = new JsonArray("decode", 0), ["crop_info"] = new JsonArray("crop", 2),
                ["softness"] = blend, ["blend_mode"] = "mask", ["color_match"] = colorMatch } };
            // Do NOT wire the resized/blurred conditioning mask into Stitch: its full-resolution
            // original mask in crop_info gives one outward-only feather, without a second halo.
        }
        else
        {
            crop["mask_hipass_filter"] = 0; // do not clip a soft brush's low-coverage rim
            var bounds = inputs.TargetBounds;
            crop["context_from_mask_extend_factor"] = Math.Max(crop["context_from_mask_extend_factor"]!.GetValue<double>(),
                1 + 2.0 * (grow + 4 * blend) / Math.Max(1, Math.Min(bounds.Width, bounds.Height)));
            var factor = crop["context_from_mask_extend_factor"]!.GetValue<double>();
            if (request.Task == AiTaskKind.GenerativeExpand && request.ExpansionMinimumSide > 0)
            {
                var dimensions = AiDimensions.FromMinimumSide(request.ExpansionMinimumSide,
                    Math.Max(1, (int)Math.Min(inputs.ContextImage.Width, bounds.Width * factor)),
                    Math.Max(1, (int)Math.Min(inputs.ContextImage.Height, bounds.Height * factor)));
                crop["output_target_width"] = dimensions.Width; crop["output_target_height"] = dimensions.Height;
            }
            var scale = Math.Max(crop["output_target_width"]!.GetValue<int>() / Math.Max(1.0, Math.Min(inputs.ContextImage.Width, bounds.Width * factor)),
                crop["output_target_height"]!.GetValue<int>() / Math.Max(1.0, Math.Min(inputs.ContextImage.Height, bounds.Height * factor)));
            samplingMargin = (int)Math.Ceiling(blend * scale) + 3 * blurPixels;
        }
        var maskSlot = pixaroma ? 1 : 2;
        graph["composa_sampling_mask"] = new JsonObject { ["class_type"] = "GrowMask", ["inputs"] = new JsonObject
        { ["mask"] = new JsonArray("crop", maskSlot), ["expand"] = samplingMargin, ["tapered_corners"] = true } };
        if (request.Task == AiTaskKind.RemoveObject)
        {
            graph["composa_black_mask"] = new JsonObject { ["class_type"] = "ThresholdMask", ["inputs"] = new JsonObject
            { ["mask"] = new JsonArray("crop", maskSlot), ["value"] = 0.5 } };
            graph["blackPatch"]!["inputs"]!["mask"] = new JsonArray("composa_black_mask", 0);
        }
        // Start from encoded source pixels with an actual noise mask, not an empty latent that
        // regenerates the complete crop and shifts its texture/exposure outside the selection.
        graph["composa_condition"] = new JsonObject { ["class_type"] = "InpaintModelConditioning", ["inputs"] = new JsonObject
        { ["positive"] = sampler["positive"]!.DeepClone(), ["negative"] = sampler["negative"]!.DeepClone(),
            ["vae"] = new JsonArray("vae", 0), ["pixels"] = graph["sourceEncode"]!["inputs"]!["pixels"]!.DeepClone(),
            ["mask"] = new JsonArray("composa_sampling_mask", 0), ["noise_mask"] = true } };
        graph["latent"] = new JsonObject { ["class_type"] = "RepeatLatentBatch", ["inputs"] = new JsonObject
        { ["samples"] = new JsonArray("composa_condition", 2), ["amount"] = 1 } };
        sampler["positive"] = new JsonArray("composa_condition", 0);
        sampler["negative"] = new JsonArray("composa_condition", 1);
        // Drop only the AuraFlow schedule, not the upstream LoRA model chain.
        sampler["model"] = graph["sampling"]?["inputs"]?["model"]?.DeepClone() ?? new JsonArray("model", 0);
        sampler["sampler_name"] = "euler";
        // The installed Flux.2 Klein model already has its native sampling schedule. The old
        // AuraFlow shift override was unrelated to the user's working four-step Klein pipeline.
        graph.Remove("sampling");
    }

    private static bool HasSolidCore(SkiaSharp.SKBitmap? mask)
    {
        if (mask == null) return false;
        // Pixaroma uses >0.5 to find its crop and falls back to a whole-crop stitch
        // when there is no such core. A faint/tiny selection must NOT edit the whole image.
        var pixels = mask.GetPixelSpan();
        for (var y = 0; y < mask.Height; y++)
            foreach (var value in pixels.Slice(y * mask.RowBytes, mask.Width)) if (value >= 128) return true;
        return false;
    }

    public static void Batch(JsonObject graph, int count)
    {
        if (count == 1) return;
        var api = graph.Where(pair => pair.Value?["class_type"]?.GetValue<string>() == "OpenAIGPTImageNodeV2").ToArray();
        if (api.Length == 1) { api[0].Value!["inputs"]!["n"] = count; return; }
        var latents = graph.Where(pair => pair.Value?["class_type"]?.GetValue<string>() is
            "EmptyFlux2LatentImage" or "EmptyLatentImage" or "EmptySD3LatentImage").ToArray();
        var repeats = graph.Where(pair => pair.Value?["class_type"]?.GetValue<string>() == "RepeatLatentBatch").ToArray();
        if (latents.Length == 0 && repeats.Length == 1) repeats[0].Value!["inputs"]!["amount"] = count;
        else if (latents.Length != 1)
            throw new InvalidOperationException("This workflow does not support a native batch. Choose List in Advanced to generate variants sequentially.");
        else latents[0].Value!["inputs"]!["batch_size"] = count;
        // Crop/stitch metadata describes one source. Split the decoded batch before stitching,
        // reuse that metadata for each variant, and join only the final images for SaveImage.
        foreach (var (id, node) in graph.ToArray())
        {
            if (node?["class_type"]?.GetValue<string>() != "InpaintStitchImproved") continue;
            var image = node["inputs"]!["inpainted_image"]!.DeepClone();
            var ids = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var pick = Unique(graph, $"composa_pick_{i}");
                graph[pick] = new JsonObject { ["class_type"] = "ImageFromBatch", ["inputs"] = new JsonObject
                { ["image"] = image.DeepClone(), ["batch_index"] = i, ["length"] = 1 } };
                var stitch = Unique(graph, $"composa_stitch_{i}");
                graph[stitch] = node.DeepClone();
                graph[stitch]!["inputs"]!["inpainted_image"] = new JsonArray(pick, 0);
                ids.Add(stitch);
            }
            var last = ids[0];
            for (var i = 1; i < count; i++)
            {
                var join = Unique(graph, "composa_join");
                graph[join] = new JsonObject { ["class_type"] = "ImageBatch", ["inputs"] = new JsonObject
                { ["image1"] = new JsonArray(last, 0), ["image2"] = new JsonArray(ids[i], 0) } };
                last = join;
            }
            foreach (var (_, other) in graph)
                if (other?["inputs"] is JsonObject inputs)
                    foreach (var key in inputs.Select(p => p.Key).ToArray())
                        if (inputs[key] is JsonArray link && link.Count == 2 && link[0]?.GetValue<string>() == id)
                            inputs[key] = new JsonArray(last, 0);
            graph.Remove(id);
        }
    }

    public static void Loras(JsonObject graph, EngineProfile engine, IReadOnlyList<AiLora> settings, ComfyServerCapabilities server)
    {
        if (settings.Count > 3) throw new ArgumentException("At most three LoRAs can be configured.");
        var enabled = settings.Where(lora => lora.Enabled && lora.Strength != 0 && !string.IsNullOrWhiteSpace(lora.Name)).ToArray();
        if (enabled.Length == 0) return;
        if (!engine.Lora.Supported || enabled.Length > engine.Lora.Maximum)
            throw new InvalidOperationException($"{engine.DisplayName} does not support these LoRAs.");
        if (graph.Where(pair => pair.Value?["class_type"]?.GetValue<string>() == "UNETLoader").ToArray() is not [{ Key: var loader }])
            throw new InvalidOperationException("This pack has no unambiguous diffusion-model LoRA target.");
        const string type = "LoraLoaderModelOnly";
        if (!server.NodeTypes.Contains(type) || !server.ModelChoices.TryGetValue(type + ".lora_name", out var names))
            throw new InvalidOperationException("Refresh ComfyUI models; LoraLoaderModelOnly and its model list are required for LoRAs.");
        var consumers = graph.Where(pair => pair.Value?["inputs"] is JsonObject inputs && inputs.Any(input =>
            input.Value is JsonArray link && link.Count == 2 && link[0]?.GetValue<string>() == loader && link[1]?.GetValue<int>() == 0)).ToArray();
        var last = loader;
        foreach (var lora in enabled)
        {
            if (!double.IsFinite(lora.Strength) || lora.Strength < Math.Max(0, engine.Lora.MinimumStrength) || lora.Strength > Math.Min(3, engine.Lora.MaximumStrength))
                throw new ArgumentException("LoRA strength is outside this workflow pack's range.");
            var name = WorkflowModels.Resolve(lora.Name, names) ?? throw new InvalidOperationException($"LoRA is missing or ambiguous on the connected server: {lora.Name}");
            var id = Unique(graph, "composa_lora");
            graph[id] = new JsonObject { ["class_type"] = type, ["inputs"] = new JsonObject
                { ["model"] = new JsonArray(last, 0), ["lora_name"] = name, ["strength_model"] = lora.Strength } };
            last = id;
        }
        foreach (var (_, node) in consumers)
            foreach (var key in node!["inputs"]!.AsObject().Select(pair => pair.Key).ToArray())
                if (node["inputs"]![key] is JsonArray link && link.Count == 2 && link[0]?.GetValue<string>() == loader && link[1]?.GetValue<int>() == 0)
                    node["inputs"]![key] = new JsonArray(last, 0);
    }

    public static void Upscale(JsonObject graph, int factor, int width, int height)
    {
        if (factor is not (2 or 4)) throw new ArgumentException("Choose ×2 or ×4.");
        var w = (long)width * factor; var h = (long)height * factor;
        if (!DocumentLimits.FitsSurface(w, h))
            throw new InvalidOperationException($"The upscaled image exceeds the {DocumentLimits.MaxSide} px / {DocumentLimits.MaxSurfaceMegapixels} MP limit.");
        // Do not shrink the input to a fixed-scale model: that destroys fine source detail.
        // The stock upscaler tiles inference. Resize its intermediate (normally CPU) output,
        // before encoding/transferring it, so a ×2 request never downloads the entire ×4 image.
        foreach (var (_, node) in graph.ToArray())
        {
            if (node?["class_type"]?.GetValue<string>() != "SaveImage") continue;
            var resize = Unique(graph, "composa_upscale_size");
            graph[resize] = new JsonObject { ["class_type"] = "ImageScale", ["inputs"] = new JsonObject
            { ["image"] = node["inputs"]!["images"]!.DeepClone(), ["upscale_method"] = "lanczos",
                ["width"] = (int)w, ["height"] = (int)h, ["crop"] = "disabled" } };
            node["inputs"]!["images"] = new JsonArray(resize, 0);
        }
    }

    private static string Unique(JsonObject graph, string stem)
    {
        var id = stem;
        for (var i = 1; graph.ContainsKey(id); i++) id = stem + "_" + i;
        return id;
    }
}
