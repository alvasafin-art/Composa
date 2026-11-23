using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.App.Dialogs;
using Composa.Rendering;

namespace Composa.App.Tests;

public sealed class EditorWelcomeTests
{
    [AvaloniaFact]
    public async Task Welcome_back_retains_choices_and_a_completed_version_is_remembered()
    {
        var app = Assert.IsType<App>(Application.Current);
        var language = Locale.Language;
        Locale.Language = "ru";
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        try
        {
            var show = window.ShowEditorWelcome("9.7.0");
            Pump();
            var dialog = Assert.Single(window.OwnedWindows.OfType<EditorWelcomeWindow>());
            Assert.Empty(Options(dialog));
            Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<Button>(), button => button.Name == "WelcomeBack");
            Assert.True(Screenshots.Save(dialog, "editor-welcome-intro"));

            Press(dialog, "WelcomeNext");
            AssertOptionsFit(dialog, 3);
            Choose(dialog, EditorTheme.DarkNeutral);
            Press(dialog, "WelcomeBack");
            Assert.Empty(Options(dialog));
            Press(dialog, "WelcomeNext");
            Assert.Equal(EditorTheme.DarkNeutral, Selected(dialog));
            Press(dialog, "WelcomeNext");
            AssertOptionsFit(dialog, 2);
            Choose(dialog, EditorLayoutPreset.Modern);
            Assert.True(Screenshots.Save(dialog, "editor-welcome-layout"));
            Press(dialog, "WelcomeNext");
            Choose(dialog, AiPanelPlacement.UnderSelection);
            Press(dialog, "WelcomeBack");
            Assert.Equal(EditorLayoutPreset.Modern, Selected(dialog));
            Press(dialog, "WelcomeBack");
            Assert.Equal(EditorTheme.DarkNeutral, Selected(dialog));
            Choose(dialog, EditorTheme.LightNeutral);
            Press(dialog, "WelcomeNext");
            Assert.Equal(EditorLayoutPreset.Modern, Selected(dialog));
            Press(dialog, "WelcomeNext");
            Assert.Equal(AiPanelPlacement.UnderSelection, Selected(dialog));
            Assert.True(Screenshots.Save(dialog, "editor-welcome-ai"));
            Press(dialog, "WelcomeNext");
            await show.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(new EditorSetup(EditorTheme.LightNeutral, EditorLayoutPreset.Modern, AiPanelPlacement.UnderSelection), window.ReadEditorSetup());
            var dock = window.GetVisualDescendants().OfType<SideDock>().Single();
            var layers = dock.Section("Layers").State;
            Assert.True(layers.Left);
            Assert.NotNull(layers.TabGroup);
            Assert.Equal(layers.TabGroup, dock.Section("Resources").State.TabGroup);
            Assert.False(dock.Section("Properties").State.Left);
            Assert.False(dock.Section("AI Generation").State.Visible);
            Assert.Equal("Bottom", window.Settings.ToolsPosition);
            Assert.Contains("9.7.0", Settings.FromJson(JsonSerializer.Serialize(window.Settings)).SeenWelcomeVersions);

            await window.ShowEditorWelcome("9.7.0").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(window.OwnedWindows);
            var update = window.ShowEditorWelcome("9.7.1");
            Pump();
            Assert.Single(window.OwnedWindows.OfType<EditorWelcomeWindow>()).Close();
            await update.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("9.7.1", window.Settings.SeenWelcomeVersions);
            Assert.Equal(EditorLayoutPreset.Modern, window.ReadEditorSetup().Layout);
        }
        finally
        {
            Locale.Language = language;
            app.ApplyTheme(EditorTheme.Classic);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Editor_preferences_apply_sidebar_and_layout_changes_and_preserve_explicit_gpu_choices()
    {
        var defaults = Settings.FromJson("{}");
        Assert.Equal(GpuAcceleration.CanEnable, defaults.UseEditorGpu);
        Assert.Equal(GpuAcceleration.CanEnable, defaults.UseRasterFilterGpu);
        var disabled = Settings.FromJson("""{"UseEditorGpu":false,"UseRasterFilterGpu":false}""");
        Assert.False(disabled.UseEditorGpu);
        Assert.False(disabled.UseRasterFilterGpu);

        var window = new MainWindow { Width = 1280, Height = 820 };
        window.Show();
        try
        {
            var pending = AiDialogs.SettingsDialog(window, window.Settings, window.AiTasks, page: 3);
            Pump();
            var dialog = Assert.Single(window.OwnedWindows);
            var pickers = dialog.GetVisualDescendants().OfType<ComboBox>().ToArray();
            pickers.Single(box => box.Name == "EditorLayoutPicker").SelectedIndex = (int)EditorLayoutPreset.Modern;
            pickers.Single(box => box.Name == "EditorAiPanelPicker").SelectedIndex = (int)AiPanelPlacement.Sidebar;
            dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, Locale.T("Save settings")))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(EditorLayoutPreset.Modern, window.ReadEditorSetup().Layout);
            Assert.Equal(AiPanelPlacement.Sidebar, window.ReadEditorSetup().AiPanel);
            Assert.False(window.Settings.Dock["AI Generation"].Left);
            Assert.Equal(window.Settings.Dock["Layers"].TabGroup, window.Settings.Dock["Resources"].TabGroup);
        }
        finally { window.Close(); }
    }

    private static RadioButton[] Options(Window dialog) => dialog.GetVisualDescendants().OfType<RadioButton>().ToArray();
    private static void AssertOptionsFit(Window dialog, int count)
    {
        Assert.Equal(count, Options(dialog).Length);
        var next = dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "WelcomeNext");
        var nextTop = next.TranslatePoint(default, dialog)!.Value.Y;
        foreach (var radio in Options(dialog))
        {
            var card = Assert.IsType<Border>(Assert.IsType<StackPanel>(radio.Parent).Parent);
            var bottom = radio.TranslatePoint(new Point(0, radio.Bounds.Height), card);
            Assert.InRange(bottom!.Value.Y, radio.Bounds.Height, card.Bounds.Height);
            Assert.True(card.TranslatePoint(new Point(0, card.Bounds.Height), dialog)!.Value.Y < nextTop);
        }
    }
    private static object? Selected(Window dialog) => Assert.Single(Options(dialog), radio => radio.IsChecked == true).Tag;
    private static void Choose(Window dialog, object choice) => Options(dialog).Single(radio => Equals(radio.Tag, choice)).IsChecked = true;
    private static void Press(Window dialog, string name)
    {
        dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
    }
    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
