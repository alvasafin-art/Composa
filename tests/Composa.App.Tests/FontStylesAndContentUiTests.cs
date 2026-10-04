using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.Editing;
using Composa.Model;
using Composa.Text;
using SkiaSharp;

namespace Composa.App.Tests;

public class FontStylesAndContentUiTests
{
    private static ComboBox Styles(MainWindow window) => window.GetVisualDescendants().OfType<ComboBox>().Single(c => ToolTip.GetTip(c) as string == "Font style");
    private static ComboBox Families(MainWindow window) => window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.MaxWidth == 190);

    [AvaloniaFact]
    public void Face_menu_follows_family_and_active_layer_without_starting_text_editing()
    {
        var window = new MainWindow { Width = 1280, Height = 800 }; window.Show();
        var session = EditorSession.NewCanvas(600, 400, SKColors.White); window.AddSession(session);
        var family = EditorSession.FontFamilies.First(f => FontCatalog.ForFamily(f).Count > 2);
        var layer = session.AddText(new SKPoint(70, 80), new TextStyle { Text = "Font styles", FontFamily = family, Size = 46 });
        window.SelectTool(Tool.Text); Dispatcher.UIThread.RunJobs();
        var choices = FontCatalog.ForFamily(family);
        var selected = choices.Last(c => c.Face != layer.Text!.Face);
        Styles(window).SelectedIndex = choices.ToList().IndexOf(selected);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(selected.Face, layer.Text!.Face); Assert.False(session.IsEditingText);
        Assert.Equal(selected.Name, Styles(window).SelectedItem);
        Assert.Equal(choices.Select(c => c.Name), Styles(window).Items.Cast<string>());
        Assert.True(Styles(window).Bounds.Height <= 30);
        Assert.True(Screenshots.Save(window, "text-installed-font-style"));
        session.Undo(); Assert.NotEqual(selected.Face, session.ActiveLayer!.Text!.Face);
        session.Redo(); Assert.Equal(selected.Name, Styles(window).SelectedItem);
        var single = EditorSession.FontFamilies.FirstOrDefault(f => FontCatalog.ForFamily(f).Count == 1);
        if (single != null)
        {
            Families(window).SelectedIndex = EditorSession.FontFamilies.ToList().IndexOf(single);
            Dispatcher.UIThread.RunJobs();
            Assert.Single(Styles(window).Items); Assert.Equal(0, Styles(window).SelectedIndex);
            Assert.Equal(FontCatalog.ForFamily(single)[0].Face, session.ActiveLayer!.Text!.Face);
            Assert.True(Screenshots.Save(window, "text-single-font-style"));
        }
        var second = session.AddText(new SKPoint(70, 160), new TextStyle { Text = "Second", FontFamily = family, Size = 40 });
        Dispatcher.UIThread.RunJobs(); Assert.Equal(choices.Count, Styles(window).Items.Count);
        session.SelectLayer(layer.Id); Dispatcher.UIThread.RunJobs();
        Assert.Equal(session.ActiveLayer!.Text!.FontFamily, Families(window).SelectedItem);
        Assert.False(session.IsEditingText);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Move_handles_resize_the_filled_square_and_inspector_reports_its_content(bool gradient)
    {
        var window = new MainWindow { Width = 1280, Height = 800 }; window.Show();
        var session = EditorSession.NewCanvas(600, 400); window.AddSession(session);
        session.View = session.View with { Snap = false };
        session.SelectRect(new SKRect(170, 100, 300, 230));
        if (gradient)
        {
            session.Foreground = new SKColor(0x3D, 0x9B, 0xFF); session.Background = new SKColor(0xFF, 0xAE, 0x56);
            var layer = session.ActiveLayer!; var original = session.BeginGradient(layer);
            session.DrawGradient(layer, original, new SKPoint(170, 100), new SKPoint(300, 230)); session.Commit();
        }
        else session.Fill(new SKColor(0x3D, 0x9B, 0xFF));
        session.Deselect(); window.SelectTool(Tool.Move); window.Canvas.ShowTransformControls = true; Dispatcher.UIThread.RunJobs();
        var source = session.ActiveLayer!.Pixels;
        var fields = window.GetVisualDescendants().OfType<NumericUpDown>().ToList();
        Assert.Contains(fields, n => n.Value == 170); Assert.Contains(fields, n => n.Value == 130);
        Assert.True(Screenshots.Save(window, gradient ? "gradient-content-frame" : "fill-content-frame"));
        Point At(float x, float y) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(new SKPoint(x, y)), window)!.Value;
        window.MouseDown(At(300, 230), MouseButton.Left);
        Assert.Equal(new SKRect(170, 100, 300, 230), session.Transform!.StartFrame);
        window.MouseMove(At(360, 290)); window.MouseUp(At(360, 290), MouseButton.Left); Dispatcher.UIThread.RunJobs();
        Assert.Equal(new SKRect(170, 100, 360, 290), session.ActiveLayer!.ControlBounds);
        Assert.Same(source, session.ActiveLayer.Pixels);
        session.Undo(); Assert.Equal(new SKRect(170, 100, 300, 230), session.ActiveLayer!.ControlBounds);
        window.Close();
    }

    [AvaloniaFact]
    public void Native_control_accent_is_the_same_blue_as_the_editor_palette()
    {
        Assert.True(Application.Current!.TryFindResource("SystemAccentColor", ThemeVariant.Dark, out var accent));
        Assert.Equal(Color.Parse("#3D9BFF"), Assert.IsType<Color>(accent));
        Assert.Equal(Color.Parse("#3D9BFF"), ((ISolidColorBrush)Palette.Accent).Color);
    }
}
