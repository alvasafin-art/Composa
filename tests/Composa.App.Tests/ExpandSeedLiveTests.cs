using System.Text.Json;
using Composa.AI;
using Composa.App.AI;
using Composa.Editing;
using Composa.IO;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

/// <summary>Opt-in GPU verification. Images and measurements remain outside the repository.</summary>
public class ExpandSeedLiveTests
{
    [Fact]
    public void Recorded_flux_strip_is_finished_without_resampling_the_model()
    {
        var path = Environment.GetEnvironmentVariable("COMPOSA_EXPAND_TEST_REPLAY"); if (string.IsNullOrWhiteSpace(path)) return;
        var blue = new SKColor(90, 165, 205); var s = EditorSession.NewCanvas(320, 240, blue); var state = s.History.CurrentId;
        var request = new AiTaskRequest { Task = AiTaskKind.GenerativeExpand, ExpansionBounds = new(0, 0, 328, 240), ExpansionMinimumSide = 640,
            Settings = new() { Width = 512, Height = 512, Values = new() { ["imageOriginalSize"] = false } } };
        using var inputs = AiTaskInputPreparer.Prepare(s, request);
        var catalog = new EngineCatalog(Path.Combine(AppContext.BaseDirectory, "ai", "engines")); var pack = catalog.Find("flux2-klein-intel-xpu")!; var binding = pack.Binding(request.Task)!;
        var values = inputs.Values(inputs.Images().ToDictionary(pair => pair.Key, pair => pair.Key + ".png"));
        foreach (var (key, value) in request.Settings.Values) values[key] = value;
        var graph = WorkflowBinder.Bind(catalog.ReadWorkflow(pack, pack.Workflow(binding.Workflow)), binding, values);
        WorkflowExecution.MaskedEdit(graph, inputs, request, new()); using var edit = new EditableMaskedWorkflow(inputs, request); edit.Bind(graph);
        var result = edit.Finish(ImageFiles.Load(path));
        AiTaskService.Insert(new EditorCommandService(s), request.Task, AiOutputMode.NewLayerWithMask, [result], inputs.TargetBounds, inputs, true, localOutputMask: edit.Mask);
        using var composite = s.Flatten(); Assert.Equal((328, 240), (composite.Width, composite.Height));
        ImageFiles.Save(composite, Path.Combine(Environment.GetEnvironmentVariable("COMPOSA_EXPAND_TEST_OUTPUT")!, "thin-crop-finished.png"), ExportFormat.Png);
        for (var y = 0; y < composite.Height; y++) for (var x = 0; x < composite.Width; x++)
        {
            var pixel = composite.GetPixel(x, y);
            if (edit.Mask.GetPixel(x, y).Alpha == 0) Assert.Equal(blue, pixel);
            else { Assert.InRange(pixel.Red, 89, 91); Assert.InRange(pixel.Green, 164, 166); Assert.InRange(pixel.Blue, 204, 206); Assert.Equal(255, pixel.Alpha); }
        }
        s.Undo(); Assert.Equal(state, s.History.CurrentId); s.Redo(); Assert.Equal(328, s.Document.Width);
    }

