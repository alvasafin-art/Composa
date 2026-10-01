using System.Text.Json.Nodes;
using Composa.Model;

namespace Composa.App.AI;

/// <summary>Execution-only graph changes; the engine's output ids remain stable.</summary>
internal static class WorkflowExecution
{
    public static void Batch(JsonObject graph, int count)
    {
        if (count == 1) return;
        var latents = graph.Where(pair => pair.Value?["class_type"]?.GetValue<string>() is
            "EmptyFlux2LatentImage" or "EmptyLatentImage" or "EmptySD3LatentImage").ToArray();
        if (latents.Length != 1)
            throw new InvalidOperationException("This workflow does not support a native batch. Choose List in Advanced to generate variants sequentially.");
        latents[0].Value!["inputs"]!["batch_size"] = count;
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
