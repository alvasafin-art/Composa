using Composa.AI;
using Composa.App.AI;
using Composa.Editing;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
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
