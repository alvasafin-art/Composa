using System.Text.Json.Nodes;

namespace Composa.App.AI;

internal static class Flux3Workflow
{
    public const string EngineId = "flux3-image-klein";

    internal static void Bind(JsonObject graph, IReadOnlyDictionary<string, string> files,
        string prompt, long seed, int width, int height)
    {
        graph["prompt"]!["inputs"]!["text"] = prompt;
        var sampler = graph["sample"]!["inputs"]!;
        sampler["seed"] = seed;
        graph["latent"]!["inputs"]!["width"] = width;
        graph["latent"]!["inputs"]!["height"] = height;
        var references = new JsonObject
        {
            ["positive"] = new JsonArray("prompt", 0), ["negative"] = new JsonArray("negative", 0),
            ["vae"] = new JsonArray("vae", 0), ["resolution"] = "keep resolution"
        };
        var index = 0;
        foreach (var semantic in Enumerable.Range(1, 6).Select(i => "referenceImage" + i).Prepend("sourceImage"))
        {
            if (!files.TryGetValue(semantic, out var file)) continue;
            var id = "reference" + index;
            graph[id] = new JsonObject { ["class_type"] = "LoadImage", ["inputs"] = new JsonObject { ["image"] = file } };
            references["images.image" + index++] = new JsonArray(id, 0);
        }
        if (index == 0) return;
        graph["references"] = new JsonObject { ["class_type"] = "ReferenceMultiLatent", ["inputs"] = references };
        sampler["positive"] = new JsonArray("references", 0);
        sampler["negative"] = new JsonArray("references", 1);
    }

    internal static void BindScene(JsonObject graph, IReadOnlyDictionary<string, string> files,
        string prompt, long seed, int width, int height)
    {
        if (!files.ContainsKey("sourceImage")) throw new InvalidDataException("Scene editing requires a source image.");
        Bind(graph, files, prompt, seed, width, height);
        // Reconstruct the same source through the VAE so registration and shadow
        // extraction can distinguish model edits from the VAE's pixel changes.
        graph["encodeBaseline"] = new JsonObject { ["class_type"] = "VAEEncode",
            ["inputs"] = new JsonObject { ["pixels"] = new JsonArray("reference0", 0), ["vae"] = new JsonArray("vae", 0) } };
        graph["decodeBaseline"] = new JsonObject { ["class_type"] = "VAEDecode",
            ["inputs"] = new JsonObject { ["samples"] = new JsonArray("encodeBaseline", 0), ["vae"] = new JsonArray("vae", 0) } };
        graph["baseline"] = new JsonObject { ["class_type"] = "SaveImage",
            ["inputs"] = new JsonObject { ["images"] = new JsonArray("decodeBaseline", 0), ["filename_prefix"] = "Composa/Flux3Klein/baseline" } };
    }
}
