using System.Text.Json.Nodes;
using Composa.AI;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.AI;

/// <summary>Tiles the complete SeedVR2 pipeline, including DiT; VAE tiling alone does not bound attention memory.</summary>
internal static class SeedVr2Upscaler
{
    internal static int TileLimit(ComfyServerInfo? info)
    {
        var gpu = info?.Memory.FirstOrDefault(d => d.Type is not ("cpu" or "") && d.Total > 0);
        if (gpu is { Free: <= 0 }) return 256;
        var gb = gpu == null ? 0 : Math.Min(gpu.Total * .7, gpu.Free) / (1024.0 * 1024 * 1024);
        return gb switch { >= 18 => 1024, >= 11 => 768, >= 7 => 640, >= 4 => 512, > 0 => 384, _ => 512 };
    }

    internal static string[] Choices(ComfyServerCapabilities capabilities, string node, string input)
    {
        var definition = capabilities.NodeDefinitions.GetValueOrDefault(node);
        var field = definition?["input"]?["required"]?[input] ?? definition?["input"]?["optional"]?[input];
        var array = field?[0] as JsonArray ?? field?[1]?["options"] as JsonArray;
        return array?.Select(v => v?.GetValue<string>() ?? "").ToArray() ?? [];
    }

    internal static void Configure(JsonObject graph, int width, int height, int factor, ComfyServerInfo? info, ComfyServerCapabilities capabilities, int limit)
    {
        if (graph["model"]?["class_type"]?.GetValue<string>() == "UNETLoader")
        {
            graph["upscale"]!["inputs"]!["width"] = width * factor; graph["upscale"]!["inputs"]!["height"] = height * factor;
            foreach (var id in new[] { "encode", "decode" })
            { graph[id]!["inputs"]!["tile_size"] = Math.Min(512, Math.Max(128, limit / 2 / 32 * 32)); graph[id]!["inputs"]!["overlap"] = 64; }
            foreach (var (id, node) in graph) if (id.StartsWith("composa_upscale_size", StringComparison.Ordinal))
            { node!["inputs"]!["width"] = width * factor; node["inputs"]!["height"] = height * factor; }
            return;
        }
        var dit = graph["dit"]!["inputs"]!; var vae = graph["vae"]!["inputs"]!; var upscale = graph["upscale"]!["inputs"]!;
        var devices = Choices(capabilities, "SeedVR2LoadDiTModel", "device");
        var vaeDevices = Choices(capabilities, "SeedVR2LoadVAEModel", "device");
        var common = devices.Where(d => vaeDevices.Contains(d)).ToArray();
        var gpu = info?.Memory.FirstOrDefault(m => common.Contains(m.Type + ":" + m.Index));
        var device = gpu == null ? common.FirstOrDefault(d => d.StartsWith("cuda:") || d.StartsWith("xpu:") || d == "mps") ?? common.FirstOrDefault()
            : gpu.Type + ":" + gpu.Index;
        if (device == null) throw new InvalidOperationException("Refresh ComfyUI models. SeedVR2 must report compatible DiT/VAE devices; install or update ComfyUI-SeedVR2_VideoUpscaler.");
        dit["device"] = device; vae["device"] = device;
        var cpu = device == "cpu" || device == "mps";
        var totalBlocks = dit["model"]!.GetValue<string>().Contains("7b", StringComparison.OrdinalIgnoreCase) ? 36 : 32;
        // Offload all transformer blocks on small/unknown GPUs. Tile reduction cannot
        // make a resident 7B model fit; keep weights and activations separate.
        var freeGb = gpu == null ? 0 : Math.Min(gpu.Total * .7, gpu.Free) / (1024.0 * 1024 * 1024);
        dit["blocks_to_swap"] = cpu ? 0 : freeGb >= 18 ? totalBlocks / 2 : totalBlocks;
        dit["swap_io_components"] = !cpu;
        dit["offload_device"] = cpu ? "none" : "cpu"; vae["offload_device"] = cpu ? "none" : "cpu";
        dit["cache_model"] = false; vae["cache_model"] = false;
        vae["encode_tiled"] = true; vae["decode_tiled"] = true;
        var vaeTile = Math.Min(512, Math.Max(128, limit / 2 / 32 * 32));
        vae["encode_tile_size"] = vaeTile; vae["decode_tile_size"] = vaeTile;
        vae["encode_tile_overlap"] = 64; vae["decode_tile_overlap"] = 64;
        upscale["resolution"] = Math.Max(16, Math.Min(width, height) * factor);
        upscale["max_resolution"] = Math.Max(16, Math.Max(width, height) * factor);
        upscale["batch_size"] = 1; upscale["input_noise_scale"] = 0; upscale["latent_noise_scale"] = 0;
        foreach (var (id, node) in graph)
            if (id.StartsWith("composa_upscale_size", StringComparison.Ordinal))
            { node!["inputs"]!["width"] = width * factor; node["inputs"]!["height"] = height * factor; }
    }

    internal static IReadOnlyList<(SKRectI Core, SKRectI Source)> Tiles(int width, int height, int factor, int limit)
    {
        var halo = Math.Min(16, limit / factor / 8);
        var coreSide = Math.Max(32, limit / factor - halo * 2);
        var nx = (width + coreSide - 1) / coreSide; var ny = (height + coreSide - 1) / coreSide;
        var result = new List<(SKRectI, SKRectI)>();
        for (var y = 0; y < ny; y++) for (var x = 0; x < nx; x++)
        {
            var core = new SKRectI(x * width / nx, y * height / ny, (x + 1) * width / nx, (y + 1) * height / ny);
            var source = new SKRectI(Math.Max(0, core.Left - halo), Math.Max(0, core.Top - halo), Math.Min(width, core.Right + halo), Math.Min(height, core.Bottom + halo));
            result.Add((core, source));
        }
        return result;
    }

