using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.AI;
using Composa.App.AI;
using Composa.App.Dialogs;
using Composa.Editing;
using SkiaSharp;
using SelectionMode = Composa.Selections.SelectionMode;

namespace Composa.App.Tests;

public sealed class ImageOperationRoutesTests
{
    [AvaloniaFact]
    public async Task The_image_catalog_preserves_the_legacy_model_without_a_duplicate_default_field()
    {
        var owner = new MainWindow { Width = 1280, Height = 720 };
        owner.Show();
        try
        {
            var settings = new Settings
            {
                ImageGenerationRoute = ImageGenerationRoute.DirectApi,
                ImageGenerationApiUrl = "http://localhost:1234/v1",
                ImageGenerationApiModel = "provider/image-model",
                ImageApiDisplayName = "Image API"
            };
            var pending = AiDialogs.SettingsDialog(owner, settings, owner.AiTasks, page: 5);
            if (pending.IsFaulted) await pending;
            var dialog = Assert.Single(owner.OwnedWindows);
            Dispatcher.UIThread.RunJobs();
            Assert.Single(dialog.GetVisualDescendants().OfType<TextBox>(), box => box.IsEffectivelyVisible && box.Text == "provider/image-model");
            Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<TextBlock>(), label => label.Text == Locale.T("Default model ID"));
            Assert.True(Screenshots.Save(dialog, "studio-settings-06-image-api-catalog-en"));
            dialog.Close(true);
            Assert.True(await pending);
            Assert.Equal("provider/image-model", settings.ImageGenerationApiModel);
            Assert.Contains(settings.ImageApiCatalogs.Values, models => models.ContainsKey("provider/image-model"));
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task Routes_are_saved_per_operation_and_keep_legacy_workflow_and_selection_defaults()
    {
        var owner = new MainWindow { Width = 1120, Height = 850 };
        owner.Show();
        try
        {
            var settings = new Settings { ImageGenerationRoute = ImageGenerationRoute.LocalCodex };
            Assert.Equal(ImageGenerationRoute.LocalCodex, settings.ImageRouteFor(AiTaskKind.GenerativeFill));
            Assert.Equal(ImageGenerationRoute.ComfyWorkflow, settings.ImageRouteFor(AiTaskKind.GenerativeExpand));
            Assert.Equal(ImageGenerationRoute.BuiltIn, settings.ImageRouteFor(AiTaskKind.SelectSubject));
            var pending = AiDialogs.SettingsDialog(owner, settings, owner.AiTasks, page: 6);
            var dialog = Assert.Single(owner.OwnedWindows);
            Dispatcher.UIThread.RunJobs();
            Assert.True(Screenshots.Save(dialog, "image-operation-connections"));
            SelectRoute(AiTaskKind.GenerativeExpand, ImageGenerationRoute.DirectApi);
            SelectRoute(AiTaskKind.Upscale, ImageGenerationRoute.LocalCodex);
            SelectRoute(AiTaskKind.ObjectSelection, ImageGenerationRoute.DirectApi);
            Assert.Empty(settings.ImageOperationRoutes);
            Assert.Empty(settings.ImageGenerationApiUrl);
            Assert.Empty(settings.ImageGenerationApiModel);
            Assert.Empty(settings.ImageGenerationCodexPath);
            dialog.Close(true);
            Assert.True(await pending);
            Assert.Equal(ImageGenerationRoute.DirectApi, settings.ImageRouteFor(AiTaskKind.GenerativeExpand));
            Assert.Equal(ImageGenerationRoute.LocalCodex, settings.ImageRouteFor(AiTaskKind.Upscale));
            Assert.Equal(ImageGenerationRoute.DirectApi, settings.ImageRouteFor(AiTaskKind.SelectSubject));
            Assert.Equal(ImageGenerationRoute.LocalCodex, settings.ImageRouteFor(AiTaskKind.GenerativeFill));
            Assert.Equal(ImageGenerationRoute.ComfyWorkflow, settings.ImageRouteFor(AiTaskKind.RemoveObject));

            var restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
            Assert.Equal(ImageGenerationRoute.DirectApi, restored.ImageRouteFor(AiTaskKind.SelectSubject));
            Assert.Equal(ImageGenerationRoute.LocalCodex, restored.ImageRouteFor(AiTaskKind.Upscale));

            void SelectRoute(AiTaskKind task, ImageGenerationRoute route)
            {
                dialog.GetVisualDescendants().OfType<Button>().Single(button => button.IsEffectivelyVisible &&
                    button.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == Locale.T(task.DisplayName())))
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                var field = dialog.GetVisualDescendants().OfType<ComboBox>().Single(combo => combo.Name == "ImageRoute" + task);
                field.SelectedItem = Locale.T(DirectImageGenerator.RouteLabel(route));
                Dispatcher.UIThread.RunJobs();
            }
        }
        finally { owner.Close(); }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Topaz_upscale_sends_the_selected_factor_and_the_original_png(int factor)
    {
        using var content = DirectImageApi.CreateUpscaleBody([1, 2, 3], factor);
        using var json = JsonDocument.Parse(await content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("topaz/image-upscale", json.RootElement.GetProperty("model").GetString());
        Assert.True(json.RootElement.GetProperty("async").GetBoolean());
        var input = json.RootElement.GetProperty("input");
        Assert.Equal(factor.ToString(System.Globalization.CultureInfo.InvariantCulture), input.GetProperty("upscale_factor").GetString());
        Assert.Equal("data:image/png;base64,AQID", Assert.Single(input.GetProperty("images").EnumerateArray()).GetProperty("data").GetString());
    }

    [Fact]
    public async Task Segmentation_requests_a_mask_and_uses_the_clicked_instance_in_document_coordinates()
    {
        var token = TestContext.Current.CancellationToken;
        using var content = DirectImageUtilities.CreateSegmentationBody([1, 2, 3]);
        using var json = JsonDocument.Parse(await content.ReadAsStringAsync(token));
        Assert.True(json.RootElement.GetProperty("mask_only").GetBoolean());
        Assert.True(json.RootElement.GetProperty("sync_mode").GetBoolean());
        Assert.Equal("data:image/png;base64,AQID", json.RootElement.GetProperty("image_url").GetString());

        var session = EditorSession.NewCanvas(96, 64, SKColors.CornflowerBlue);
        session.SelectRect(new(2, 2, 8, 8));
        var state = session.History.CurrentId;
        var request = new AiTaskRequest { Task = AiTaskKind.ObjectSelection, SelectionRegion = new(20, 10, 80, 50),
            SelectionPoint = new(30, 20), SelectionOperation = SelectionMode.Add };
        using var inputs = AiTaskInputPreparer.Prepare(session, request);
        using var generated = new SKBitmap(60, 40);
        using (var canvas = new SKCanvas(generated))
        {
            using var white = new SKPaint { Color = SKColors.White };
            canvas.Clear(SKColors.Black);
            canvas.DrawRect(SKRect.Create(5, 5, 15, 15), white);
            canvas.DrawRect(SKRect.Create(35, 22, 15, 10), white);
        }
        var coverage = DirectImageUtilities.MaskCoverage(generated, token);
        AiTaskService.Insert(new EditorCommandService(session), request.Task, AiOutputMode.Selection, [coverage], inputs.TargetBounds, inputs);
        Assert.True(session.HasForegroundSelection);
        Assert.Equal((byte)255, session.Selection!.GetPixel(30, 20).Alpha);
        Assert.Equal((byte)0, session.Selection.GetPixel(60, 35).Alpha);
        Assert.Equal((byte)255, session.Selection.GetPixel(4, 4).Alpha);
        Assert.Equal(SKColors.CornflowerBlue, session.Composite().GetPixel(30, 20));
        session.Undo();
        Assert.Equal(state, session.History.CurrentId);
        Assert.Equal((byte)0, session.Selection!.GetPixel(30, 20).Alpha);
    }
}
