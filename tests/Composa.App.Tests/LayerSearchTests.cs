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

public class LayerSearchTests
{
    [AvaloniaFact]
    public void Search_combines_words_exact_tags_and_kind_without_changing_collapsed_groups_pixels_or_history()
    {
        var window = new MainWindow { Width = 1280, Height = 900 }; window.Show();
        var session = EditorSession.NewCanvas(500, 320, SKColors.White, ColorDepth.Sixteen); window.AddSession(session);
        try
        {
            var group = Layer.Group("Campaign"); group.Collapsed = true;
            var product = Layer.Raster("Red bottle", Composa.Rendering.Pixels.NewColor(30, 30)); product.Tags.Add("product");
            var other = Layer.Raster("Red reflection", Composa.Rendering.Pixels.NewColor(30, 30)); other.Tags.Add("product-image");
            group.Children.Add(product); group.Children.Add(other); session.Document.Layers.Add(group); session.NotifyLayersChanged(); Pump();
            var panel = Panel(window); var query = Query(panel); var kind = Kind(panel); var state = session.History.CurrentId;
            using var before = session.Flatten();
            query.Text = "RED #PRODUCT"; Pump(); Assert.Equal(new[] { group.Id, product.Id }, Rows(panel).Select(row => ((Layer)row.Tag!).Id)); Assert.True(group.Collapsed);
            Assert.True(Screenshots.Save(window, "layers-search-tag-rgb16"));
            kind.SelectedIndex = 2; Pump(); Assert.Empty(Rows(panel));
            kind.SelectedIndex = 1; Pump(); Assert.Equal(2, Rows(panel).Count);
            query.Focus(); window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); Pump(); Assert.Equal("", query.Text); Assert.Equal(0, kind.SelectedIndex);
            Assert.Contains(Rows(panel), row => ReferenceEquals(row.Tag, group)); Assert.DoesNotContain(Rows(panel), row => ReferenceEquals(row.Tag, product)); Assert.True(group.Collapsed);
            using var after = session.Flatten(); Assert.Equal(before.Bytes, after.Bytes); Assert.Equal(state, session.History.CurrentId);
        }
        finally { session.MarkSaved("search.cmps", session.History.CurrentId); window.Close(); }
    }

    [AvaloniaFact]
    public void Shift_select_and_drag_in_filtered_rows_leave_excluded_layers_and_order_untouched()
    {
        var window = new MainWindow { Width = 1280, Height = 900 }; window.Show();
        var session = EditorSession.NewCanvas(500, 320); window.AddSession(session);
        try
        {
            var a = session.AddBlankLayer(); session.Rename(a, "Product A"); var hidden = session.AddBlankLayer(); session.Rename(hidden, "Reflection"); var b = session.AddBlankLayer(); session.Rename(b, "Product B"); Pump();
            var panel = Panel(window); Query(panel).Text = "product"; Pump(); var originalOrder = session.Document.Layers.Select(layer => layer.Id).ToArray(); var state = session.History.CurrentId;
            Click(window, Rows(panel)[0]); Click(window, Rows(panel)[1], RawInputModifiers.Shift);
            Assert.Equal(2, session.Document.SelectedLayerIds.Count); Assert.Contains(a.Id, session.Document.SelectedLayerIds); Assert.Contains(b.Id, session.Document.SelectedLayerIds); Assert.DoesNotContain(hidden.Id, session.Document.SelectedLayerIds);
            var start = Center(window, Rows(panel)[0]); var end = Center(window, Rows(panel)[1]);
            window.MouseDown(start, MouseButton.Left); window.MouseMove(end, RawInputModifiers.LeftMouseButton); window.MouseUp(end, MouseButton.Left); Pump();
            Assert.Equal(originalOrder, session.Document.Layers.Select(layer => layer.Id)); Assert.Equal(state, session.History.CurrentId);
            session.SelectLayer(hidden.Id); Pump(); Assert.Contains(panel.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.Contains("1 selected outside filter") == true);
            session.Rename(b, "Other"); Pump(); Assert.Single(Rows(panel)); session.Undo(); Pump(); Assert.Equal(2, Rows(panel).Count);
        }
        finally { session.MarkSaved("search.cmps", session.History.CurrentId); window.Close(); }
    }

    private static LayersPanel Panel(MainWindow window) => window.GetVisualDescendants().OfType<LayersPanel>().Single();
    private static TextBox Query(LayersPanel panel) => panel.GetVisualDescendants().OfType<TextBox>().Single(box => box.Watermark == "Find layers / #tag");
    private static ComboBox Kind(LayersPanel panel) => panel.GetVisualDescendants().OfType<ComboBox>().Single(box => box.ItemsSource is string[]);
    private static List<Border> Rows(LayersPanel panel) => panel.GetVisualDescendants().OfType<Border>().Where(row => row.Tag is Layer).ToList();
    private static Point Center(MainWindow window, Border row) => row.TranslatePoint(new Point(150, row.Bounds.Height / 2), window)!.Value;
    private static void Click(MainWindow window, Border row, RawInputModifiers modifiers = RawInputModifiers.None) { var point = Center(window, row); window.MouseDown(point, MouseButton.Left, modifiers); window.MouseUp(point, MouseButton.Left, modifiers); Pump(); }
    private static void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
}