    internal static async Task<SKBitmap> RunAsync(IComfyConnection client, JsonObject template, SKBitmap source, int factor, long seed,
        ComfyServerInfo? info, ComfyServerCapabilities capabilities, IProgress<AiOperationState>? progress, CancellationToken cancellation)
    {
        if (factor is not (2 or 4)) throw new ArgumentException("Choose ×2 or ×4.");
        if (!DocumentLimits.FitsSurface((long)source.Width * factor, (long)source.Height * factor)) throw new InvalidOperationException("Upscale exceeds document limits.");
        info = await client.MemoryInfoAsync(cancellation) ?? info;
        var limit = TileLimit(info);
        while (true)
        {
            var output = Pixels.NewColor(source.Width * factor, source.Height * factor);
            try
            {
                var weights = new byte[checked(output.Width * output.Height)];
                var tiles = Tiles(source.Width, source.Height, factor, limit);
                for (var i = 0; i < tiles.Count; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var (core, area) = tiles[i];
                    using var patch = Pixels.NewColor(area.Width, area.Height);
                    using (var draw = new SKCanvas(patch)) draw.DrawImage(Pixels.ImageOf(source), -area.Left, -area.Top);
                    var graph = (JsonObject)template.DeepClone();
                    Configure(graph, patch.Width, patch.Height, factor, info, capabilities, limit);
                    var compatible = EngineCompatibility.CheckWorkflow(graph, capabilities);
                    if (!compatible.IsCompatible) throw new InvalidOperationException("SeedVR2: " + string.Join(", ", compatible.Missing));
                    graph["source"]!["inputs"]!["image"] = await client.UploadPngAsync("seedvr2-tile", patch, cancellation);
                    graph[graph.ContainsKey("sampler") ? "sampler" : "upscale"]!["inputs"]!["seed"] = seed;
                    var number = i + 1;
                    var tileProgress = new Progress<AiOperationState>(state => progress?.Report(state with
                    { Stage = $"SeedVR2 · tile {number}/{tiles.Count} · {limit} px · {state.Stage}", Status = state.Status == AiOperationStatus.Completed ? AiOperationStatus.Running : state.Status }));
                    var execution = await client.ExecuteAsync(graph, tileProgress, cancellation);
                    using (execution.History)
                    {
                        var image = execution.Images.Where(image => image.NodeId == "save").ToArray();
                        if (image.Length != 1) throw new InvalidDataException("SeedVR2 did not return one tile; no result was applied.");
                        using var decoded = await client.DownloadAsync(image[0], cancellation);
                        if (decoded.Width != patch.Width * factor || decoded.Height != patch.Height * factor)
                            throw new InvalidDataException("SeedVR2 tile dimensions differ from the requested geometry; no result was applied.");
                        await Task.Run(() => Blend(output, weights, decoded, core, area, factor), cancellation);
                    }
                }
                cancellation.ThrowIfCancellationRequested(); Pixels.Invalidate(output); return output;
            }
            catch (Exception error) when (limit > 256 && IsOutOfMemory(error) && !cancellation.IsCancellationRequested)
            {
                output.Dispose(); limit = Math.Max(256, limit / 2 / 32 * 32);
                progress?.Report(new() { Status = AiOperationStatus.Running, Stage = $"SeedVR2 · retrying with {limit} px tiles after insufficient VRAM" });
            }
            catch { output.Dispose(); throw; }
        }
    }

    internal static bool IsOutOfMemory(Exception error) => error is not OperationCanceledException &&
        (error.Message.Contains("out of memory", StringComparison.OrdinalIgnoreCase) || error.Message.Contains("OutOfMemoryError", StringComparison.OrdinalIgnoreCase)
        || error.Message.Contains("not enough memory", StringComparison.OrdinalIgnoreCase));

    private static void Blend(SKBitmap destination, byte[] weights, SKBitmap patch, SKRectI core, SKRectI area, int factor)
    {
        var pixels = destination.GetPixelSpan(); var input = patch.GetPixelSpan();
        static double Ramp(int coordinate, int start, int end, int coreStart, int coreEnd) => coordinate < coreStart
            ? (coordinate - start + .5) / Math.Max(1, coreStart - start) : coordinate >= coreEnd ? (end - coordinate - .5) / Math.Max(1, end - coreEnd) : 1;
        for (var y = 0; y < patch.Height; y++) for (var x = 0; x < patch.Width; x++)
        {
            var px = area.Left * factor + x; var py = area.Top * factor + y;
            var weight = Math.Max(1, (int)Math.Round(60 * Ramp(px, area.Left * factor, area.Right * factor, core.Left * factor, core.Right * factor)
                * Ramp(py, area.Top * factor, area.Bottom * factor, core.Top * factor, core.Bottom * factor)));
            var at = py * destination.Width + px; var old = weights[at]; var sum = old + weight;
            for (var c = 0; c < 4; c++)
                pixels[py * destination.RowBytes + px * 4 + c] = (byte)((pixels[py * destination.RowBytes + px * 4 + c] * old + input[y * patch.RowBytes + x * 4 + c] * weight + sum / 2) / sum);
            weights[at] = (byte)sum;
        }
    }
}
