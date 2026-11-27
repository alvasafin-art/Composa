using Composa.AI;
using Composa.App.AI;
using Composa.Editing;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.App.Tests;

public class PromptRegionTests
{
    [Fact]
    public void Coordinates_follow_the_model_frame_including_padding()
    {
        var prompt = RegionPrompt.Coordinates("Edit", [new(1, 60, 20, 100, 50, "Blue")], new(10, 10, 110, 60), 0.8, 0.5, true);
        Assert.Contains("\"src_bbox\":[100,400,400,720]", prompt);
        Assert.Contains("<object_1>", prompt);
    }

    [Fact]
    public void Masked_regions_clip_to_the_crop_and_include_letterbox_offsets()
    {
        var request = new AiTaskRequest
        {
            Task = AiTaskKind.GenerativeFill,
            RegionDelivery = RegionDelivery.Coordinates,
            Regions = [new(1, 90, 50, 160, 80, "Edit this"), new(2, 10, 10, 20, 20, "Outside crop")]
        };
        var prompt = RegionPrompt.MaskedPrompt("Fill", request, new SKRectI(100, 40, 300, 140),
            SKRectI.Create(20, 10, 160, 80), new SKSizeI(200, 100));
        Assert.Contains("\"bbox\":[180,100,420,340]", prompt);
        Assert.DoesNotContain("Outside crop", prompt);
    }

