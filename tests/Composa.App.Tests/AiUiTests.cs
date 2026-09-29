using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Composa.AI;
using Composa.Editing;
using SkiaSharp;

namespace Composa.App.Tests;

public class AiUiTests
{
    [AvaloniaFact]
    public void Ai_bar_and_selection_brush_render_without_an_engine_pack()
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
        var session = EditorSession.NewCanvas(640, 420, SKColors.White);
        window.AddSession(session);
        session.SelectRect(new SKRect(120, 90, 420, 300));
        window.SelectTool(Tool.SelectionBrush);

        var text = window.GetVisualDescendants().OfType<TextBlock>().Select(item => item.Text).Where(value => value != null).ToList();
        Assert.Contains("AI", text);
        Assert.Contains("Generate", window.GetVisualDescendants().OfType<Button>().Select(button => button.Content as string));
        Assert.Contains("Remove", window.GetVisualDescendants().OfType<Button>().Select(button => button.Content as string));
        Assert.Contains("Advanced…", window.GetVisualDescendants().OfType<Button>().Select(button => button.Content as string));
        Assert.Contains("Selection Brush", text);
        Assert.Contains(window.GetVisualDescendants().OfType<ComboBox>(), combo => combo.SelectedItem?.ToString() == "1 MP");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), box => box.PlaceholderText?.Contains("Describe what you want") == true);
        Assert.True(Screenshots.Save(window, "ai-platform-selection-brush"));
    }
}
