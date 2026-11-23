using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.Editing;
using SkiaSharp;

namespace Composa.App.Tests;

public sealed class EditorChromeInputTests
{
    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    public void Menu_hover_keeps_the_fitted_and_panned_view_in_place(double scaling)
    {
        var window = new MainWindow { Width = 1100, Height = 680 };
        window.Show();
        try
        {
            window.SetRenderScaling(scaling);
            window.AddSession(EditorSession.NewCanvas(1200, 900, SKColors.White));
            window.SelectTool(Tool.Hand);
            Pump();
            var canvas = window.GetVisualDescendants().OfType<CanvasView>().Single();
            var menu = window.GetVisualDescendants().OfType<Menu>().Single();
            var items = menu.Items.OfType<MenuItem>().Take(5).ToArray();
            Assert.Equal(scaling, window.RenderScaling);
            var detail = new SKPoint(250, 310);

            (Rect Menu, Rect Canvas, PixelPoint CanvasTopLeft, double Zoom, Point Origin, PixelPoint Detail) View() =>
                (menu.Bounds, canvas.Bounds, canvas.PointToScreen(default), canvas.Zoom, canvas.ToScreen(default),
                    canvas.PointToScreen(canvas.ToScreen(detail)));

            void Sweep()
            {
                var before = View();
                int viewChanges = 0;
                void ViewChanged() => viewChanges++;
                canvas.ViewChanged += ViewChanged;
                try
                {
                    foreach (var item in items.Concat(items.Reverse()))
                    {
                        window.MouseMove(Center(item, window));
                        Pump();
                        Assert.False(item.IsSubMenuOpen);
                        Assert.Equal(before, View());
                    }
                    Assert.Equal(0, viewChanges);
                }
                finally { canvas.ViewChanged -= ViewChanged; }
            }

            canvas.Fit();
            Pump();
            Sweep();

            canvas.ZoomTo(1);
            Pump();
            Sweep();
            var beforePan = canvas.ToScreen(default);
            var start = Center(canvas, window);
            var end = start + new Vector(70, 45);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            window.MouseUp(end, MouseButton.Left);
            Pump();
            Assert.NotEqual(beforePan, canvas.ToScreen(default));
            Assert.Equal(1, canvas.Zoom);
            Sweep();

            Click(window, Center(items[0], window));
            Assert.True(items[0].IsSubMenuOpen);
            menu.Close();
            var beforeResize = canvas.Bounds.Size;
            window.Width -= 90;
            Pump();
            Assert.NotEqual(beforeResize, canvas.Bounds.Size);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false, 1.0)]
    [InlineData(false, 1.25)]
    [InlineData(true, 1.0)]
    [InlineData(true, 1.25)]
    public void A_visible_tooltip_allows_the_next_tool_and_its_first_canvas_action(bool bottomRail, double scaling)
    {
        var window = new MainWindow { Width = 1100, Height = 680 };
        window.Show();
        try
        {
            window.SetRenderScaling(scaling);
            var session = EditorSession.NewCanvas(600, 400, SKColors.White);
            window.AddSession(session);
            window.ApplyEditorArrangement(bottomRail ? EditorLayoutPreset.Modern : EditorLayoutPreset.Classic, AiPanelPlacement.UnderSelection);
            Pump();
            foreach (var (from, to) in new[] { (Tool.Brush, Tool.Eraser), (Tool.Marquee, Tool.Lasso) })
            {
                window.SelectTool(from);
                Pump();
                var source = window.RailButton(from);
                var target = window.RailButton(to);
                var at = Center(target, window);
                // Deliberately place the tip over the next button to exercise hit testing.
                Assert.True(ToolTip.GetShouldUseOverlayLayer(source));
                ToolTip.SetPlacement(source, bottomRail
                    ? PlacementMode.RightEdgeAlignedTop : PlacementMode.BottomEdgeAlignedLeft);
                ToolTip.SetVerticalOffset(source, 0);
                window.MouseMove(Center(source, window));
                ToolTip.SetIsOpen(source, true);
                Pump();

                var tip = Assert.Single(window.GetVisualDescendants().OfType<ToolTip>());
                Assert.NotNull(tip.Content);
                var host = Assert.IsType<OverlayPopupHost>(tip.FindAncestorOfType<OverlayPopupHost>());
                Assert.False(host.IsHitTestVisible);
                Assert.True(new Rect(host.Bounds.Size).Contains(window.TranslatePoint(at, host)!.Value));
                var hit = Assert.IsAssignableFrom<Visual>(window.InputHitTest(at));
                Assert.Contains(target, hit.GetSelfAndVisualAncestors());

                Click(window, at);
                Assert.Equal(to, session.Tool);
                Assert.False(target.IsGroupOpen);
                if (to == Tool.Eraser)
                {
                    var canvas = window.GetVisualDescendants().OfType<CanvasView>().Single();
                    var pixel = new SKPoint(300, 200);
                    var point = canvas.TranslatePoint(canvas.ToScreen(pixel), window)!.Value;
                    Assert.Equal(255, session.ActiveLayer!.Pixels!.GetPixel(300, 200).Alpha);
                    Click(window, point);
                    Assert.True(session.ActiveLayer!.Pixels!.GetPixel(300, 200).Alpha < 255);
                }
            }
        }
        finally { window.Close(); }
    }

    private static Point Center(Control control, Window window) => control.TranslatePoint(new Rect(control.Bounds.Size).Center, window)!.Value;

    private static void Click(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Pump();
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
