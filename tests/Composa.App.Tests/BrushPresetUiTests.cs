using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.Editing;
using Composa.Painting;

namespace Composa.App.Tests;

public class BrushPresetUiTests
{
    [Fact]
    public void Updating_preserves_legacy_and_same_named_user_presets()
    {
        var settings = Settings.FromJson("""
            {
              "BrushPresets": { "Soft": { "Hardness": 0.45 }, "Soft round": { "Flow": 0.25 } },
              "EraserPresets": { "Hard": { "Size": 17 }, "Мягкая": { "Opacity": 0.3 } }
            }
            """);
        Assert.Equal(new BrushSettings { Hardness = .45 }, settings.BrushPresets["Soft"]);
        Assert.Equal(new BrushSettings { Flow = .25 }, settings.BrushPresets["Soft round"]);
        Assert.Equal(new BrushSettings { Size = 17 }, settings.EraserPresets["Hard"]);
        Assert.Equal(new BrushSettings { Opacity = .3 }, settings.EraserPresets["Мягкая"]);
        Assert.Equal(1, settings.BrushPresets["Soft"].TipRoundness);
        Assert.Equal(0, settings.EraserPresets["Hard"].TipAngle);
    }

    [AvaloniaTheory]
    [InlineData(Tool.Brush)]
    [InlineData(Tool.Eraser)]
    public async Task Tip_controls_save_and_restore_user_presets_with_built_in_names(Tool tool)
    {
        var previous = Locale.Language;
        Locale.Language = "ru";
        var window = new MainWindow { Width = 1440, Height = 850 };
        var session = EditorSession.NewCanvas(320, 240);
        try
        {
            window.Show();
            window.AddSession(session);
            window.SelectTool(tool);
            Button Picker(string name) => window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);
            StackPanel Open(string name, out Flyout flyout)
            {
                var button = Picker(name);
                flyout = Assert.IsType<Flyout>(button.Flyout);
                flyout.ShowAt(button);
                Dispatcher.UIThread.RunJobs();
                return Assert.IsType<StackPanel>(flyout.Content);
            }
            static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            static Button[] Entries(StackPanel panel) => Assert.IsType<StackPanel>(
                panel.Children.OfType<ScrollViewer>().Single().Content).Children.OfType<Button>().ToArray();

            var library = Open("BrushPresetPicker", out _);
            var factory = Entries(library);
            Assert.Equal(2, factory.Length);
            Click(factory[0]);
            Assert.Equal(BrushPresets.All[0].Settings, session.Brush);

            var tip = Open("BrushTipPicker", out var tipFlyout);
            var roundness = tip.Children.OfType<SliderField>().Single(f => f.Name == "BrushTipRoundness");
            roundness.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Left });
            var angle = tip.GetVisualDescendants().OfType<SliderField>().Single(f => f.Label == "Angle");
            angle.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
            Assert.Equal(.99, session.Brush.Roundness);
            Assert.Equal(1, session.Brush.Angle);
            tipFlyout.Hide();
            var saved = session.Brush with { Size = 75, Flow = .4, Opacity = .7 };
            session.Brush = saved;
            library = Open("BrushPresetPicker", out _);
            Click(library.Children.OfType<Button>().Single());
            Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(window.OwnedWindows);
            dialog.GetVisualDescendants().OfType<TextBox>().Single().Text = "Мягкая";
            Click(dialog.GetVisualDescendants().OfType<Button>().Single(b => b.IsDefault));
            var presets = tool == Tool.Eraser ? window.Settings.EraserPresets : window.Settings.BrushPresets;
            for (var i = 0; i < 50 && presets.Count == 0; i++)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
                Dispatcher.UIThread.RunJobs();
            }
            Assert.Equal(saved, presets["Мягкая"]);

            library = Open("BrushPresetPicker", out _);
            var entries = Entries(library);
            Assert.Equal(3, entries.Length);
            Click(entries[1]);
            Assert.Equal(BrushPresets.All[1].Settings, session.Brush);
            library = Open("BrushPresetPicker", out _);
            Click(Entries(library)[0]);
            Assert.Equal(BrushPresets.All[0].Settings, session.Brush);
            library = Open("BrushPresetPicker", out _);
            Click(Entries(library)[2]);
            Assert.Equal(saved, session.Brush);

            var reloaded = window.Settings.CopyPreferences();
            Assert.Equal(saved, (tool == Tool.Eraser ? reloaded.EraserPresets : reloaded.BrushPresets)["Мягкая"]);
            Assert.Empty(tool == Tool.Eraser ? reloaded.BrushPresets : reloaded.EraserPresets);
        }
        finally
        {
            Locale.Language = previous;
            session.MarkSaved("brush-tip.cmps", session.History.CurrentId);
            window.Close();
        }
    }
}
