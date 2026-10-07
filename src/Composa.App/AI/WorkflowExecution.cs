using System.Text.Json.Nodes;
using Composa.Model;
using Composa.AI;

namespace Composa.App.AI;

/// <summary>Execution-only graph changes; the engine's output ids remain stable.</summary>
internal static class WorkflowExecution
{
    internal static void NativeFluxSchedule(JsonObject graph)
    {
        if (graph["sampler"]?["class_type"]?.GetValue<string>() != "KSampler") return;
        var sampler = graph["sampler"]!["inputs"]!;
        if (graph["sampling"]?["class_type"]?.GetValue<string>() == "ModelSamplingAuraFlow")
        {
            sampler["model"] = graph["sampling"]!["inputs"]!["model"]!.DeepClone();
            graph.Remove("sampling");
        }
        sampler["sampler_name"] = "euler";
        sampler["scheduler"] = "simple";
    }

    /// <summary>Match the user's mask-aware edit pipeline, keeping crop metadata tied to the ORIGINAL image.</summary>
    public static void MaskedEdit(JsonObject graph, AiTaskInputs inputs, AiTaskRequest request, ComfyServerCapabilities capabilities)
    {
        if (graph["crop"]?["class_type"]?.GetValue<string>() != "InpaintCropImproved"
            || graph["sampler"]?["class_type"]?.GetValue<string>() != "KSampler") return;
        var crop = graph["crop"]!["inputs"]!.AsObject();
        var sampler = graph["sampler"]!["inputs"]!.AsObject();
        // Crop/stitch is performed locally with stock nodes, regardless of optional packs.
        crop["mask_hipass_filter"] = 0;
        const int maskSlot = 2;
        graph["composa_sampling_mask"] = new JsonObject { ["class_type"] = "GrowMask", ["inputs"] = new JsonObject
        { ["mask"] = new JsonArray("crop", maskSlot), ["expand"] = 0, ["tapered_corners"] = true } };
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
