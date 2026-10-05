using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.AI;
using Composa.App.Dialogs;
using Composa.Editing;
using SkiaSharp;

namespace Composa.App.Tests;

public class AiAdvancedTests
{
    [AvaloniaFact]
    public async Task Flux_fill_advanced_uses_its_own_defaults_without_overwriting_expand_or_gpt()
    {
        var window = new MainWindow(); window.Settings.CheckForUpdates = false; window.Show();
        try
        {
            var settings = new Settings();
            window.AiTasks.SelectedEngine = window.AiTasks.Engines.Find("flux2-klein-intel-xpu");
            foreach (var task in new[] { AiTaskKind.GenerativeFill, AiTaskKind.GenerativeExpand })
            {
                var pending = AiDialogs.Advanced(window, settings, window.AiTasks, task); Dispatcher.UIThread.RunJobs();
                var dialog = Assert.Single(window.OwnedWindows);
                var values = dialog.GetVisualDescendants().OfType<Composa.App.Controls.SliderField>().Take(4).Select(field => Math.Round(field.Value, 6)).ToArray();
                Assert.Equal(task == AiTaskKind.GenerativeFill ? new[] { 4.0,8,4,1.2 } : new[] { 16.0,48,16,2 }, values);
                dialog.Close(true); Assert.True(await pending);
            }
            Assert.Equal((4,8,4,1.2), (settings.AiFluxFillMaskGrow,settings.AiFluxFillMaskBlend,settings.AiFluxFillMaskBlur,settings.AiFluxFillMaskContext));
            Assert.Equal((16,48,16,2.0), (settings.AiMaskGrow,settings.AiMaskBlend,settings.AiMaskBlur,settings.AiMaskContext));
            Assert.Equal((4,8,0), (settings.AiGptMaskGrow,settings.AiGptMaskBlend,settings.AiGptContextPadding));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Local_advanced_has_three_loras_and_paid_advanced_hides_local_model_controls()
    {
        var window = new MainWindow { Width = 1280, Height = 900 }; window.Settings.CheckForUpdates = false; window.Show();
        var settings = new Settings();
        var capabilities = PartnerImageTests.Capabilities() with
        {
            ModelChoices = new() { ["LoraLoaderModelOnly.lora_name"] = ["portrait.safetensors", "shared/style.safetensors"] }
        };
        window.AiTasks.SetConnectedForTests(capabilities);
        var pending = AiDialogs.Advanced(window, settings, window.AiTasks); Dispatcher.UIThread.RunJobs();
        var local = Assert.Single(window.OwnedWindows);
        Assert.Equal(3, local.GetVisualDescendants().OfType<ComboBox>().Count(combo => combo.Items.Cast<string>().Contains("portrait.safetensors")));
        var localText = local.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToArray();
        Assert.Contains("Mask context", localText); Assert.Contains("Mask conditioning blur", localText); Assert.DoesNotContain("GPT context padding", localText);
        Assert.True(Screenshots.Save(local, "ai-advanced-loras")); local.Close(); Assert.False(await pending);
        window.AiTasks.SelectedEngine = window.AiTasks.Engines.Find("chatgpt-image-2.5");
        pending = AiDialogs.Advanced(window, settings, window.AiTasks); Dispatcher.UIThread.RunJobs();
        var paid = Assert.Single(window.OwnedWindows); var text = paid.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToArray();
        Assert.Contains("GPT quality", text); Assert.DoesNotContain("Seed", text); Assert.DoesNotContain("Color match", text);
        Assert.Contains("GPT context padding", text); Assert.DoesNotContain("Mask context", text); Assert.DoesNotContain("Mask conditioning blur", text);
        Assert.DoesNotContain(paid.GetVisualDescendants().OfType<CheckBox>(), check => check.Content as string == "Enable LoRAs");
        Assert.True(Screenshots.Save(paid, "ai-advanced-gpt")); paid.Close(); Assert.False(await pending);
        var session = EditorSession.NewCanvas(640, 420, SKColors.White); window.AddSession(session); session.SelectRect(new SKRect(100, 50, 250, 120));
        window.Settings.AiTaskEngineIds[nameof(AiTaskKind.GenerativeFill)] = "chatgpt-image-2.5";
        for (var i = 0; i < 6; i++) { var reference = new SKBitmap(32, 32); reference.Erase(new SKColor((byte)(i * 30), 120, 170)); window.AddAiReferenceForTests(reference); }
        Assert.True(Screenshots.Save(window, "ai-gpt-six-references"));
        Assert.Contains(window.AiFloatingPanel.GetVisualDescendants().OfType<TextBlock>(), block => block.Text?.Contains(" cr / $") == true);
        Assert.Contains(window.AiFloatingPanel.GetVisualDescendants().OfType<TextBlock>(), block => block.Text?.StartsWith("≈ ") == true);
        Assert.DoesNotContain(window.AiFloatingPanel.GetVisualDescendants().OfType<Button>(), button => button.Content as string == "＋");
        window.Width = 800; Assert.True(Screenshots.Save(window, "ai-gpt-narrow-panel"));
        var workflow = window.AiFloatingPanel.GetVisualDescendants().OfType<ComboBox>().Single(combo => combo.Name == "AiPanelWorkflow");
        var at = workflow.TranslatePoint(default, window.AiFloatingPanel)!.Value;
        Assert.True(at.X + workflow.Bounds.Width <= window.AiFloatingPanel.Bounds.Width);
        window.Close();
    }
}
