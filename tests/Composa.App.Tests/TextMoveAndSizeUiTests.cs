using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.Editing;
using Composa.Model;
using Composa.Text;
using SkiaSharp;

namespace Composa.App.Tests;

public class TextMoveAndSizeUiTests
{
    [AvaloniaFact]
    public void Move_picks_text_in_a_space_over_a_filled_layer_and_undo_restores_its_position()
    {
        var window = new MainWindow { Width = 1440, Height = 850 }; window.Settings.CheckForUpdates = false; window.Show();
        var session = EditorSession.NewCanvas(800, 500, SKColors.White); window.AddSession(session);
        var text = session.AddText(new SKPoint(160, 140), new TextStyle { Text = "Left      Right", Size = 44 });
        var layout = new TextLayout(text.Text!); var line = layout.Lines[0];
        var point = text.Matrix.MapPoint(line.X + (line.Positions[6] + line.Positions[7]) / 2, line.Baseline - 8);
        Assert.Equal(0, text.Pixels!.GetPixel((int)(point.X - text.Transform.X), (int)(point.Y - text.Transform.Y)).Alpha);
        session.SelectLayer(session.Document.Layers[0].Id); window.SelectTool(Tool.Move); window.Canvas.ShowTransformControls = false;
        Dispatcher.UIThread.RunJobs();
        Point At(SKPoint p) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(p), window)!.Value;
        var before = text.Transform;
        window.MouseDown(At(point), MouseButton.Left); window.MouseMove(At(new SKPoint(point.X + 35, point.Y + 20)), RawInputModifiers.LeftMouseButton);
        window.MouseUp(At(new SKPoint(point.X + 35, point.Y + 20)), MouseButton.Left); Dispatcher.UIThread.RunJobs();
        Assert.Equal(text.Id, session.Document.ActiveLayerId); Assert.Equal(before.X + 35, text.Transform.X); Assert.Equal(before.Y + 20, text.Transform.Y);
        session.Undo(); Assert.Equal(before, session.Document.Find(text.Id)!.Transform);
        window.MouseDown(At(new SKPoint(-10, 20)), MouseButton.Left); window.MouseUp(At(new SKPoint(-10, 20)), MouseButton.Left);
        Assert.Null(session.Document.ActiveLayerId); window.Close();
    }

    [AvaloniaFact]
    public void Size_field_edits_selected_letters_and_alignment_controls_keep_the_editor_style()
    {
        var window = new MainWindow { Width = 1440, Height = 850 }; window.Settings.CheckForUpdates = false; window.Show();
        var session = EditorSession.NewCanvas(800, 500); window.AddSession(session);
        var text = session.AddText(new SKPoint(130, 140), new TextStyle { Text = "Different sizes", Size = 24 });
        session.EditText(text); window.SelectTool(Tool.Text); Dispatcher.UIThread.RunJobs();
        session.TextEdit!.MoveTo(10, false); session.TextEdit.MoveTo(15, true); Dispatcher.UIThread.RunJobs();
        var size = window.GetVisualDescendants().OfType<NumericUpDown>().Single(c => c.Name == "TextSize");
        size.Value = 58; Dispatcher.UIThread.RunJobs();
        Assert.Equal(24, text.Text!.SizeAt(0)); Assert.Equal(58, text.Text.SizeAt(10));
        Assert.True(session.IsEditingText); Assert.Equal(10, session.TextEdit.SelectionStart); Assert.Equal(15, session.TextEdit.SelectionEnd);
        Screenshots.Save(window, "mixed-character-sizes");
        session.FinishText(); window.SelectTool(Tool.Move); Dispatcher.UIThread.RunJobs();
        Assert.Single(window.GetVisualDescendants().OfType<ComboBox>(), c => c.Name == "AlignmentReference");
        Assert.Equal(6, window.GetVisualDescendants().OfType<Button>().Count(b => b.Name?.StartsWith("Align", StringComparison.Ordinal) == true));
        Screenshots.Save(window, "move-alignment-options"); window.Close();
    }
}
