using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.Editing;
using SkiaSharp;

namespace Composa.App.Tests;

public sealed class EditorChromeInputTests
{
    [AvaloniaFact]
    public async Task Only_the_file_menu_refreshes_recent_paths_when_switching_menus()
    {
        var path = Path.GetTempFileName();
        var window = new MainWindow { Width = 1100, Height = 680 };
        window.Settings.RecentFiles.Add(path);
        window.Show();
        try
        {
            Pump();
            var menu = window.GetVisualDescendants().OfType<Menu>().Single();
            var files = menu.Items.OfType<MenuItem>().First();
            var recent = files.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Open Recent"));
            await WaitForRecent(() => recent.Items.Count > 0);
            var previous = Assert.Single(recent.Items);

            File.Delete(path);
            foreach (var neighboring in menu.Items.OfType<MenuItem>().Skip(1).Take(5))
                neighboring.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent));
            Pump();
            Assert.Same(previous, Assert.Single(recent.Items));

            files.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent));
            await WaitForRecent(() => recent.Items.Count == 0);
            Assert.False(recent.IsEnabled);
        }
        finally
        {
            window.Close();
            File.Delete(path);
        }

        static async Task WaitForRecent(Func<bool> ready)
        {
            for (var i = 0; !ready() && i < 100; i++)
            {
                Pump();
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
            Assert.True(ready());
        }
    }

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
    [InlineData(false)]
    [InlineData(true)]
    public void A_visible_tooltip_allows_a_click_on_its_own_button(bool icon)
    {
        const string caption = "A tooltip over its own button";
        int clicks = 0;
        var button = icon ? Ui.IconButton(Icons.Plus, caption, () => clicks++) : Ui.TextButton("Add", () => clicks++);
        if (!icon) Locale.Tip(button, caption);
        button.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        button.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        ToolTip.SetPlacement(button, PlacementMode.Center);
        ToolTip.SetVerticalOffset(button, 0);
        var window = new Window { Width = 360, Height = 240, Content = button };
        window.Show();
        try
        {
            Pump();
            var at = Center(button, window);
            window.MouseMove(at);
            ToolTip.SetIsOpen(button, true);
            Pump();

            var tip = Assert.Single(window.GetVisualDescendants().OfType<ToolTip>());
            Assert.Equal(caption, tip.Content);
            var host = Assert.IsType<OverlayPopupHost>(tip.FindAncestorOfType<OverlayPopupHost>());
            Assert.False(host.IsHitTestVisible);
            Assert.True(new Rect(host.Bounds.Size).Contains(window.TranslatePoint(at, host)!.Value));
            var hit = Assert.IsAssignableFrom<Visual>(window.InputHitTest(at));
            Assert.Contains(button, hit.GetSelfAndVisualAncestors());

            Click(window, at);
            Assert.Equal(1, clicks);
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
            foreach (var (from, to) in new[] { (Tool.Brush, Tool.Eraser), (Tool.Lasso, Tool.Marquee) })
            {
                window.SelectTool(from);
                Pump();
                var source = window.RailButton(from);
                var target = window.RailButton(to);
                var at = Center(target, window);
                // Deliberately place the tip over the next button to exercise hit testing.
                Assert.True(ToolTip.GetShouldUseOverlayLayer(source));
                var fromPoint = Center(source, window);
                ToolTip.SetPlacement(source, PlacementMode.Center);
                ToolTip.SetHorizontalOffset(source, at.X - fromPoint.X);
                ToolTip.SetVerticalOffset(source, at.Y - fromPoint.Y);
                window.MouseMove(fromPoint);
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