    [Theory]
    [InlineData("large-black")]
    [InlineData("thin-selection")]
    [InlineData("thin-crop")]
    public async Task Flux_continuation_avoids_black_reference_and_preserves_original_pixels(string scenario)
    {
        var url = Environment.GetEnvironmentVariable("COMPOSA_EXPAND_TEST_URL"); if (string.IsNullOrWhiteSpace(url)) return;
        var folder = Environment.GetEnvironmentVariable("COMPOSA_EXPAND_TEST_OUTPUT")!; Directory.CreateDirectory(folder);
        var color = new SKColor(90, 165, 205); var s = EditorSession.NewCanvas(320, 240, color);
        var task = scenario == "large-black" ? AiTaskKind.RemoveObject : AiTaskKind.GenerativeExpand;
        if (scenario == "large-black") { s.AddShape(new(Composa.Model.ShapeKind.Rectangle, (uint)SKColors.Black, 0), new SKRect(45, 30, 275, 210)); s.SelectRect(new(45, 30, 275, 210)); }
        else if (scenario == "thin-selection") s.SelectRect(new(155, 25, 157, 215));
        var request = new AiTaskRequest { Task = task, Prompt = scenario == "large-black" ? "Continue the same smooth blue wall, no objects, preserve its blue color." : "",
            ExpansionBounds = scenario == "thin-crop" ? new SKRectI(0, 0, 328, 240) : null, ExpansionMinimumSide = 640,
            Settings = new() { Width = 512, Height = 512, Seed = 43, Values = new() { ["imageOriginalSize"] = false } } };
        var service = new AiTaskService(() => url, Path.Combine(AppContext.BaseDirectory, "ai", "engines"));
        using var before = s.Flatten(); var state = s.History.CurrentId;
        ImageFiles.Save(before, Path.Combine(folder, scenario + "-before.png"), ExportFormat.Png);
        await service.RunAsync(new EditorCommandService(s), request, TestContext.Current.CancellationToken);
        using var after = s.Flatten(); ImageFiles.Save(after, Path.Combine(folder, scenario + "-after.png"), ExportFormat.Png);
        var coverage = s.ActiveLayer!.Mask!; var sum = new double[3]; var squares = new double[3]; var count = 0;
        for (var y = 0; y < after.Height; y++) for (var x = 0; x < after.Width; x++)
        {
            if (coverage.GetPixel(x, y).Alpha == 0 && x < before.Width && y < before.Height) Assert.Equal(before.GetPixel(x, y), after.GetPixel(x, y));
            if (coverage.GetPixel(x, y).Alpha != 255) continue;
            var pixel = after.GetPixel(x, y); var rgb = new[] { pixel.Red, pixel.Green, pixel.Blue };
            for (var c = 0; c < 3; c++) { sum[c] += rgb[c]; squares[c] += rgb[c] * rgb[c]; } count++;
        }
        var mean = sum.Select(v => v / Math.Max(1, count)).ToArray();
        var noise = squares.Select((v, c) => Math.Sqrt(Math.Max(0, v / Math.Max(1, count) - mean[c] * mean[c]))).ToArray();
        File.WriteAllText(Path.Combine(folder, scenario + "-metrics.json"), JsonSerializer.Serialize(new { count, mean, noise }));
        Assert.True(count > 0); Assert.True(mean[2] - mean[0] > 35); Assert.True(mean[2] > 130);
        s.Undo(); Assert.Equal(state, s.History.CurrentId); using var restored = s.Flatten(); Assert.Equal(before.GetPixelSpan().ToArray(), restored.GetPixelSpan().ToArray());
        s.Redo(); using var redo = s.Flatten(); Assert.Equal(after.GetPixelSpan().ToArray(), redo.GetPixelSpan().ToArray());
    }

    [Fact]
    public async Task Native_seedvr2_uses_installed_models_and_applies_an_undoable_upscale()
    {
        var url = Environment.GetEnvironmentVariable("COMPOSA_SEED_TEST_URL"); if (string.IsNullOrWhiteSpace(url)) return;
        var folder = Environment.GetEnvironmentVariable("COMPOSA_EXPAND_TEST_OUTPUT")!; Directory.CreateDirectory(folder);
        using var photo = ImageFiles.Load(Path.Combine(Environment.GetEnvironmentVariable("COMPOSA_SEED_TEST_INPUT")!, "portrait.jpeg"));
        var source = Pixels.NewColor(96, 144); using (var canvas = new SKCanvas(source)) canvas.DrawImage(Pixels.ImageOf(photo), SKRect.Create(96, 144), new SKSamplingOptions(SKCubicResampler.Mitchell));
        var s = EditorSession.NewCanvas(96, 144); s.AddImageLayer("Photo", source, new(48, 72), fit: false); var state = s.History.CurrentId;
        var service = new AiTaskService(() => url, Path.Combine(AppContext.BaseDirectory, "ai", "engines"));
        if (Environment.GetEnvironmentVariable("COMPOSA_SEED_TEST_DIT") is { } dit && Environment.GetEnvironmentVariable("COMPOSA_SEED_TEST_VAE") is { } vae)
            service.ModelSelections = _ => service.Engines.ModelSlots(service.Engines.Find("seedvr2-native")!)
                .ToDictionary(slot => slot.Key, slot => slot.Kind == EngineAssetKind.Vae ? vae : dit);
        await service.RunAsync(new EditorCommandService(s), new() { Task = AiTaskKind.Upscale, EngineId = "seedvr2-native", Settings = new() { UpscaleFactor = 4, Seed = 43 } }, TestContext.Current.CancellationToken);
        Assert.Equal((384, 576), (s.Document.Width, s.Document.Height)); using var result = s.Flatten(); ImageFiles.Save(result, Path.Combine(folder, "seedvr2-native.png"), ExportFormat.Png);
        Assert.Equal(255, result.GetPixel(383, 575).Alpha);
        s.Undo(); Assert.Equal(state, s.History.CurrentId); s.Redo(); Assert.Equal((384, 576), (s.Document.Width, s.Document.Height));
    }
}
