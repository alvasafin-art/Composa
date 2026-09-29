using Avalonia.Controls;
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
