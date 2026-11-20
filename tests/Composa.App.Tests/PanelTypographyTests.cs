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
    public async Task Russian_document_properties_apply_pixel_size_and_density_and_presets_are_independent()
    {
        var previous = Locale.Language;
        Locale.Language = "ru";
        var window = new MainWindow { Width = 1440, Height = 850 };
        var session = EditorSession.NewCanvas(300, 180);
        try
        {
            window.Show(); window.AddSession(session); session.ClearLayerSelection(); Dispatcher.UIThread.RunJobs();
            var visual = window.GetVisualDescendants();
            Assert.Contains(visual.OfType<MenuItem>(), i => Equals(i.Header, "_Файл"));
            NumericUpDown Field(string name) => window.GetVisualDescendants().OfType<NumericUpDown>()
                .Single(f => Avalonia.Automation.AutomationProperties.GetName(f) == name);
            Field("Width").Value = 360; Field("Height").Value = 200; Field("Resolution").Value = 300;
            var apply = visual.OfType<Button>().Single(b => Equals(b.Content, "Применить"));
            apply.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal((360, 200, 300d), (session.Document.Width, session.Document.Height, session.Document.Resolution));
            Assert.True(Screenshots.Save(window, "russian-document-properties"));
            window.SelectTool(Tool.Brush);
            session.Brush = session.Brush with { Hardness = .73 };
            window.SelectTool(Tool.Eraser);
            session.Brush = session.Brush with { Hardness = .1 };
            Assert.Equal(.73, session.PaintBrush.Hardness);
            var picker = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "BrushPresetPicker");
            var library = Assert.IsType<Flyout>(picker.Flyout);
            library.ShowAt(picker);
            Dispatcher.UIThread.RunJobs();
            Assert.True(Screenshots.Save(window, "russian-eraser-presets"));
            var panel = Assert.IsType<StackPanel>(library.Content);
            var save = panel.Children.OfType<Button>().Single();
            save.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(window.OwnedWindows);
            dialog.GetVisualDescendants().OfType<TextBox>().Single().Text = "Мягкая кромка";
            var confirm = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.IsDefault);
            confirm.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            for (int i = 0; i < 50 && window.Settings.EraserPresets.Count == 0; i++)
            { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.Equal(session.EraserBrush, window.Settings.EraserPresets["Мягкая кромка"]);
            Assert.Empty(window.Settings.BrushPresets);
        }
        finally { Locale.Language = previous; session.MarkSaved("document.cmps", session.History.CurrentId); window.Close(); }
    }

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
    public void Text_properties_remember_sections_accept_typed_font_names_and_scrub_in_the_field()
    {
        var window = new MainWindow { Width = 1440, Height = 1000 };
        window.Show();
        var session = EditorSession.NewCanvas(540, 320, SKColors.White);
        window.AddSession(session);
        var layer = session.AddText(new(10, 30), new TextStyle { Text = "Typography 🦊", Size = 40 });
        try
        {
            var dock = window.GetVisualDescendants().OfType<SideDock>().Single();
            dock.Move("Properties", true, null);
            Dispatcher.UIThread.RunJobs();
            var firstColumn = dock.Columns(true).Single().View;
            Assert.Empty(firstColumn.GetVisualDescendants().OfType<GridSplitter>());
            Assert.Equal(firstColumn.TranslatePoint(default, window)!.Value.Y,
                dock.Section("Properties").Frame!.TranslatePoint(default, window)!.Value.Y, 1);
            Assert.Contains(dock.Columns(false).Single().View.GetVisualDescendants().OfType<GridSplitter>(),
                splitter => splitter.ResizeDirection == GridResizeDirection.Rows);
            T Find<T>(string name) where T : Control => window.GetVisualDescendants().OfType<T>()
                .Single(c => Avalonia.Automation.AutomationProperties.GetName(c) == name);
            Point Center(Control c) => c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), window)!.Value;
            void Click(Control c) { var p = Center(c); window.MouseDown(p, MouseButton.Left); window.MouseUp(p, MouseButton.Left); Dispatcher.UIThread.RunJobs(); }
            Click(Find<Button>("Paragraph")); Click(Find<Button>("Type details"));
            session.SelectLayer(session.Document.Layers[0].Id); session.SelectLayer(layer.Id); Dispatcher.UIThread.RunJobs();
            var gap = Find<NumericUpDown>("Paragraph gap"); Assert.True(gap.IsEffectivelyVisible);
            Assert.True(Find<CheckBox>("Capital spacing").IsEffectivelyVisible);
            Assert.Equal(Avalonia.Layout.VerticalAlignment.Center, gap.VerticalContentAlignment);
            var start = Center(gap);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start.WithX(start.X + 14), RawInputModifiers.LeftMouseButton);
            Assert.True(layer.Text!.Type.ParagraphSpacing > 0);
            window.MouseUp(start.WithX(start.X + 14), MouseButton.Left);
            var combo = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "FontFamilyPicker");
            combo.IsDropDownOpen = true; Dispatcher.UIThread.RunJobs();
            Assert.Contains(combo.GetRealizedContainers().SelectMany(c => c.GetVisualDescendants()).OfType<TextBlock>(),
                t => t.Text == "⚠");
            combo.IsDropDownOpen = false; Dispatcher.UIThread.RunJobs();
            var editor = combo.GetVisualDescendants().OfType<TextBox>().First();
            Assert.Equal(Avalonia.Layout.VerticalAlignment.Center, editor.VerticalContentAlignment);
            Assert.True(editor.Padding.Left > 0);
            var family = EditorSession.FontFamilies.First(f => f != layer.Text.FontFamily);
            editor.Focus(); editor.SelectAll(); window.KeyTextInput(family.ToLowerInvariant());
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(family, layer.Text.FontFamily);
            var wrap = Find<Avalonia.Controls.Primitives.ToggleButton>("Wrap characters");
            var caption = wrap.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Characters");
            Assert.True(wrap.Bounds.Width >= caption.DesiredSize.Width + 12);
            Assert.True(Center(Find<Button>("Type details")).Y < Center(Find<Button>("Paragraph")).Y);
            Assert.True(Screenshots.Save(window, "refined-type-properties"));
        }
        finally { session.MarkSaved("layout.cmps", session.History.CurrentId); window.Close(); }
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
            Assert.True(window.RailButton(Tool.Move).TranslatePoint(default, window)!.Value.Y < 170);
            Drag(header.TranslatePoint(new Point(30, 8), window)!.Value, new Point(45, 150));
            Assert.True(dock.Section("Layers").State.Left);
            Assert.Same(layers, dock.LeftHost.GetVisualDescendants().OfType<LayersPanel>().Single());
            var historyTitle = dock.GetVisualDescendants().OfType<Button>()
                .Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Collapse History");
            var begin = historyTitle.TranslatePoint(new Point(32, 9), window)!.Value;
            var drop = dock.TranslatePoint(new Point(12, 120), window)!.Value;
            window.MouseDown(begin, MouseButton.Left); window.MouseMove(drop, RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(window.GetVisualDescendants(), c => c.Name == "DockDropTarget" && c.Bounds.Width > 20);
            Assert.True(Screenshots.Save(window, "panel-drop-preview"));
            window.MouseUp(drop, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            Assert.True(dock.Section("History").State.Column < dock.Section("Properties").State.Column);
            var rail = window.RailButton(Tool.Move).FindAncestorOfType<ScrollViewer>()!;
            var surface = (Border)rail.Parent!;
            Drag(surface.TranslatePoint(new Point(20, 3), window)!.Value, new Point(730, 820));
            Assert.Equal("Bottom", window.CaptureWorkspace().ToolsPosition);
            Assert.Equal(revision, session.Revision);
            Assert.Single(window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == "Layers" && t.IsEffectivelyVisible));
            Assert.DoesNotContain(window.GetVisualDescendants(), c => c.Name == "DocumentInfo");
            var info = window.GetVisualDescendants().OfType<TextBlock>().Single(b => b.Name == "DocumentZoom");
            var zoomPosition = info.TranslatePoint(default, window);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "(RGB/8)");
            Assert.True(Screenshots.Save(window, "movable-properties-and-tools"));
            foreach (var section in dock.Ordered(false).Where(p => p.State.Visible).ToArray())
                dock.Move(section.Title, true, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(zoomPosition, info.TranslatePoint(default, window));
            window.ApplyWorkspace(window.CaptureWorkspace());
            Assert.Single(window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == "Layers" && t.IsEffectivelyVisible));
        }
        finally { session.MarkSaved("panels.cmps", session.History.CurrentId); window.Close(); }
    }
}
