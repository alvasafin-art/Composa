using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
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
        var session = EditorSession.NewCanvas(640, 420, SKColors.White);
        window.AddSession(session);
        session.SelectRect(new SKRect(120, 90, 420, 300));
        window.SelectTool(Tool.SelectionBrush);

        var text = window.GetVisualDescendants().OfType<TextBlock>().Select(item => item.Text).Where(value => value != null).ToList();
        Assert.Contains("AI", text);
        Assert.Contains("AI ▾", window.GetVisualDescendants().OfType<Button>().Select(button => button.Content as string));
        Assert.Contains("Selection Brush", text);
        Assert.Contains(window.GetVisualDescendants().OfType<ComboBox>(), combo => combo.SelectedItem?.ToString() == "No Engine Pack");
        Assert.True(Screenshots.Save(window, "ai-platform-selection-brush"));
    }
}
