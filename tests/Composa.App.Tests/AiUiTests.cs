using Avalonia.Controls;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Composa.AI;
using Composa.Editing;
using SkiaSharp;

namespace Composa.App.Tests;

public class AiUiTests
{
    [AvaloniaFact]
    public void Workflow_picker_and_compact_variant_dropdown_follow_the_selected_pack()
    {
        var window = new MainWindow { Width = 1280, Height = 900 }; window.Settings.CheckForUpdates = false; window.Show(); window.AiTasks.SetConnectedForTests();
        var session = EditorSession.NewCanvas(640, 420, SKColors.White); window.AddSession(session); session.SelectRect(new SKRect(100, 50, 250, 120));
        var combos = window.AiFloatingPanel.GetVisualDescendants().OfType<ComboBox>().ToArray();
        var variants = Assert.Single(combos, combo => combo.Items.Cast<string>().SequenceEqual(new[] { "1", "2", "3" }));
        Assert.Equal(60, variants.Width); variants.SelectedIndex = 1;
        var generate = Assert.Single(window.AiFloatingPanel.GetVisualDescendants().OfType<Button>(), button => button.Content as string == "Generate");
        var split = Assert.Single(window.AiFloatingPanel.GetVisualDescendants().OfType<Border>(), border => border.Name == "AiGenerateSplit");
        var joined = Assert.IsType<StackPanel>(split.Child);
        Assert.Equal(0, joined.Spacing);
        Assert.Equal(generate.Height, variants.Height);
        var remove = Assert.Single(window.AiFloatingPanel.GetVisualDescendants().OfType<Button>(), button => button.Content as string == "Remove");
        var more = Assert.Single(window.AiFloatingPanel.GetVisualDescendants().OfType<Button>(), button => button.Content as string == "••• ▾");
        Assert.Equal(remove.Height, split.Height);
        Assert.Equal(more.Height, split.Height);
        Assert.Equal(new Avalonia.CornerRadius(6, 0, 0, 6), generate.CornerRadius);
        Assert.Equal(new Avalonia.CornerRadius(0, 6, 6, 0), variants.CornerRadius);
        var engines = Assert.Single(combos, combo => combo.Items.Cast<string>().Contains("CHAT GPT 2.5"));
        engines.SelectedIndex = 1; Assert.True(window.AiTasks.SelectedEngine!.PaidApi);
        Assert.DoesNotContain("Variants", window.AiFloatingPanel.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text));
        Assert.True(Screenshots.Save(window, "ai-gpt-pack-compact-panel")); window.Close();
    }

    [AvaloniaFact]
    public void Floating_panel_anchors_below_selection_follows_zoom_and_can_be_dragged()
    {
        var window = new MainWindow { Width = 1280, Height = 900 }; window.Show();
        window.AiTasks.SetConnectedForTests();
        var session = EditorSession.NewCanvas(640, 420, SKColors.White); window.AddSession(session);
        session.SelectRect(new SKRect(120, 60, 280, 130));
        Assert.True(Screenshots.Save(window, "ai-panel-below-selection"));
        var panel = window.AiFloatingPanel;
        Assert.Equal(window.Canvas.ToScreen(new SKPoint(280, 130)).Y + 10, Canvas.GetTop(panel), 1);
        window.Canvas.ZoomTo(0.5); Dispatcher.UIThread.RunJobs();
        Assert.Equal(window.Canvas.ToScreen(new SKPoint(280, 130)).Y + 10, Canvas.GetTop(panel), 1);
        var heading = window.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "Generative AI");
        var at = heading.TranslatePoint(new Point(20, 8), window)!.Value;
        var original = new Point(Canvas.GetLeft(panel), Canvas.GetTop(panel));
        window.MouseDown(at, MouseButton.Left); window.MouseMove(at + new Vector(30, -25), RawInputModifiers.LeftMouseButton); window.MouseUp(at + new Vector(30, -25), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(original.Y - 25, Canvas.GetTop(panel), 1); Assert.Equal(original.X + 30, Canvas.GetLeft(panel), 1);
        Assert.True(Screenshots.Save(window, "ai-panel-dragged")); window.Close();
    }

    [AvaloniaFact]
    public void Reference_image_icon_is_centered_and_clicking_a_thumbnail_opens_a_preview()
    {
        var window = new MainWindow { Width = 1280, Height = 900 }; window.Show(); window.AiTasks.SetConnectedForTests();
        var session = EditorSession.NewCanvas(640, 420); window.AddSession(session); session.SelectRect(new SKRect(100, 60, 200, 130));
        var reference = new SKBitmap(80, 40); reference.Erase(SKColors.CornflowerBlue); window.AddAiReferenceForTests(reference);
        Assert.True(Screenshots.Save(window, "ai-reference-thumbnails"));
        var add = window.GetVisualDescendants().OfType<Button>().Single(button => ToolTip.GetTip(button)?.ToString()?.StartsWith("Add another reference image") == true);
        Assert.Equal(0, add.MinWidth); Assert.Equal(HorizontalAlignment.Center, add.HorizontalContentAlignment); Assert.Equal(VerticalAlignment.Center, add.VerticalContentAlignment);
        var previewButton = window.GetVisualDescendants().OfType<Button>().Single(button => ToolTip.GetTip(button)?.ToString() == "Preview reference: Test reference");
        previewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        var preview = Assert.Single(window.OwnedWindows); Assert.Equal("Test reference", preview.Title);
        Assert.True(Screenshots.Save(preview, "ai-reference-preview")); preview.Close(); window.Close();
    }

    [AvaloniaFact]
    public void Connected_floating_ai_and_compact_tool_bar_render()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        window.AiTasks.SelectedEngine = new EngineProfile
        {
            Id = "ui-test", DisplayName = "UI Test Engine",
            Workflows = [new EngineWorkflow { Id = "edit", File = "edit.json" }],
            Tasks =
            [
                new EngineTaskBinding { Task = AiTaskKind.GenerativeFill, Workflow = "edit" },
                new EngineTaskBinding { Task = AiTaskKind.RemoveObject, Workflow = "edit" },
                new EngineTaskBinding { Task = AiTaskKind.ChangeBackground, Workflow = "edit" }
            ]
        };
        window.AiTasks.SetConnectedForTests();
        var session = EditorSession.NewCanvas(640, 420, SKColors.White);
        window.AddSession(session);
        session.SelectRect(new SKRect(120, 90, 420, 300));
        window.SelectTool(Tool.SelectionBrush);

        var text = window.GetVisualDescendants().OfType<TextBlock>().Select(item => item.Text).Where(value => value != null).ToList();
        Assert.Contains("Generate", window.GetVisualDescendants().OfType<Button>().Select(button => button.Content as string));
        Assert.Contains("Remove", window.GetVisualDescendants().OfType<Button>().Select(button => button.Content as string));
        Assert.Contains("Advanced…", window.GetVisualDescendants().OfType<Button>().Select(button => button.Content as string));
        Assert.Contains("Selection Brush", text);
        Assert.Contains("References (0/6)", text);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<ComboBox>(), combo => combo.SelectedItem?.ToString() == "1 MP");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), box => box.PlaceholderText?.Contains("Describe what you want") == true);
        Assert.True(Screenshots.Save(window, "ai-platform-selection-brush"));
    }

    [AvaloniaFact]
    public void Floating_ai_waits_for_selection_brush_and_reference_remove_button_works()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        window.AiTasks.SelectedEngine = new EngineProfile
        {
            Id = "ui-test", DisplayName = "UI Test Engine",
            Workflows = [new EngineWorkflow { Id = "edit", File = "edit.json" }],
            Tasks = [new EngineTaskBinding { Task = AiTaskKind.GenerativeFill, Workflow = "edit" }]
        };
        window.AiTasks.SetConnectedForTests();
        var session = EditorSession.NewCanvas(640, 420, SKColors.White);
        window.AddSession(session);
        window.SelectTool(Tool.SelectionBrush);

        session.BeginSelectionBrush(new SKPoint(100, 100));
        Assert.False(window.AiFloatingVisible);
        session.ContinueSelectionBrush(new SKPoint(180, 150));
        Assert.False(window.AiFloatingVisible);
        session.EndSelectionBrush();
        Assert.True(window.AiFloatingVisible);

        var reference = new SKBitmap(32, 24);
        reference.Erase(SKColors.CornflowerBlue);
        window.AddAiReferenceForTests(reference);
        Assert.Equal(1, window.AiReferenceCount);
        var remove = Assert.Single(window.GetVisualDescendants().OfType<Button>(), button => ToolTip.GetTip(button)?.ToString() == "Remove reference");
        remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(0, window.AiReferenceCount);
        Assert.Contains("References (0/6)", window.GetVisualDescendants().OfType<TextBlock>().Select(item => item.Text));
    }
}
