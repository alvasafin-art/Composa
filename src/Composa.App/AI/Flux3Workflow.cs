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
}
