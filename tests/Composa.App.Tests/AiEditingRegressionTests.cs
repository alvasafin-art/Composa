using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.AI;
using Composa.App.AI;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.App.Tests;

public class AiEditingRegressionTests
{
    private static EngineCatalog Catalog() => new(Path.Combine(AppContext.BaseDirectory, "ai", "engines"));

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(6, false)]
    [InlineData(0, true)]
    [InlineData(6, true)]
    public void Gpt_default_fill_never_changes_any_pixel_outside_a_hard_or_soft_selection(int references, bool soft)
    {
        var session = EditorSession.NewCanvas(271, 189, SKColors.White);
        session.SelectRect(new SKRect(1, 35, 91, 119));
        if (soft) session.ApplyAiSelection(AiTaskKind.SelectSubject, SelectionMask.Feather(session.Selection!, 5));
        using var reference = Pixels.NewColor(23, 19);
        var request = new AiTaskRequest { Task = AiTaskKind.GenerativeFill,
            ReferenceImages = Enumerable.Repeat(reference, references).ToArray(), ReferenceMegapixels = null };
        using var inputs = AiTaskInputPreparer.Prepare(session, request);
        using var api = new PartnerImageInputs(inputs, request);
        var source = api.Images["apiSource"];
        var generated = Pixels.NewColor(source.Width * 2, source.Height * 2); generated.Erase(SKColors.CornflowerBlue);
        var result = api.Finish(generated);
        for (var y = 0; y < result.Height; y++) for (var x = 0; x < result.Width; x++)
            if (inputs.SelectionMask!.GetPixel(x, y).Alpha == 0) Assert.Equal(SKColors.White, result.GetPixel(x, y));
        Assert.Equal(SKColors.CornflowerBlue, result.GetPixel(45, 75));
        AiTaskService.Insert(new EditorCommandService(session), request.Task, AiOutputMode.NewLayerWithMask, [result], inputs.TargetBounds, inputs, true);
        using var displayed = session.Flatten();
        Assert.Equal(result.GetPixelSpan().ToArray(), displayed.GetPixelSpan().ToArray()); // no second soft mask
        session.Undo(); Assert.Single(session.Document.Layers);
    }

    [Fact]
    public void Gpt_rejects_a_square_answer_for_a_landscape_source_instead_of_stretching_the_mask()
    {
        var session = EditorSession.NewCanvas(320, 160, SKColors.White);
        using var inputs = AiTaskInputPreparer.Prepare(session, new() { Task = AiTaskKind.ImageEdit });
        using var api = new PartnerImageInputs(inputs, new() { Task = AiTaskKind.ImageEdit });
        Assert.Throws<InvalidDataException>(() => api.Finish(Pixels.NewColor(512, 512)));
        Assert.Equal(0, session.History.Count); Assert.Single(session.Document.Layers);
    }

    [Theory]
    [InlineData(79, 61)]
    [InlineData(1920, 1080)]
    [InlineData(2048, 2048)]
    [InlineData(30000, 10000)]
    [InlineData(1, 3)]
    [InlineData(2999, 1000)]
    [InlineData(1000, 2999)]
    public void Gpt_custom_sizes_respect_official_limits_and_preserve_proportions(int width, int height)
    {
        var (w, h) = PartnerImageSize.Plan(width, height);
        Assert.Equal(0, w % 16); Assert.Equal(0, h % 16);
        Assert.InRange(w, PartnerImageSize.MinimumEdge, PartnerImageSize.MaximumEdge);
        Assert.InRange(h, PartnerImageSize.MinimumEdge, PartnerImageSize.MaximumEdge);
        Assert.InRange((long)w * h, PartnerImageSize.MinimumPixels, PartnerImageSize.MaximumPixels);
        Assert.InRange((double)Math.Max(w, h) / Math.Min(w, h), 1, 3);
        Assert.InRange(Math.Abs((double)w / h / ((double)width / height) - 1), 0, 0.025);
    }

    [Fact]
    public void Generate_sends_custom_original_or_mp_dimensions_and_no_fake_source_or_mask()
    {
        var session = EditorSession.NewCanvas(1920, 1080); session.SelectRect(new SKRect(5, 5, 25, 25));
        foreach (var size in new[] { (1920, 1080), AiDimensions.FromMegapixels(0.75, 1920, 1080) })
        {
            var request = new AiTaskRequest { Task = AiTaskKind.GenerateImage, Settings = new() { Width = size.Item1, Height = size.Item2 } };
            using var inputs = AiTaskInputPreparer.Prepare(session, request); using var api = new PartnerImageInputs(inputs, request);
            var pack = Catalog().Find("chatgpt-image-2.5")!;
            var graph = api.Bind(Catalog().ReadWorkflow(pack, pack.Workflows[0]), pack, new Dictionary<string, string>(), 0);
            var node = graph["gpt"]!["inputs"]!;
            var expected = PartnerImageSize.Plan(size.Item1, size.Item2);
            Assert.Equal("Custom", node["model.size"]!.GetValue<string>());
            Assert.Equal(expected.Width, node["model.custom_width"]!.GetValue<int>());
            Assert.Equal(expected.Height, node["model.custom_height"]!.GetValue<int>());
            Assert.Empty(api.Images); Assert.Null(inputs.SelectionMask);
        }
        Assert.Throws<InvalidOperationException>(() => PartnerImageSize.Plan(4000, 1000));
    }

    [Theory]
    [InlineData(AiTaskKind.ImageEdit)]
    [InlineData(AiTaskKind.RemoveObject)]
    [InlineData(AiTaskKind.ChangeBackground)]
    [InlineData(AiTaskKind.Harmonize)]
    [InlineData(AiTaskKind.Relight)]
    [InlineData(AiTaskKind.GenerativeExpand)]
    public void Image_tasks_are_available_without_selection_and_full_edits_do_not_invent_a_mask(AiTaskKind task)
    {
        var session = EditorSession.NewCanvas(79, 61, SKColors.White);
        foreach (var pack in Catalog().Profiles) Assert.True(AiTaskAvailability.Resolve(session, pack, task).Available);
        Assert.False(AiTaskAvailability.Resolve(session, Catalog().Profiles[0], AiTaskKind.GenerativeFill).Available);
        if (task == AiTaskKind.GenerativeExpand) return;
        var request = new AiTaskRequest { Task = task, Prompt = "a red cup" };
        using var inputs = AiTaskInputPreparer.Prepare(session, request); using var api = new PartnerImageInputs(inputs, request);
        Assert.Single(api.Images); Assert.Contains("apiSource", api.Images.Keys); Assert.Null(inputs.SelectionMask);
        var generated = Pixels.NewColor(158, 122); generated.Erase(SKColors.CornflowerBlue);
        using var result = api.Finish(generated); Assert.Equal(SKColors.CornflowerBlue, result.GetPixel(0, 0));
    }

    [Theory]
    [InlineData(AiExpansionMode.MaskedRegion)]
    [InlineData(AiExpansionMode.WholeImage)]
    public void Expand_black_input_soft_mask_and_whole_image_mode_have_distinct_undoable_results(AiExpansionMode mode)
    {
        var session = EditorSession.NewCanvas(79, 61, SKColors.White);
        var request = new AiTaskRequest { Task = AiTaskKind.GenerativeExpand, ExpansionBounds = new(-11, -7, 91, 69),
            ExpansionMode = mode, ExpansionMinimumSide = mode == AiExpansionMode.WholeImage ? 0 : 1024 };
        using var inputs = AiTaskInputPreparer.Prepare(session, request); using var api = new PartnerImageInputs(inputs, request);
        Assert.Equal(SKColors.Black, inputs.PreprocessedImage!.GetPixel(0, 0));
        Assert.DoesNotContain("apiMask", api.Images.Keys); // expansion mask is local in both GPT modes
        Assert.Equal(mode == AiExpansionMode.WholeImage ? 102 : 1024, Math.Max(inputs.CanvasWidth, inputs.CanvasHeight));
        var source = api.Images["apiSource"]; var generated = Pixels.NewColor(source.Width * 2, source.Height * 2); generated.Erase(SKColors.CornflowerBlue);
        var result = api.Finish(generated);
        Assert.Equal((102, 76), (result.Width, result.Height));
        Assert.Equal(SKColors.CornflowerBlue, result.GetPixel(0, 0)); // no black matte / translucent hole
        Assert.Equal(mode == AiExpansionMode.WholeImage ? SKColors.CornflowerBlue : SKColors.White, result.GetPixel(30, 30));
        var history = session.History.Count;
        AiTaskService.Insert(new EditorCommandService(session), request.Task, AiOutputMode.NewLayer, [result], inputs.TargetBounds, inputs);
        Assert.Equal((102, 76), (session.Document.Width, session.Document.Height));
        Assert.Equal(history + 1, session.History.Count);
        session.Undo(); Assert.Equal((79, 61), (session.Document.Width, session.Document.Height)); Assert.Single(session.Document.Layers);
    }

    [Fact]
    public void Expand_inside_existing_canvas_targets_transparent_space_and_respects_a_selection()
    {
        var session = EditorSession.NewCanvas(120, 90);
        var image = Pixels.NewColor(60, 40); image.Erase(SKColors.White); session.AddImageLayer("small", image, fit: false);
        session.SelectRect(new SKRect(0, 0, 60, 90));
        using var inputs = AiTaskInputPreparer.Prepare(session, new() { Task = AiTaskKind.GenerativeExpand });
        using var api = new PartnerImageInputs(inputs, new() { Task = AiTaskKind.GenerativeExpand });
        var source = api.Images["apiSource"]; var generated = Pixels.NewColor(source.Width * 2, source.Height * 2); generated.Erase(SKColors.CornflowerBlue);
        using var result = api.Finish(generated);
        var values = inputs.PreprocessedMask!.GetPixelSpan(); Assert.Contains((byte)255, values.ToArray());
        for (var y = 0; y < 90; y++) for (var x = 0; x < 120; x++)
        {
            if (inputs.SourceImage.GetPixel(x, y).Alpha == 255 || x >= 60) Assert.Equal((byte)0, inputs.OutputMask!.GetPixel(x, y).Alpha);
            if (inputs.SourceImage.GetPixel(x, y).Alpha == 0)
            {
                Assert.Equal(SKColors.Black, inputs.PreprocessedImage!.GetPixel(x, y));
                Assert.Equal(x >= 60 ? (byte)0 : (byte)255, result.GetPixel(x, y).Alpha);
            }
        }
    }

    [AvaloniaFact]
    public async Task Generate_and_expand_dialogs_have_pack_picker_joined_variants_and_no_base_seed()
    {
        var window = new MainWindow { Width = 1280, Height = 900 }; window.Settings.CheckForUpdates = false; window.Show();
        window.AiTasks.SetConnectedForTests(PartnerImageTests.Capabilities());
        var settings = new Settings();
        foreach (var task in new[] { AiTaskKind.GenerateImage, AiTaskKind.GenerativeExpand })
        {
            var pending = AiDialogs.Prompt(window, task, settings, 1024, 768, service: window.AiTasks);
            Dispatcher.UIThread.RunJobs(); var dialog = Assert.Single(window.OwnedWindows);
            var text = dialog.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToArray();
            Assert.DoesNotContain("Seed", text); Assert.DoesNotContain("Variants", text);
            var split = dialog.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "AiGenerateSplit");
            Assert.Equal(26, split.Height);
            var packs = dialog.GetVisualDescendants().OfType<ComboBox>().Single(combo => combo.Items.Cast<string>().Contains("CHAT GPT 2.5"));
            Assert.Equal(2, packs.ItemCount);
            packs.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            Assert.True(window.AiTasks.SelectedEngine!.PaidApi);
            Assert.True(Screenshots.Save(dialog, task == AiTaskKind.GenerateImage ? "gpt-generate-preview6" : "gpt-expand-preview6"));
            if (task == AiTaskKind.GenerativeExpand)
            {
                var modes = dialog.GetVisualDescendants().OfType<ComboBox>().Single(combo => combo.Items.Cast<string>().Contains("Regenerate whole expanded image"));
                modes.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
                Assert.True(Screenshots.Save(dialog, "gpt-expand-whole-preview6"));
            }
            dialog.Close(false); Assert.Null(await pending); Assert.False(window.AiTasks.SelectedEngine!.PaidApi);
        }
        Assert.Equal(AiExpansionMode.MaskedRegion, settings.AiExpansionMode); window.Close();
    }

    [Fact]
    public void Per_pack_prompts_preserve_legacy_customizations_without_cross_pack_updates()
    {
        var settings = new Settings { ComfyAdditionalPrompt = "legacy custom", ComfyAdditionalPromptEnabled = false };
        Assert.Equal(new AiPromptSetting(false, "legacy custom"), settings.PromptFor("chatgpt-image-2.5"));
        settings.AiPackPrompts["chatgpt-image-2.5"] = new(true, "GPT custom");
        Assert.Equal("legacy custom", settings.PromptFor("flux2-klein-intel-xpu").Text);
        Assert.Equal("GPT custom", settings.PromptFor("chatgpt-image-2.5").Text);
        Assert.Contains("cr / $", new PartnerPriceEstimate(0.1, 0.2).Label);
        Assert.Equal("211 cr / $1.000", PartnerPricing.Reported(211));
    }
}
