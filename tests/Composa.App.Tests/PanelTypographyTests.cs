using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

public class PanelTypographyTests
{
    [AvaloniaFact]
    public async Task Font_arrows_preview_the_canvas_and_escape_preserves_the_selected_text()
    {
        var window = new MainWindow { Width = 1360, Height = 900 }; window.Show();
        var s = EditorSession.NewCanvas(320, 150, SKColors.White); window.AddSession(s);
        var layer = s.AddText(new SKPoint(12, 24), new TextStyle { Text = "Type 123", FontFamily = EditorSession.FontFamilies.First(), Size = 35 });
        int revision = s.Revision;
        byte[] View()
        {
            using var bmp = Composa.Rendering.Pixels.NewColor(320, 150);
            s.RenderView(bmp, bmp.Info.Rect, new Composa.Rendering.RenderView(1, new()));
            return bmp.Bytes;
        }
        try
        {
            Dispatcher.UIThread.RunJobs();
            var before = View();
            var combo = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "FontFamilyPicker");
            combo.Focus(); combo.IsDropDownOpen = true; Dispatcher.UIThread.RunJobs();
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            var until = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < until && View().SequenceEqual(before))
            { await Task.Delay(15); Dispatcher.UIThread.RunJobs(); }
            Assert.False(View().SequenceEqual(before));
            Assert.Equal(revision, s.Revision);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); Dispatcher.UIThread.RunJobs();
            Assert.False(combo.IsDropDownOpen); Assert.Equal(before, View()); Assert.Equal(revision, s.Revision);
        }
        finally { s.MarkSaved("type.cmps", s.History.CurrentId); window.Close(); }
    }

    [AvaloniaFact]
    public void Spacing_fields_align_units_and_convert_values_in_place()
    {
        var window = new MainWindow { Width = 1440, Height = 900 };
        window.Show();
        var session = EditorSession.NewCanvas(480, 240, SKColors.White);
        window.AddSession(session);
        var layer = session.AddText(new SKPoint(30, 60), new TextStyle { Text = "Composa", Size = 50, Leading = 60, Tracking = -1 });
        try
        {
            Dispatcher.UIThread.RunJobs();
            NumericUpDown Field(string name) => window.GetVisualDescendants().OfType<NumericUpDown>()
                .Single(n => Avalonia.Automation.AutomationProperties.GetName(n) == name);
            Panel Unit(string name) => (Panel)((Grid)Field(name).Parent!).Children[2];
            TextBlock Label(string name) => Unit(name).Children.OfType<TextBlock>().Single();
            Point Center(Control control) => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
            var sizeLabel = Label("Font size");
            var trackLabel = Label("Letter spacing");
            var lineLabel = Label("Line height");
            Assert.Equal(Center(sizeLabel).X, Center(trackLabel).X, 1);
            Assert.Equal(Center(trackLabel).Y, Center(lineLabel).Y, 1);
            Assert.Equal(sizeLabel.FontSize, trackLabel.FontSize);
            Assert.Equal(sizeLabel.Foreground, trackLabel.Foreground);
            void Switch(string name)
            {
                var point = Center(Unit(name));
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
            }
            Switch("Line height");
            Switch("Letter spacing");
            Assert.Equal("%", Label("Letter spacing").Text);
            Assert.Equal("%", Label("Line height").Text);
            Assert.Equal(120, layer.Text!.Leading);
            Assert.Equal(-2, layer.Text.Tracking);
            Assert.Equal(60, layer.Text.LineHeight);
            window.MouseMove(new Point(600, 100));
            Assert.True(Screenshots.Save(window, "text-property-spacing"));
            Switch("Line height");
            Switch("Letter spacing");
            Assert.Equal(60, layer.Text.Leading);
            Assert.Equal(-1, layer.Text.Tracking);
        }
        finally { session.MarkSaved("spacing.cmps", session.History.CurrentId); window.Close(); }
    }

    [AvaloniaFact]
    public void Dragging_titles_reparents_live_panels_and_bottom_rail_without_changing_the_document()
    {
        var window = new MainWindow { Width = 1440, Height = 900 };
        window.Show();
        var session = EditorSession.NewCanvas(640, 400, SKColors.White);
        window.AddSession(session);
        session.AddText(new SKPoint(40, 100), new TextStyle { Text = "Composa", Size = 64 });
        var revision = session.Revision;
        try
        {
            var dock = window.GetVisualDescendants().OfType<SideDock>().Single();
            Assert.True(dock.Section("Properties").State.Visible);
            var layers = dock.GetVisualDescendants().OfType<LayersPanel>().Single();
            var header = layers.PanelTitle;
            void Drag(Point start, Point end)
            {
                window.MouseMove(start); window.MouseDown(start, MouseButton.Left);
                window.MouseMove(start.WithX(start.X + 12), RawInputModifiers.LeftMouseButton); window.MouseMove(end, RawInputModifiers.LeftMouseButton);
                window.MouseUp(end, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            }
            Dispatcher.UIThread.RunJobs();
            Drag(header.TranslatePoint(new Point(30, 8), window)!.Value, new Point(45, 150));
            Assert.True(dock.Section("Layers").State.Left);
            Assert.Same(layers, dock.LeftHost.GetVisualDescendants().OfType<LayersPanel>().Single());
            var rail = window.RailButton(Tool.Move).FindAncestorOfType<ScrollViewer>()!;
            var surface = (Border)rail.Parent!;
            Drag(surface.TranslatePoint(new Point(20, 3), window)!.Value, new Point(730, 820));
            Assert.Equal("Bottom", window.CaptureWorkspace().ToolsPosition);
            Assert.Equal(revision, session.Revision);
            Assert.Single(window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == "Layers" && t.IsEffectivelyVisible));
            var info = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "DocumentInfo");
            Assert.True(info.TranslatePoint(default, window)!.Value.X > window.Bounds.Width / 2);
            Assert.True(Screenshots.Save(window, "movable-properties-and-tools"));
            foreach (var section in dock.Ordered(false).Where(p => p.State.Visible).ToArray())
                dock.Move(section.Title, true, null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(info.TranslatePoint(default, window)!.Value.X < window.Bounds.Width / 2);
            Assert.Equal(window.Bounds.Height, info.TranslatePoint(new Point(0, info.Bounds.Height), window)!.Value.Y, 0);
            Assert.True(Screenshots.Save(window, "document-info-left"));
            window.ApplyWorkspace(window.CaptureWorkspace());
            Assert.Single(window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == "Layers" && t.IsEffectivelyVisible));
        }
        finally { session.MarkSaved("panels.cmps", session.History.CurrentId); window.Close(); }
    }
}
