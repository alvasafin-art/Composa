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
        window.SelectTool(Tool.SelectionBrush); window.SelectTool(Tool.Bucket); window.SelectTool(Tool.HealingBrush); window.SelectTool(Tool.Move);
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
            (Tool.Marquee, "Ellipse Marquee"), (Tool.Smear, "Burn"), (Tool.Shape, "Line"), (Tool.CloneStamp, "Healing Brush") })
            Assert.Equal(name, reopened.RailButton(tool).Current!.Name);
        reopened.SelectTool(Tool.Bucket); Assert.Equal(Tool.Bucket, reopened.Session!.Tool);
        Screenshots.Save(reopened, "remembered-tool-folders"); window.Close(); reopened.Close();
    }

    [AvaloniaTheory]
    [InlineData(Tool.Brush, Tool.Eraser)]
    [InlineData(Tool.ObjectSelectionAi, Tool.Wand)]
    [InlineData(Tool.Patch, Tool.Patch)]
    [InlineData(Tool.HealingBrush, Tool.HealingBrush)]
    public void Startup_selects_move_and_keeps_the_legacy_tool_folder_choices(Tool saved, Tool expected)
    {
        var window = new MainWindow();
        window.Settings.ToolPalette = new ToolPaletteSettings { Active = saved, Eraser = saved == Tool.Brush };
        window.Show(); window.AddSession(EditorSession.NewCanvas(120, 80));
        Assert.Equal(Tool.Move, window.Session!.Tool);
        Assert.True(window.RailButton(Tool.Move).IsChecked);
        window.SelectTool(expected);
        Assert.Equal(expected, window.Session.Tool);
        if (expected == Tool.Wand) Assert.Equal("Object Selection", window.RailButton(expected).Current!.Name);
        window.Close();
    }

    [AvaloniaFact]
    public void Healing_hardness_starts_at_one_hundred_and_survives_tabs_and_a_relaunch()
    {
        var window = new MainWindow();
        window.Settings.Brush = new() { Hardness = 0.36 };
        window.Show();
        window.AddSession(EditorSession.NewCanvas(120, 80));
        window.SelectTool(Tool.HealingBrush);
        Assert.Equal(1, window.Session!.Brush.Hardness);
        var hardness = window.GetVisualDescendants().OfType<Composa.App.Controls.SliderField>()
            .Single(field => field.Label == "Hardness" && field.IsEffectivelyVisible);
        Assert.True(hardness.Focus());
        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        Assert.Equal(0.99, window.Session.Brush.Hardness, 2);
        window.AddSession(EditorSession.NewCanvas(120, 80));
        Assert.Equal(0.99, window.Session.Brush.Hardness, 2);
        window.SelectTool(Tool.Brush);
        Assert.Equal(0.36, window.Session.Brush.Hardness, 2);
        var saved = Settings.FromJson(JsonSerializer.Serialize(window.Settings));
        window.Close();

        var reopened = new MainWindow();
        reopened.Settings.ToolPalette = saved.ToolPalette;
        reopened.Settings.HealingBrushHardness = saved.HealingBrushHardness;
        reopened.Show();
        reopened.AddSession(EditorSession.NewCanvas(120, 80));
        try
        {
            Assert.Equal(Tool.Move, reopened.Session!.Tool);
            reopened.SelectTool(Tool.HealingBrush);
            Assert.Equal(0.99, reopened.Session.Brush.Hardness, 2);
        }
        finally { reopened.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(IconStyle.Outline)]
    [InlineData(IconStyle.Filled)]
    public void Alignment_icons_follow_the_style_and_are_separated_from_the_reference_selector(IconStyle style)
    {
        var app = (App)Application.Current!;
        app.ApplyIconStyle(style);
        var window = new MainWindow { Width = 1400, Height = 850 };
        window.Show();
        try
        {
            window.AddSession(EditorSession.NewCanvas(300, 200, SKColors.White));
            Dispatcher.UIThread.RunJobs();
            var reference = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "AlignmentReference");
            var left = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AlignLeft");
            var start = left.TranslatePoint(default, window)!.Value.X;
            var end = reference.TranslatePoint(new Point(reference.Bounds.Width, 0), window)!.Value.X;
            Assert.True(start - end >= 14, $"Gap: {start - end}");
            var buttons = window.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Name?.StartsWith("Align") == true || b.Name?.StartsWith("Flip") == true);
            foreach (var button in buttons)
                Assert.Contains(button.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>(),
                    glyph => style == IconStyle.Filled ? glyph.Fill != null : glyph.StrokeThickness > 0);
            Screenshots.Save(window, $"alignment-{style.ToString().ToLowerInvariant()}");
        }
        finally { app.ApplyIconStyle(IconStyle.Outline); window.Close(); }
    }
}
