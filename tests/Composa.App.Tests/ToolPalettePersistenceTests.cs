using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

public class ToolPalettePersistenceTests
{
    [AvaloniaFact]
    public void Folder_choices_survive_tabs_closed_documents_and_serialized_preferences()
    {
        var window = new MainWindow(); window.Show(); var first = EditorSession.NewCanvas(200, 120, SKColors.White); window.AddSession(first);
        first.LassoKind = LassoKind.Polygonal; first.MarqueeKind = MarqueeKind.Ellipse;
        first.SmearMode = SmearMode.Burn; first.ShapeKind = ShapeKind.Line; first.EraserMode = true;
        window.SelectTool(Tool.SelectionBrush); window.SelectTool(Tool.Bucket); window.SelectTool(Tool.Move);
        Assert.Equal("Paint Bucket", window.RailButton(Tool.Gradient).Current!.Name);
        var second = EditorSession.NewCanvas(200, 120, SKColors.White); window.AddSession(second);
        Assert.Equal("Paint Bucket", window.RailButton(Tool.Gradient).Current!.Name);
        Assert.Equal(LassoKind.Polygonal, second.LassoKind);
        window.KeyPressQwerty(PhysicalKey.W, RawInputModifiers.Control); Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.W, RawInputModifiers.Control); Dispatcher.UIThread.RunJobs();
        Assert.Null(window.Session);
        window.AddSession(EditorSession.NewCanvas(200, 120, SKColors.White));
        Assert.Equal("Paint Bucket", window.RailButton(Tool.Gradient).Current!.Name);
        var restored = Settings.FromJson(JsonSerializer.Serialize(window.Settings));
        var reopened = new MainWindow(); reopened.Settings.ToolPalette = restored.ToolPalette; reopened.Show();
        reopened.AddSession(EditorSession.NewCanvas(200, 120, SKColors.White));
        foreach (var (tool, name) in new[] { (Tool.Gradient, "Paint Bucket"), (Tool.Wand, "Selection Brush"), (Tool.Lasso, "Polygonal Lasso"),
            (Tool.Marquee, "Ellipse Marquee"), (Tool.Brush, "Eraser"), (Tool.Smear, "Burn"), (Tool.Shape, "Line") })
            Assert.Equal(name, reopened.RailButton(tool).Current!.Name);
        reopened.SelectTool(Tool.Bucket); Assert.Equal(Tool.Bucket, reopened.Session!.Tool);
        Screenshots.Save(reopened, "remembered-tool-folders"); window.Close(); reopened.Close();
    }

    [AvaloniaFact]
    public void Alignment_icons_are_filled_and_separated_from_the_reference_selector()
    {
        var window = new MainWindow { Width = 1400, Height = 850 }; window.Show(); window.AddSession(EditorSession.NewCanvas(300, 200, SKColors.White));
        Dispatcher.UIThread.RunJobs();
        var reference = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "AlignmentReference");
        var left = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AlignLeft");
        var start = left.TranslatePoint(default, window)!.Value.X;
        var end = reference.TranslatePoint(new Point(reference.Bounds.Width, 0), window)!.Value.X;
        Assert.True(start - end >= 14, $"Gap: {start - end}");
        foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(b => b.Name?.StartsWith("Align") == true || b.Name?.StartsWith("Flip") == true))
            Assert.Contains(button.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>(), p => p.Fill != null);
        Screenshots.Save(window, "filled-spaced-alignment"); window.Close();
    }
}
