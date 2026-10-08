using System.Text.Json;
using System.Text.Json.Nodes;
using Composa.AI;
using Composa.App.AI;
using Composa.Editing;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class SeedVr2Tests
{
    [Fact]
    public void Server_memory_and_seed_loader_models_are_parsed_without_guessing_device_types()
    {
        using var stats = JsonDocument.Parse("""{"devices":[{"name":"GPU","type":"xpu","index":1,"vram_total":12884901888,"vram_free":5368709120}]}""");
        var info = ComfyClient.ParseInfo(stats.RootElement);
        Assert.Equal("xpu", info.Memory[0].Type); Assert.Equal(1, info.Memory[0].Index); Assert.Equal(512, SeedVr2Upscaler.TileLimit(info));
        Assert.Equal(256, SeedVr2Upscaler.TileLimit(info with { Memory = [info.Memory[0] with { Free = 0 }] }));
        var catalog = new EngineCatalog(Path.Combine(AppContext.BaseDirectory, "ai", "engines")); var pack = catalog.Find("seedvr2-native")!;
        var graph = catalog.ReadWorkflow(pack, pack.Workflows.Single());
        var capabilities = new ComfyServerCapabilities { NodeDefinitions = new()
            { ["SeedVR2PostProcessing"] = System.Text.Json.Nodes.JsonNode.Parse("""{"input":{"required":{"color_correction_method":[["lab","wavelet","adain","none"]]}}}""")!.AsObject() } };
        SeedVr2Upscaler.Configure(graph, 96, 144, 4, info, capabilities, 512);
        Assert.Equal("adain", graph["postprocess"]!["inputs"]!["color_correction_method"]!.GetValue<string>());
        Assert.True(ComfyClient.AssetKind("model", "SeedVR2LoadDiTModel", out var dit)); Assert.Equal(EngineAssetKind.DiffusionModel, dit);
        Assert.True(ComfyClient.AssetKind("model", "SeedVR2LoadVAEModel", out var vae)); Assert.Equal(EngineAssetKind.Vae, vae);
    }

    [Theory]
    [InlineData(2, 256)] [InlineData(4, 256)] [InlineData(4, 768)]
    public void Tile_plan_covers_every_core_once_and_bounds_output_including_overlap(int factor, int limit)
    {
        var counts = new byte[801 * 133];
        foreach (var (core, source) in SeedVr2Upscaler.Tiles(801, 133, factor, limit))
        {
            Assert.InRange(source.Width * factor, 1, limit); Assert.InRange(source.Height * factor, 1, limit);
            Assert.True(source.Contains(core));
            for (var y = core.Top; y < core.Bottom; y++) for (var x = core.Left; x < core.Right; x++) counts[y * 801 + x]++;
        }
        Assert.All(counts, value => Assert.Equal(1, value));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Tiled_upscale_reassembles_pixels_retries_only_oom_and_commits_once(bool oom)
    {
        var s = EditorSession.NewCanvas(185, 143, SKColors.Coral);
        s.AddShape(new(Composa.Model.ShapeKind.Rectangle, (uint)SKColors.Blue, 0), new SKRect(31, 28, 104, 116));
        using var original = s.Flatten(); var state = s.History.CurrentId;
        using var connection = new Connection { FailFirst = oom };
        var service = new AiTaskService(() => "http://localhost:8188", Path.Combine(AppContext.BaseDirectory, "ai", "engines"), _ => connection);
        await service.RunAsync(new EditorCommandService(s), new() { Task = AiTaskKind.Upscale, EngineId = "seedvr2", Settings = new() { UpscaleFactor = 4, Seed = 17 } }, TestContext.Current.CancellationToken);
        Assert.Equal((740, 572), (s.Document.Width, s.Document.Height)); Assert.True(connection.Executions > 1);
        Assert.All(connection.Patches, p => Assert.InRange(Math.Max(p.Width, p.Height) * 4, 1, oom ? 512 : 512));
        using var actual = s.Flatten();
        for (var y = 0; y < actual.Height; y += 7) for (var x = 0; x < actual.Width; x += 7) Assert.Equal(original.GetPixel(x / 4, y / 4), actual.GetPixel(x, y));
        var completed = s.History.CurrentId; Assert.NotEqual(state, completed); s.Undo(); Assert.Equal(state, s.History.CurrentId);
        using var restored = s.Flatten(); Assert.Equal(original.GetPixelSpan().ToArray(), restored.GetPixelSpan().ToArray());
        s.Redo(); Assert.Equal(completed, s.History.CurrentId);
        Assert.Equal(oom ? 1 : 0, connection.OomCount);
    }

    [Fact]
    public async Task Cancelling_between_tiles_leaves_document_and_history_untouched()
    {
        var s = EditorSession.NewCanvas(185, 143, SKColors.Coral); var state = s.History.CurrentId;
        using var connection = new Connection { CancelOnSecond = true };
        var service = new AiTaskService(() => "http://localhost:8188", Path.Combine(AppContext.BaseDirectory, "ai", "engines"), _ => connection);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.RunAsync(new EditorCommandService(s), new() { Task = AiTaskKind.Upscale, EngineId = "seedvr2", Settings = new() { UpscaleFactor = 4 } }, TestContext.Current.CancellationToken));
        Assert.Equal(state, s.History.CurrentId); Assert.Equal((185, 143), (s.Document.Width, s.Document.Height)); Assert.Single(s.Document.Layers);
    }

    internal sealed class Connection : IComfyConnection
    {
        public ComfyServerAddress Address => ComfyServerAddress.Parse("http://localhost:8188");
        public TimeSpan ConnectionTimeout { get; set; }
        public bool FailFirst, CancelOnSecond;
        public int Executions, OomCount;
        public List<SKSizeI> Patches = [];
        private SKBitmap? patch;
        public Task<(ComfyServerInfo, ComfyServerCapabilities)> TestConnectionAsync(CancellationToken cancellationToken = default)
        {
            var definitions = new Dictionary<string, JsonObject>();
            foreach (var node in new[] { "SeedVR2LoadDiTModel", "SeedVR2LoadVAEModel" }) definitions[node] = JsonNode.Parse("""{"input":{"required":{"device":[["cuda:0","cpu"]]}}}""")!.AsObject();
            return Task.FromResult((new ComfyServerInfo("0.37.0", "Windows", "3.12", ["GPU"]) { Memory = [new("GPU", "cuda", 0, 8L << 30, 6L << 30)] },
                new ComfyServerCapabilities { NodeTypes = ["LoadImage","SaveImage","ImageScale","InvertMask","JoinImageWithAlpha","SeedVR2LoadDiTModel","SeedVR2LoadVAEModel","SeedVR2VideoUpscaler"], NodeDefinitions = definitions }));
        }
        public Task<string> UploadPngAsync(string semantic, SKBitmap image, CancellationToken cancellationToken = default)
        { Assert.Equal("seedvr2-tile", semantic); patch?.Dispose(); patch = Pixels.Clone(image); Patches.Add(new(image.Width, image.Height)); return Task.FromResult("tile.png"); }
        public Task<ComfyExecutionResult> ExecuteAsync(JsonObject graph, IProgress<AiOperationState>? progress = null, CancellationToken cancellationToken = default)
        {
            Executions++; Assert.Equal(32, graph["dit"]!["inputs"]!["blocks_to_swap"]!.GetValue<int>());
            Assert.True(graph["vae"]!["inputs"]!["decode_tiled"]!.GetValue<bool>()); Assert.Equal(1, graph["upscale"]!["inputs"]!["batch_size"]!.GetValue<int>());
            if (CancelOnSecond && Executions == 2) throw new OperationCanceledException();
            if (FailFirst && Executions == 1) { OomCount++; throw new InvalidOperationException("CUDA out of memory"); }
            return Task.FromResult(new ComfyExecutionResult("test", JsonDocument.Parse("{}"), [new("tile.png", "", "output", "save")]));
        }
        public Task<SKBitmap> DownloadAsync(ComfyImageReference image, CancellationToken cancellationToken = default)
        {
            var output = Pixels.NewColor(patch!.Width * 4, patch.Height * 4);
            using var canvas = new SKCanvas(output); canvas.DrawImage(Pixels.ImageOf(patch), new SKRect(0, 0, output.Width, output.Height), new SKSamplingOptions(SKFilterMode.Nearest)); return Task.FromResult(output);
        }
        public void Dispose() { patch?.Dispose(); patch = null; }
    }
}
