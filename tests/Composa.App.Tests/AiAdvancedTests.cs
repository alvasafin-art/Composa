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
        Assert.True(Screenshots.Save(local, "ai-advanced-loras")); local.Close(); Assert.False(await pending);
        window.AiTasks.SelectedEngine = window.AiTasks.Engines.Find("chatgpt-image-2.5");
        pending = AiDialogs.Advanced(window, settings, window.AiTasks); Dispatcher.UIThread.RunJobs();
        var paid = Assert.Single(window.OwnedWindows); var text = paid.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToArray();
        Assert.Contains("GPT quality", text); Assert.DoesNotContain("Seed", text); Assert.DoesNotContain("Color match", text);
        Assert.DoesNotContain(paid.GetVisualDescendants().OfType<CheckBox>(), check => check.Content as string == "Enable LoRAs");
        Assert.True(Screenshots.Save(paid, "ai-advanced-gpt")); paid.Close(); Assert.False(await pending);
        var session = EditorSession.NewCanvas(640, 420, SKColors.White); window.AddSession(session); session.SelectRect(new SKRect(100, 50, 250, 120));
        for (var i = 0; i < 6; i++) { var reference = new SKBitmap(32, 32); reference.Erase(new SKColor((byte)(i * 30), 120, 170)); window.AddAiReferenceForTests(reference); }
        Assert.True(Screenshots.Save(window, "ai-gpt-six-references"));
        Assert.Contains(window.AiFloatingPanel.GetVisualDescendants().OfType<TextBlock>(), block => block.Text?.StartsWith("Estimate ≈ $") == true);
        Assert.Contains(window.AiFloatingPanel.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Estimate ≈ $0.096–$0.169");
        Assert.DoesNotContain(window.AiFloatingPanel.GetVisualDescendants().OfType<Button>(), button => button.Content as string == "＋");
        window.Width = 900; Assert.True(Screenshots.Save(window, "ai-gpt-narrow-panel")); window.Close();
    }
}
