using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Automation;
using Composa.App.Dialogs;
using Composa.Editing;
using SkiaSharp;

namespace Composa.App.Tests;

public class SmartObjectUiTests
{
    [AvaloniaFact]
    public void Contents_open_once_in_a_tab_and_save_updates_parent_shared_instances()
    {
        var window = new MainWindow { Width = 1280, Height = 850 }; window.Show();
        var parent = EditorSession.NewCanvas(400, 260, SKColors.CornflowerBlue); window.AddSession(parent);
        parent.ConvertToSmartObject(); parent.DuplicateSelectedLayers(); var objectLayer = parent.ActiveLayer!;
        var contents = window.OpenSmartObject(objectLayer);
        Assert.Equal(2, window.Sessions.Count); Assert.Same(contents, window.Session);
        contents.Fill(SKColors.OrangeRed);
        Assert.Equal(SKColors.CornflowerBlue, objectLayer.Pixels!.GetPixel(10, 10));
        Assert.True(Screenshots.Save(window, "smart-object-contents-tab"));
        var save = window.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "Save contents ↗");
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        Assert.False(contents.IsModified); Assert.True(parent.IsModified);
        Assert.All(parent.Document.Layers, layer => Assert.Equal(SKColors.OrangeRed, layer.Pixels!.GetPixel(10, 10)));
        var back = window.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "Back to parent");
        back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Same(parent, window.Session);
        Assert.Same(contents, window.OpenSmartObject(parent.ActiveLayer!)); Assert.Equal(2, window.Sessions.Count);
        var backAgain = window.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "Back to parent");
        backAgain.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        parent.Undo(); // Reopening an unmodified contents tab must follow the parent's undone source.
        Assert.Same(contents, window.OpenSmartObject(parent.ActiveLayer!));
        Assert.Equal(SKColors.CornflowerBlue, contents.ActiveLayer!.Pixels!.GetPixel(10, 10));
        Assert.False(contents.IsModified);
        parent.MarkEmbeddedSaved(); contents.MarkEmbeddedSaved(); window.Close();
    }

    [Fact]
    public void Script_smart_object_operations_are_real_and_document_description_exposes_the_kind()
    {
        var session = EditorSession.NewCanvas(80, 60, SKColors.White);
        new JavaScriptRuntime().Execute(session, "const layer=app.activeDocument.activeLayer.toSmartObject(); layer.duplicate(); layer.duplicateIndependent();");
        Assert.Equal(3, session.Document.Layers.Count);
        Assert.Equal(2, session.Document.Layers.Select(layer => layer.SmartObject!.Id).Distinct().Count());
        Assert.Contains("smartObject", JavaScriptRuntime.Describe(session));
        session.Undo(); Assert.Single(session.Document.Layers); Assert.False(session.ActiveLayer!.IsSmartObject);
    }

    [AvaloniaFact]
    public async Task Advanced_cancel_does_not_change_settings_and_upscale_has_only_two_factors()
    {
        var window = new MainWindow(); window.Show(); var settings = new Settings();
        var asking = AiDialogs.Advanced(window, settings); Dispatcher.UIThread.RunJobs();
        var dialog = Assert.Single(window.OwnedWindows);
        var combos = dialog.GetVisualDescendants().OfType<ComboBox>().ToArray();
        combos.Single(combo => combo.SelectedItem?.ToString() == "List · lower VRAM").SelectedIndex = 1;
        Assert.DoesNotContain(combos, combo => combo.Items.Cast<string>().SequenceEqual(new[] { "1", "2", "3" }));
        Assert.True(Screenshots.Save(dialog, "ai-advanced-variants"));
        dialog.Close(false); Assert.False(await asking); Assert.Equal(1, settings.AiVariants); Assert.Equal(Composa.AI.AiVariantMode.List, settings.AiVariantMode);
        var upscaling = AiDialogs.Upscale(window, settings, 57, 43, false); Dispatcher.UIThread.RunJobs();
        dialog = Assert.Single(window.OwnedWindows); var scale = dialog.GetVisualDescendants().OfType<ComboBox>().Single();
        Assert.Equal(new[] { "×2", "×4" }, scale.Items.Cast<string>());
        Assert.True(Screenshots.Save(dialog, "ai-upscale-two-factors"));
        dialog.Close(false); Assert.False(await upscaling); window.Close();
    }

    [AvaloniaFact]
    public async Task Prompt_advanced_updates_prompt_controls_without_overwriting_saved_options()
    {
        var window = new MainWindow(); window.Show(); var settings = new Settings();
        var asking = AiDialogs.Prompt(window, Composa.AI.AiTaskKind.GenerativeFill, settings, 320, 160); Dispatcher.UIThread.RunJobs();
        var prompt = Assert.Single(window.OwnedWindows);
        prompt.GetVisualDescendants().OfType<ComboBox>().Single(combo => combo.Width == 60).SelectedIndex = 1;
        prompt.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "Advanced…")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        var advanced = Assert.Single(prompt.OwnedWindows);
        advanced.GetVisualDescendants().OfType<ComboBox>().Single(combo => combo.SelectedItem?.ToString() == "List · lower VRAM").SelectedIndex = 1;
        advanced.Close(true); Dispatcher.UIThread.RunJobs();
        Assert.Equal("2", prompt.GetVisualDescendants().OfType<ComboBox>().Single(combo => combo.Width == 60).SelectedItem);
        prompt.Close(true); Assert.NotNull(await asking); Assert.Equal(2, settings.AiVariants); Assert.Equal(Composa.AI.AiVariantMode.Batch, settings.AiVariantMode); window.Close();
    }
}
