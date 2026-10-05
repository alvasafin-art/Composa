using System.Text.Json.Nodes;
using Composa.AI;

namespace Composa.App.AI;

internal static class WorkflowMemory
{
    internal static void Apply(JsonObject graph, string mode, ComfyServerInfo? server, ComfyServerCapabilities capabilities)
    {
        if (mode is not ("auto" or "reduced" or "standard")) throw new ArgumentException("Memory use must be auto, reduced or standard.");
        var reduced = mode == "reduced" || mode == "auto" && server?.Devices.Any(device => device.Contains("xpu", StringComparison.OrdinalIgnoreCase)) == true;
        if (!reduced) return;
        foreach (var (_, node) in graph)
        {
            if (node is not JsonObject obj || obj["inputs"] is not JsonObject fields) continue;
            var type = obj["class_type"]?.GetValue<string>();
            if (type is "CLIPLoader" or "DualCLIPLoader") fields["device"] = "cpu";
            if (type is not ("VAEEncode" or "VAEDecode")) continue;
            var tiled = type + "Tiled";
            if (!capabilities.NodeTypes.Contains(tiled)) throw new InvalidOperationException($"Lower VRAM requires the stock ComfyUI node {tiled}. Choose Standard memory use or update ComfyUI.");
            obj["class_type"] = tiled;
            fields["tile_size"] = 512; fields["overlap"] = 64;
            fields["temporal_size"] = 64; fields["temporal_overlap"] = 8;
        }
    }
}
