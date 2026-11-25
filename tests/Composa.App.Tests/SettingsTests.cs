using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Composa.Editing;
using Composa.AI;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

/// <summary>Tool toggles that follow the person rather than the document.</summary>
public class SettingsTests
{
    [Fact]
    public async Task Queued_tool_preferences_cannot_overwrite_a_later_synchronous_save()
    {
        var directory = Directory.CreateTempSubdirectory("composa-preferences-");
        var path = Path.Combine(directory.FullName, "settings.json");
        try
        {
            var settings = new Settings { ToolPalette = new() { Active = Tool.Brush } };
            var brush = settings.SaveInBackgroundTo(path);
            settings.ToolPalette = new() { Active = Tool.Eraser };
            var eraser = settings.SaveInBackgroundTo(path);
            settings.ToolPalette = new() { Active = Tool.Marquee };
            Assert.True(settings.SaveTo(path));
            await Task.WhenAll(brush, eraser);
            Assert.Equal(Tool.Marquee, Settings.FromJson(File.ReadAllText(path)).ToolPalette!.Active);

            settings.ToolPalette = new() { Active = Tool.Brush };
            var saved = settings.SaveInBackgroundTo(path);
            settings.ToolPalette = new() { Active = Tool.Move };
            await saved;
            Assert.Equal(Tool.Brush, Settings.FromJson(File.ReadAllText(path)).ToolPalette!.Active);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void View_options_and_move_tool_toggles_survive_the_settings_file()
    {
        var settings = new Settings
        {
            UseSelectionGpu = false, ShowTransformControls = false, AutoSelect = false, ShowPixelGrid = false,
            AiGptContextPadding = 64,
            AiSelectionContextPercent = 150,
            AiTaskEngineIds = new() { [nameof(AiTaskKind.GenerateImage)] = "chatgpt-image-2.5", [nameof(AiTaskKind.GenerativeExpand)] = "flux2-klein-intel-xpu" },
            AssistantServerUrl = "http://127.0.0.1:18080", AssistantServerExecutable = "llama-server.exe",
            AssistantModelPath = "assistant.gguf", AssistantContextSize = 8192, AssistantMaxTokens = 1024, AssistantAutoStart = false,
            View = new ViewOptions
            {
                ShowRulers = true, ShowGrid = true, Snap = false, SnapToGrid = true, LockGuides = true,
                Grid = new LayoutGrid { Spacing = 50, Subdivisions = 5 },
                GridAppearance = new GridAppearance { Preset = GridColorPreset.Custom, CustomColor = 0xFF102030, Style = GridStyle.Dots, Opacity = 60 }
            }
        };
        var json = JsonSerializer.Serialize(settings);
        Assert.Contains("\"Dots\"", json); // Named, so the file survives the enums being reordered.
        var loaded = JsonSerializer.Deserialize<Settings>(json)!;
        Assert.Equal((false, false, false), (loaded.ShowTransformControls, loaded.AutoSelect, loaded.ShowPixelGrid));
        Assert.False(loaded.UseSelectionGpu);
        Assert.Equal(settings.View, loaded.View);
        Assert.Equal(64, loaded.AiGptContextPadding);
        Assert.Equal(150, loaded.AiSelectionContextPercent);
        Assert.Equal("chatgpt-image-2.5", loaded.EngineForTask(AiTaskKind.GenerateImage));
        Assert.Equal("flux2-klein-intel-xpu", loaded.EngineForTask(AiTaskKind.GenerativeExpand));
        Assert.Equal("flux2-klein-intel-xpu", loaded.EngineForTask(AiTaskKind.GenerativeFill));
        Assert.Equal((settings.AssistantServerUrl, settings.AssistantServerExecutable, settings.AssistantModelPath),
            (loaded.AssistantServerUrl, loaded.AssistantServerExecutable, loaded.AssistantModelPath));
        Assert.Equal((8192, 1024, false), (loaded.AssistantContextSize, loaded.AssistantMaxTokens, loaded.AssistantAutoStart));
        // A settings file from before these were remembered keeps the defaults.
        var old = JsonSerializer.Deserialize<Settings>("""{ "JpegQuality": 80 }""")!;
        Assert.Equal((true, true, true), (old.ShowTransformControls, old.AutoSelect, old.View.Snap));
        Assert.True(old.UseSelectionGpu);
        Assert.Equal(0, old.AiGptContextPadding);
        Assert.Equal(100, old.AiSelectionContextPercent);
        Assert.Empty(old.AiTaskEngineIds);
    }

    [AvaloniaFact]
    public void Toggling_view_options_and_transform_controls_is_remembered_and_seeds_the_first_document()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        window.Settings.View = new ViewOptions { ShowRulers = true, SnapToLayers = false };
        window.Settings.AutoSelect = false;
        window.AddSession(EditorSession.NewCanvas(200, 100, SKColors.White));
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.Session!.View.ShowRulers);
        Assert.False(window.Session.View.SnapToLayers);
        Assert.True(window.Canvas.AutoSelect); // The canvas was built before the test changed the settings; startup reads them.

        window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.Control);      // Rulers off
        window.KeyPressQwerty(PhysicalKey.Quote, RawInputModifiers.Control);  // Grid on
        window.KeyPressQwerty(PhysicalKey.H, RawInputModifiers.Control);      // Transform controls off
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.Settings.View.ShowRulers);
        Assert.True(window.Settings.View.ShowGrid);
        Assert.False(window.Settings.ShowTransformControls);
        Assert.False(window.Canvas.ShowTransformControls);
    }
}
