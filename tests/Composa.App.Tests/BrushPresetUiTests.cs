using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.Editing;
using Composa.Painting;
using SkiaSharp;

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
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.Name == "BrushTipPicker");
            static StackPanel Open(MainWindow host, out Flyout flyout)
            {
                var button = host.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "BrushPresetPicker");
                flyout = Assert.IsType<Flyout>(button.Flyout);
                flyout.ShowAt(button);
                Dispatcher.UIThread.RunJobs();
                return Assert.IsType<StackPanel>(flyout.Content);
            }
            static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            static void Press(SliderField field, Key key) =>
                field.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
            static SliderField Roundness(StackPanel panel) =>
                panel.GetVisualDescendants().OfType<SliderField>().Single(f => f.Name == "BrushTipRoundness");
            static SliderField Angle(StackPanel panel) =>
                panel.GetVisualDescendants().OfType<SliderField>().Single(f => f.Label == "Tip angle");
            static Button[] Entries(StackPanel panel) => Assert.IsType<StackPanel>(
                panel.Children.OfType<ScrollViewer>().Single().Content).Children.OfType<Button>().ToArray();

            var library = Open(window, out var flyout);
            var factory = Entries(library);
            Assert.Equal(2, factory.Length);
            Click(factory[1]);
            Dispatcher.UIThread.RunJobs();
            Assert.True(flyout.IsOpen);
            Assert.Equal(BrushPresets.All[1].Settings, session.Brush);

            var roundness = Roundness(library);
            Press(roundness, Key.Home);
            for (var i = 0; i < 15; i++) Press(roundness, Key.Right);
            var angle = Angle(library);
            for (var i = 0; i < 90; i++) Press(angle, Key.Right);
            Assert.Equal(.25, session.Brush.Roundness);
            Assert.Equal(90, session.Brush.Angle);
            var saved = session.Brush with { Size = 75, Flow = .4, Opacity = .7 };
            session.Brush = saved;
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

            library = Open(window, out flyout);
            var entries = Entries(library);
            Assert.Equal(3, entries.Length);
            Click(entries[0]);
            Assert.Equal(BrushPresets.All[0].Settings, session.Brush);
            Click(entries[2]);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(saved, session.Brush);
            Assert.Equal(25, Roundness(library).Value);
            Assert.Equal(90, Angle(library).Value);
            Assert.True(Screenshots.Save(window, tool == Tool.Eraser ? "eraser-presets-with-tip" : "brush-presets-with-tip"));
            flyout.Hide();

            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            Settings reloaded;
            try
            {
                Assert.True(window.Settings.SaveTo(path));
                reloaded = Settings.LoadFrom(path);
            }
            finally { File.Delete(path); }
            Assert.Equal(saved, (tool == Tool.Eraser ? reloaded.EraserPresets : reloaded.BrushPresets)["Мягкая"]);
            Assert.Empty(tool == Tool.Eraser ? reloaded.BrushPresets : reloaded.EraserPresets);

            var reopened = new MainWindow { Width = 1440, Height = 850 };
            var baseColor = tool == Tool.Eraser ? SKColors.White : SKColors.Transparent;
            var fresh = EditorSession.NewCanvas(320, 240, baseColor);
            try
            {
                reopened.Settings.Brush = reloaded.Brush;
                reopened.Settings.EraserBrush = reloaded.EraserBrush;
                reopened.Settings.BrushPresets = reloaded.BrushPresets;
                reopened.Settings.EraserPresets = reloaded.EraserPresets;
                reopened.Show();
                reopened.AddSession(fresh);
                reopened.SelectTool(tool);
                Assert.Equal(saved, fresh.Brush);
                var afterRestart = Open(reopened, out var restoredFlyout);
                var restored = Entries(afterRestart);
                Click(restored[1]);
                Assert.Equal(BrushPresets.All[1].Settings, fresh.Brush);
                Click(restored[2]);
                Assert.Equal(saved, fresh.Brush);
                restoredFlyout.Hide();

                Assert.True(fresh.BeginStroke(new(160.5f, 120.5f), out var reason), reason);
                fresh.EndStroke();
                var pixels = fresh.ActiveLayer!.Pixels!;
                Assert.Equal(baseColor.Alpha, pixels.GetPixel(178, 120).Alpha);
                Assert.NotEqual(baseColor.Alpha, pixels.GetPixel(160, 138).Alpha);
            }
            finally
            {
                fresh.MarkSaved("restored-tip.cmps", fresh.History.CurrentId);
                reopened.Close();
            }
        }
        finally
        {
            Locale.Language = previous;
            session.MarkSaved("brush-tip.cmps", session.History.CurrentId);
            window.Close();
        }
    }
}
