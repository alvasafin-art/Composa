using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.App.Dialogs;
using Composa.Rendering;

namespace Composa.App.Tests;

public sealed class EditorWelcomeTests
{
    [AvaloniaFact]
    public async Task Welcome_back_retains_choices_and_setup_is_remembered_for_the_profile()
    {
        var app = Assert.IsType<App>(Application.Current);
        var language = Locale.Language;
        Locale.Language = "ru";
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        try
        {
            var show = window.ShowEditorWelcome();
            Pump();
            var dialog = Assert.Single(window.OwnedWindows.OfType<EditorWelcomeWindow>());
            var preview = dialog.GetVisualDescendants().OfType<Image>().Single(image => image.Name == "WelcomePreview");
            var originalPreview = preview.Source;
            Choose(dialog, EditorLayoutPreset.Modern);
            Assert.NotSame(originalPreview, preview.Source);
            Choose(dialog, AiPanelPlacement.UnderSelection);
            var theme = dialog.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "WelcomeTheme");
            theme.SelectedIndex = (int)EditorTheme.LightNeutral;
            Assert.True(Screenshots.Save(dialog, "studio-settings-02-welcome-ru"));

            Press(dialog, "WelcomeNext");
            Pump();
            var connections = Assert.Single(window.OwnedWindows.OfType<StudioSettingsWindow>());
            Assert.True(Screenshots.Save(connections, "studio-settings-03-first-run-ru"));
            var note = Assert.Single(connections.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == Locale.T("You can finish setup later in Settings."));
            var primary = connections.FooterButtons.Children.OfType<Button>().Last();
            var noteCenter = note.TranslatePoint(new(0, note.Bounds.Height / 2), connections)!.Value.Y;
            var buttonCenter = primary.TranslatePoint(new(0, primary.Bounds.Height / 2), connections)!.Value.Y;
            Assert.Equal(buttonCenter, noteCenter, tolerance: 1);
            var serverDetails = Assert.Single(connections.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>(),
                button => Avalonia.Automation.AutomationProperties.GetName(button) == Locale.T("ComfyUI server"));
            var serverUrl = Assert.Single(connections.GetLogicalDescendants().OfType<TextBox>(),
                box => Avalonia.Automation.AutomationProperties.GetName(box) == Locale.T("ComfyUI Server URL"));
            Assert.False(serverUrl.IsEffectivelyVisible);
            serverDetails.IsChecked = true;
            Pump();
            Assert.True(serverUrl.IsEffectivelyVisible);
            serverDetails.IsChecked = false;
            Assert.Single(connections.GetVisualDescendants().OfType<ComboBox>().Where(box => box.Name == "TextEnhancerRoute"));
            connections.FooterButtons.Children.OfType<Button>().First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            dialog = Assert.Single(window.OwnedWindows.OfType<EditorWelcomeWindow>());
            Assert.Contains(Options(dialog), option => Equals(option.Tag, EditorLayoutPreset.Modern) && option.IsChecked == true);
            Assert.Contains(Options(dialog), option => Equals(option.Tag, AiPanelPlacement.UnderSelection) && option.IsChecked == true);
            Press(dialog, "WelcomeNext");
            Assert.Single(window.OwnedWindows.OfType<StudioSettingsWindow>()).Close(false);
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
            var saved = Settings.FromJson(JsonSerializer.Serialize(window.Settings));
            Assert.True(saved.EditorWelcomeSeen);
            Assert.Equal(EditorTheme.LightNeutral, saved.Theme);
            Assert.Equal("Bottom", saved.ToolsPosition);
            Assert.Equal(layers, saved.Dock["Layers"]);

            await window.ShowEditorWelcome().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(window.OwnedWindows);
            Assert.Equal(EditorLayoutPreset.Modern, window.ReadEditorSetup().Layout);
        }
        finally
        {
            Locale.Language = language;
            app.ApplyTheme(EditorTheme.Classic);
            window.Close();
        }
    }

    [Theory]
    [InlineData("""{"SeenWelcomeVersions":["17.7.0-preview.4"]}""", true)]
    [InlineData("""{"SeenWelcomeVersions":[]}""", false)]
    [InlineData("""{"SeenWelcomeVersions":null}""", false)]
    [InlineData("""{"SeenWelcomeVersions":["", " "]}""", false)]
    [InlineData("""{"EditorWelcomeSeen":true}""", true)]
    [InlineData("{}", false)]
    public void Welcome_seen_migrates_from_any_previous_build(string json, bool seen)
    {
        var settings = Settings.FromJson(json);
        Assert.Equal(seen, settings.EditorWelcomeSeen);
        Assert.Equal(seen, Settings.FromJson(JsonSerializer.Serialize(settings)).EditorWelcomeSeen);
    }

    [AvaloniaFact]
    public async Task Editor_preferences_apply_sidebar_and_layout_changes_and_preserve_explicit_gpu_choices()
    {
        var defaults = Settings.FromJson("{}");
        Assert.Equal(GpuAcceleration.CanEnable, defaults.UseEditorGpu);
        Assert.Equal(GpuAcceleration.CanEnable, defaults.UseRasterFilterGpu);
        Assert.True(defaults.UseSelectionGpu);
        var disabled = Settings.FromJson("""{"UseEditorGpu":false,"UseRasterFilterGpu":false}""");
        Assert.False(disabled.UseEditorGpu);
        Assert.False(disabled.UseRasterFilterGpu);

        var window = new MainWindow { Width = 1280, Height = 820 };
        window.Show();
        try
        {
            var pending = AiDialogs.SettingsDialog(window, window.Settings, window.AiTasks, page: 3);
            Pump();
            if (pending.IsFaulted) await pending;
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

    [AvaloniaFact]
    public async Task Performance_preferences_show_devices_and_keep_the_master_switch_in_sync()
    {
        var window = new MainWindow { Width = 1280, Height = 820 };
        var defaults = Settings.FromJson("{}");
        window.Show();
        try
        {
            var pending = AiDialogs.SettingsDialog(window, defaults, window.AiTasks, page: 7);
            Pump();
            if (pending.IsFaulted) await pending;
            var dialog = Assert.Single(window.OwnedWindows);
            var elements = dialog.GetVisualDescendants().ToArray();
            var master = elements.OfType<ToggleSwitch>().Single(control => control.Name == "UseHardwareAcceleration");
            var options = elements.OfType<CheckBox>().Where(control => control.Name is "UseEditorGpu" or "UseRasterFilterGpu" or "UseSelectionGpu").ToArray();
            Assert.Equal(3, options.Length);
            var supported = options.Where(control => control.IsEnabled).ToArray();
            Assert.Equal(supported.Length != 0, master.IsChecked);
            Assert.All(supported, option => Assert.True(option.IsChecked));

            var devices = elements.OfType<TextBlock>().Where(control => control.Name is "CanvasPreviewDevice" or "CanvasCompositeDevice"
                or "BrushCoverageDevice" or "BrushBlendingDevice" or "LayerBlurDevice" or "LayerStrokeDevice" or "Rgb16BlurDevice"
                or "SelectionModelDevice" or "MattingModelDevice").ToArray();
            Assert.Equal(9, devices.Length);
            Assert.All(devices, device => Assert.False(string.IsNullOrWhiteSpace(device.Text)));

            if (supported.Length > 0)
            {
                master.IsChecked = false;
                Assert.All(supported, option => Assert.False(option.IsChecked));
                supported[0].IsChecked = true;
                Assert.True(master.IsChecked);
                Assert.All(supported.Skip(1), option => Assert.False(option.IsChecked));
                supported[0].IsChecked = false;
                Assert.False(master.IsChecked);
            }
            dialog.Close();
            Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(GpuAcceleration.CanEnable, defaults.UseEditorGpu);
            Assert.Equal(GpuAcceleration.CanEnable, defaults.UseRasterFilterGpu);
            Assert.True(defaults.UseSelectionGpu);
        }
        finally { window.Close(); }
    }

    private static RadioButton[] Options(Window dialog) => dialog.GetVisualDescendants().OfType<RadioButton>().ToArray();
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
