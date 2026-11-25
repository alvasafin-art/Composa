using System.Text.Json.Nodes;
using Composa.AI;

namespace Composa.App.AI;

/// <summary>The official eight-step Turbo schedule, shared by generation and image editing.</summary>
internal static class QwenImageWorkflow
{
    internal const string EngineId = "qwen-image-2.1-turbo";
    internal const string HarmonizePrompt = "Object naturally integrated into the scene, matching lighting direction, realistic shadows and contact shadow, subtle ambient occlusion, consistent reflections, physically grounded, seamless photorealism";

    internal static void Configure(JsonObject graph, ComfyServerInfo? server, bool align = true)
    {
        // Kitchen's quantized kernels are an optimization for the tested NVIDIA setup.
        // Other devices use the server's ordinary PyTorch attention implementation.
        if (server?.Devices.Any(device => device.Contains("nvidia", StringComparison.OrdinalIgnoreCase)) != true)
            graph["attention"]!["inputs"]!["attention"] = "pytorch attention";
        // A replacement environment has no stable background correspondences for a homography.
        // Fitting it to the old scene stretches the new surfaces and can move the support edge.
        if (!align && graph.ContainsKey("align"))
        {
            graph["save"]!["inputs"]!["images"] = new JsonArray("decode", 0);
            graph.Remove("align");
        }
    }

    internal static void Bind(JsonObject graph, IReadOnlyDictionary<string, string> files, string prompt, long seed,
        int width = 0, int height = 0)
    {
        var conditioning = graph["conditioning"]!["inputs"]!.AsObject();
        conditioning["prompt"] = prompt;
        conditioning["negative_prompt"] = ""; // BasicGuider uses CFG 1; a negative prompt has no effect.
        conditioning["resolution"] = 0; // Keep source geometry; the caller pads to the native 32-pixel grid.
        foreach (var key in conditioning.Select(pair => pair.Key).Where(key => key.StartsWith("images.", StringComparison.Ordinal)).ToArray())
            conditioning.Remove(key);
        var number = 0;
        if (files.TryGetValue("sourceImage", out var source))
        {
            graph["source"]!["inputs"]!["image"] = source;
            conditioning[$"images.image_{++number}"] = new JsonArray("source", 0);
        }
        for (var index = 1; index <= 6; index++)
        {
            if (!files.TryGetValue("referenceImage" + index, out var file)) continue;
            var id = "qwen_reference_" + index;
            graph[id] = new JsonObject { ["class_type"] = "LoadImage", ["inputs"] = new JsonObject { ["image"] = file } };
            conditioning[$"images.image_{++number}"] = new JsonArray(id, 0);
        }
        graph["noise"]!["inputs"]!["noise_seed"] = seed;
        if (graph["latent"] is { } latent)
        {
            latent["inputs"]!["width"] = width;
            latent["inputs"]!["height"] = height;
        }
    }

    internal static string SubjectTarget(SceneEditInput scene)
    {
        var x=scene.SubjectBounds.MidX/(double)scene.Before.Width;
        var y=scene.SubjectBounds.MidY/(double)scene.Before.Height;
        var position=(y<.33?"upper ":y>.67?"lower ":"")+(x<.33?"left ":x>.67?"right ":y is >=.33 and <=.67?"central ":"");
        return "the "+position+"foreground object";
    }

    internal static string ScenePrompt(AiTaskRequest request, bool wholeImage = false, bool cleanup = false,string target="the foreground object")
    {
        if (cleanup) return SceneEditingWorkflow.CleanupPrompt(request);
        var settings = request.Scene.Normalized();
        var suspended = settings.SuspendedSubject && request.Task is AiTaskKind.Harmonize or AiTaskKind.MatchToScene;
        var prompt = request.Task switch
        {
            AiTaskKind.ChangeBackground => "Replace the background around the foreground subject. Keep the subject's position, shape and size. Create a natural support surface and contact shadow. " + request.Prompt,
            AiTaskKind.Relight when settings.Preset==RelightPreset.GoldenHour && settings.LightAngle==null =>
                "Relight " + (wholeImage ? "the image" : target) + " with warm sunlight from the upper-left corner. Its shadow falls diagonally toward the lower-right corner of the image. Keep its shape, position" + (wholeImage ? "." : " and the background.") + (string.IsNullOrWhiteSpace(request.Prompt) ? "" : " " + request.Prompt.Trim()),
            AiTaskKind.Relight => "Relight " + (wholeImage ? "the image. " : "the foreground subject. ") + settings.LightingInstruction + " " + request.Prompt,
            _ when suspended => "Adapt the suspended subject's visible faces and underside to the scene's lighting. Preserve its shape, position, clearance and existing attachment.",
            _ => (wholeImage ? "Match the image lighting naturally. " : "") + HarmonizePrompt
        };
        if (request.Task is AiTaskKind.Harmonize or AiTaskKind.MatchToScene && !string.IsNullOrWhiteSpace(request.Prompt))
            prompt += ". " + request.Prompt.Trim();
        if (request.Task != AiTaskKind.ChangeBackground && (!settings.AddShadows || settings.MatchMode != SceneMatchMode.LightAndShadows))
            prompt += ". Keep lighting changes on the subject; do not add shadows outside it";
        if (suspended && settings.AddShadows && settings.MatchMode == SceneMatchMode.LightAndShadows)
            prompt += ". " + SceneEditingWorkflow.SuspendedCastInstruction;
        if (settings.LightAngle is { } angle)
            prompt += FormattableString.Invariant($". Light comes from angle {angle:0.#} degrees counterclockwise from image right");
        var explicitAngle = settings.LightAngle ?? (request.Task == AiTaskKind.Relight && settings.Preset is not (RelightPreset.MatchScene or RelightPreset.GoldenHour)
            ? settings.Preset == RelightPreset.SoftboxRight ? 45d : settings.Preset is RelightPreset.SoftboxLeft or RelightPreset.WindowLight or RelightPreset.GoldenHour or RelightPreset.Neon ? 135d : 90d : (double?)null);
        if (!suspended && explicitAngle is { } degrees && settings.AddShadows && settings.MatchMode == SceneMatchMode.LightAndShadows)
        {
            var dx = -Math.Cos(degrees * Math.PI / 180); var dy = Math.Sin(degrees * Math.PI / 180);
            var direction = (dy > .3 ? "lower" : dy < -.3 ? "upper" : "") + (Math.Abs(dx) > .3 ? (Math.Abs(dy) > .3 ? "-" : "") + (dx > 0 ? "right" : "left") : "");
            prompt += ". Replace the original subject lighting; cast its shadow toward the " + direction + " of the image";
        }
        if (!string.IsNullOrWhiteSpace(request.AdditionalPrompt)) prompt += ". " + request.AdditionalPrompt.Trim();
        return prompt;
    }
}
