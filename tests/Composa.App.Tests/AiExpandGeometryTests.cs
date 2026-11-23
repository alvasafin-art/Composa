using Composa.AI;
using Composa.App.AI;
using Composa.Editing;
using Composa.IO;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public sealed class AiExpandGeometryTests
{
    [Theory]
    [InlineData(0, 0, 19, 0)]
    [InlineData(0, 0, 0, 13)]
    [InlineData(0, 0, 19, 13)]
    [InlineData(17, 11, 0, 0)]
    public void Expansion_keeps_source_coordinates_and_does_not_feed_edge_stripes_to_flux(
        int left, int top, int right, int bottom)
    {
        const int width = 79, height = 61;
        var session = Pattern(width, height);
        using var source = session.Flatten();
        var state = session.History.CurrentId;
        var request = new AiTaskRequest
        {
            Task = AiTaskKind.GenerativeExpand, ExpansionBounds = new(-left, -top, width + right, height + bottom),
            ExpansionMinimumSide = 147, Prompt = "Continue the pattern", Settings = new() { Seed = 2042 }
        };
        using var inputs = AiTaskInputPreparer.Prepare(session, request);
        using var edit = new EditableMaskedWorkflow(inputs, request);
        var frame = SKRectI.Create(left, top, width, height);
        var plan = inputs.MaskPlan!;
        Assert.Null(edit.ConditioningImage);
        var conditioning = inputs.PreprocessedImage!;
        for (var y = 0; y < conditioning.Height; y++) for (var x = 0; x < conditioning.Width; x++)
        {
            var sourcePixel = frame.Contains(x, y);
            Assert.Equal(sourcePixel ? source.GetPixel(x - left, y - top) : SKColors.Black, conditioning.GetPixel(x, y));
            Assert.Equal(sourcePixel ? (byte)0 : (byte)255, inputs.OutputMask!.GetPixel(x, y).Alpha);
        }

        var catalog = new EngineCatalog(Path.Combine(AppContext.BaseDirectory, "ai", "engines"));
        var engine = catalog.Find("flux2-klein-intel-xpu")!;
        var binding = engine.Binding(request.Task)!;
        var files = inputs.Images().ToDictionary(pair => pair.Key, pair => pair.Key + ".png");
        var graph = WorkflowBinder.Bind(catalog.ReadWorkflow(engine, engine.Workflow(binding.Workflow)), binding, inputs.Values(files));
        WorkflowExecution.MaskedEdit(graph, inputs, request, new());
        edit.Bind(graph);
        Assert.Contains("black patches", graph["prompt"]!["inputs"]!["text"]!.GetValue<string>());
        Assert.Equal("source", graph["composa_edit_crop"]!["inputs"]!["image"]![0]!.GetValue<string>());
        Assert.Equal(plan.Bounds.Left, graph["composa_edit_crop"]!["inputs"]!["x"]!.GetValue<int>());
        Assert.Equal(plan.Bounds.Top, graph["composa_edit_mask_crop"]!["inputs"]!["y"]!.GetValue<int>());
        Assert.Equal("red", graph["mask"]!["inputs"]!["channel"]!.GetValue<string>());
        var reference = SKRectI.Intersect(plan.Bounds, frame);
        var referenceCrop = graph["composa_edit_reference_crop"]!["inputs"]!;
        Assert.Equal(reference.Left, referenceCrop["x"]!.GetValue<int>());
        Assert.Equal(reference.Top, referenceCrop["y"]!.GetValue<int>());
        Assert.Equal(reference.Width, referenceCrop["width"]!.GetValue<int>());
        Assert.Equal(reference.Height, referenceCrop["height"]!.GetValue<int>());
        Assert.Equal("composa_edit_reference_resize", graph["sourceEncode"]!["inputs"]!["pixels"]![0]!.GetValue<string>());
        Assert.Equal("composa_edit_size", graph["composa_condition"]!["inputs"]!["pixels"]![0]!.GetValue<string>());
        Assert.Equal(147, Math.Max(edit.ContentSize.Width, edit.ContentSize.Height));
        Assert.Equal(0, edit.GenerationSize.Width % 16);
        Assert.Equal(0, edit.GenerationSize.Height % 16);
        Assert.NotEqual(edit.ContentSize, edit.GenerationSize);

        // Padding is outside the content. Changing only its pixels cannot change the edit.
        using var control = edit.Finish(ProviderResponse(SKColors.Magenta));
        var result = edit.Finish(ProviderResponse(SKColors.Yellow));
        Assert.Equal(control.GetPixelSpan().ToArray(), result.GetPixelSpan().ToArray());
        AiTaskService.Insert(new EditorCommandService(session), request.Task, binding.OutputMode, [result], inputs.TargetBounds,
            inputs, outputIsComposited: true, localOutputMask: edit.Mask);
        using var flattened = session.Flatten();
        using var exported = SKBitmap.Decode(ImageFiles.Encode(flattened, ExportFormat.Png));
        Assert.Equal((width + left + right, height + top + bottom), (exported.Width, exported.Height));
        for (var y = 0; y < exported.Height; y++) for (var x = 0; x < exported.Width; x++)
        {
            if (!frame.Contains(x, y))
            {
                Assert.Equal(255, edit.Mask.GetPixel(x, y).Alpha);
                Assert.Equal(255, exported.GetPixel(x, y).Alpha);
                continue;
            }
            // The opaque source can only change within the bounded transition.
            var seam = plan.SeamWidth;
            if ((left == 0 || x >= frame.Left + seam) && (right == 0 || x < frame.Right - seam)
                && (top == 0 || y >= frame.Top + seam) && (bottom == 0 || y < frame.Bottom - seam))
                Assert.Equal(0, edit.Mask.GetPixel(x, y).Alpha);
            if (edit.Mask.GetPixel(x, y).Alpha == 0) Assert.Equal(source.GetPixel(x - left, y - top), exported.GetPixel(x, y));
        }
        session.Undo();
        Assert.Equal(state, session.History.CurrentId);
        using var restored = session.Flatten();
        Assert.Equal(source.GetPixelSpan().ToArray(), restored.GetPixelSpan().ToArray());

        SKBitmap ProviderResponse(SKColor padding)
        {
            var decoded = Pixels.NewColor(edit.GenerationSize.Width, edit.GenerationSize.Height);
            decoded.Erase(padding);
            for (var y = 0; y < edit.ContentSize.Height; y++) for (var x = 0; x < edit.ContentSize.Width; x++)
                decoded.SetPixel(x, y, x < edit.ContentSize.Width / 2 ? SKColors.Blue : SKColors.Green);
            return decoded;
        }
    }

    [Fact]
    public void Canvas_expansion_does_not_translate_the_decoded_rectangle()
    {
        var session = Pattern(79, 61);
        using var source = session.Flatten();
        var request = new AiTaskRequest
        {
            Task = AiTaskKind.GenerativeExpand, ExpansionBounds = new(0, 0, 98, 74),
            ExpansionMinimumSide = 98, AlignMaskedEdits = true
        };
        using var inputs = AiTaskInputPreparer.Prepare(session, request);
        using var edit = new QwenImageInputs(inputs, request);
        using var unaligned = new QwenImageInputs(inputs, request with { AlignMaskedEdits = false });
        Assert.Equal((98, 74), edit.ContentSize);
        using var decoded = Pixels.NewColor(edit.PaddedSize.Width, edit.PaddedSize.Height);
        decoded.Erase(SKColors.Magenta);
        for (var y = 0; y < 74; y++) for (var x = 0; x < 98; x++)
        {
            // A shifted source in the provider answer tempts registration to sample
            // past the bottom and right edges of the decoded content.
            var color = x >= 2 && y >= 2 && x < 81 && y < 63
                ? source.GetPixel(x - 2, y - 2) : new SKColor((byte)(2 * x), (byte)(3 * y), 60);
            decoded.SetPixel(x, y, color);
        }
        using var expected = unaligned.Finish(Pixels.Clone(decoded));
        using var result = edit.Finish(Pixels.Clone(decoded));
        Assert.Equal(expected.GetPixelSpan().ToArray(), result.GetPixelSpan().ToArray());
    }

    private static EditorSession Pattern(int width, int height)
    {
        var source = Pixels.NewColor(width, height);
        var random = new Random(2042);
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
            source.SetPixel(x, y, new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
        var session = EditorSession.NewCanvas(width, height);
        session.AddImageLayer("Pattern", source, new(width / 2f, height / 2f), fit: false);
        return session;
    }
}