    [Theory]
    [InlineData("flux3-image-klein", RegionDelivery.Coordinates)]
    [InlineData("qwen-image-2.1-turbo", RegionDelivery.Coordinates)]
    [InlineData("qwen-image-2.1-turbo", RegionDelivery.Overlay)]
    public void Native_masked_region_workflows_keep_both_selected_islands(string engine, RegionDelivery method)
    {
        var session = EditorSession.NewCanvas(480, 320, SKColors.White);
        session.SelectRect(new SKRect(124, 109, 154, 139));
        session.SelectRect(new SKRect(231, 157, 261, 187), SelectionMode.Add);
        var request = new AiTaskRequest
        {
            EngineId = engine, Task = AiTaskKind.GenerativeFill, Prompt = "Fill the two objects",
            RegionDelivery = method,
            Regions = [new(1, 124, 109, 154, 139, "First island"), new(2, 231, 157, 261, 187, "Second island"),
                       new(3, 445, 270, 465, 290, "Outside crop")],
            SelectionContextPercent = 30, Settings = new() { Width = 384, Height = 288 }
        };
        using var prepared = AiTaskInputPreparer.Prepare(session, request);
        using var native = new QwenImageInputs(prepared, request);
        var catalog = new EngineCatalog(Path.Combine(AppContext.BaseDirectory, "ai", "engines"));
        var pack = catalog.Find(engine)!;
        var flux = engine == Flux3Workflow.EngineId;
        var graph = native.Bind(catalog.ReadWorkflow(pack, pack.Workflow(pack.Binding(request.Task)!.Workflow)),
            native.Images.Keys.ToDictionary(key => key, key => key + ".png"), 73, flux);
        var prompt = graph[flux ? "prompt" : "conditioning"]!["inputs"]![flux ? "text" : "prompt"]!.GetValue<string>();
        Assert.NotEqual(session.Document.Bounds, native.Bounds);
        Assert.Contains("First island", prompt);
        Assert.Contains("Second island", prompt);
        Assert.DoesNotContain("Outside crop", prompt);
        if (method == RegionDelivery.Overlay)
        {
            Assert.Contains("blue rectangle", prompt);
            Assert.Contains("Remove all colored annotation lines", prompt);
            Assert.Equal(SKColors.White, prepared.ContextImage.GetPixel(124, 124));
            Assert.Equal(request.Regions[0].Ink.Color, prepared.SourceImage.GetPixel(124, 124));
        }
        else Assert.Contains(flux ? "\"src_bbox\"" : "\"bbox\"", prompt);
        var noise = native.Images["noiseMask"];
        byte At(int x, int y) => noise.GetPixel((x - native.Bounds.Left) * native.ContentSize.Width / native.Bounds.Width,
            (y - native.Bounds.Top) * native.ContentSize.Height / native.Bounds.Height).Alpha;
        Assert.Equal(255, At(138, 121));
        Assert.Equal(255, At(244, 172));
        Assert.Equal(0, At(190, 144));
        Assert.Equal("masked", graph["sample"]!["inputs"]!["latent_image"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Document_regions_round_trip_and_undo()
    {
        var session = EditorSession.NewCanvas(160, 100);
        var region = session.AddPromptRegion(new(10, 15, 90, 50))!;
        session.ChangePromptRegion(region with { Prompt = "Blue" });
        using var stream = new MemoryStream();
        ProjectFile.Write(session.Document, stream);
        stream.Position = 0;
        Assert.Equal("Blue", ProjectFile.Read(stream).PromptRegions.Single().Prompt);
        session.Undo();
        Assert.Equal("", session.Document.PromptRegions.Single().Prompt);
    }

    [Fact]
    public void Region_colors_do_not_repeat_and_bulk_delete_is_one_undo_step()
    {
        var session = EditorSession.NewCanvas(160, 120);
        var regions = Enumerable.Range(1, 1000).Select(number => new PromptRegion(number, 10, 20, 90, 80)).ToArray();
        Assert.Equal(regions.Length, regions.Select(region => region.Ink.Color).Distinct().Count());
        Assert.Equal(regions.Length, regions.Select(region => region.Ink.Name).Distinct().Count());
        session.AddPromptRegion(new(10, 20, 90, 80));
        session.AddPromptRegion(new(20, 30, 40, 60));
        session.DeleteAllPromptRegions();
        Assert.Empty(session.Document.PromptRegions);
        session.Undo();
        Assert.Equal(2, session.Document.PromptRegions.Count);
    }

    public static bool Live => Environment.GetEnvironmentVariable("COMPOSA_REGION_IMAGE") != null;

    [Fact(Skip = "Set COMPOSA_REGION_IMAGE, COMPOSA_REGION_URL and COMPOSA_REGION_OUTPUT.", SkipUnless = nameof(Live))]
    public async Task Compare_Flux_then_Qwen_on_the_same_two_regions()
    {
        var output = Environment.GetEnvironmentVariable("COMPOSA_REGION_OUTPUT")!;
        var url = Environment.GetEnvironmentVariable("COMPOSA_REGION_URL")!;
        using var input = ImageFiles.Load(Environment.GetEnvironmentVariable("COMPOSA_REGION_IMAGE")!);
        Directory.CreateDirectory(output);
        foreach (var engine in new[] { Flux3Workflow.EngineId, QwenImageWorkflow.EngineId })
        {
            var doc = new Document(768, 512);
            var photo = Pixels.NewColor(768, 512);
            using (var canvas = new SKCanvas(photo))
                canvas.DrawImage(Pixels.ImageOf(input), SKRect.Create(768,512), new SKSamplingOptions(SKFilterMode.Linear));
            Pixels.Invalidate(photo);
            doc.Layers.Add(Layer.Raster("Original", photo));
            doc.SelectedLayerIds.Add(doc.Layers[0].Id);
            var session = new EditorSession(doc);
            var service = new AiTaskService(() => url, Path.Combine(AppContext.BaseDirectory, "ai/engines"));
            service.SelectedEngine = service.Engines.Find(engine);
            var request = new AiTaskRequest { EngineId = engine, Task = AiTaskKind.ImageEdit, Prompt = "Keep the original lighting, background and object positions.",
                Regions = [new(1, 30, 137, 397, 373, "Coat the object in deep cobalt blue glaze."), new(2, 468, 182, 722, 371, "Paint narrow green horizontal stripes on the object.")],
                RegionDelivery = engine == Flux3Workflow.EngineId ? RegionDelivery.Coordinates : RegionDelivery.Overlay,
                Settings = new() { Width = 768, Height = 512, Seed = 73, Values = new() { ["imageOriginalSize"] = true } } };
            await service.RunAsync(new EditorCommandService(session), request, TestContext.Current.CancellationToken);
            Assert.Equal(AiOperationStatus.Completed, service.Operation!.Status);
            using var result = DocumentRenderer.Flatten(session.Document);
            ImageFiles.Save(result, Path.Combine(output, engine + ".png"), ExportFormat.Png);
        }
    }
}
