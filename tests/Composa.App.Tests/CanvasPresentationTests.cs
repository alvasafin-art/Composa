using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class CanvasPresentationTests
{
    [AvaloniaTheory]
    [InlineData(Tool.Brush, .65)]
    [InlineData(Tool.Eraser, 1)]
    [InlineData(Tool.SpotHealing, .65)]
    [InlineData(Tool.SpotHealing, .36, true)]
    public void Live_stroke_and_full_redraw_show_the_same_pixels(Tool tool, double zoom, bool withEffects = false)
    {
        var session = Scene();
        if (withEffects)
        {
            session.ActiveLayer!.Effects = new LayerEffects { Shadow = new ShadowEffect { Blur = 12, Distance = 6 } };
            session.SampleAllLayers = true;
        }
        var window = new MainWindow { Width = 1280, Height = 850 };
        window.Show();
        window.AddSession(session);
        try
        {
            window.SelectTool(tool);
            session.Brush = session.Brush with { Size = 190, Opacity = .7, Hardness = .4 };
            session.Foreground = SKColors.Coral;
            window.Canvas.ZoomTo(zoom);
            window.Canvas.CenterOn(new SKPoint(1220, 900));
            Point At(float x, float y) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(new SKPoint(x, y)), window)!.Value;
            var name = withEffects ? $"presentation-{tool}-effects" : $"presentation-{tool}";
            using var before = Capture(window, $"{name}-before");
            window.MouseDown(At(1160, 850), MouseButton.Left);
            window.MouseMove(At(1290, 870), RawInputModifiers.LeftMouseButton);
            window.MouseMove(At(1330, 915), RawInputModifiers.LeftMouseButton);
            Assert.True(session.IsStroking);
            using var live = Capture(window, $"{name}-live");
            session.InvalidateAll();
            using var redrawn = Capture(window, $"{name}-full");
            var changed = At(1160, 850);
            Assert.NotEqual(before.GetPixel((int)changed.X, (int)changed.Y), live.GetPixel((int)changed.X, (int)changed.Y));
            AssertCanvasPixels(window, redrawn, live);
            window.Canvas.CancelInteraction();
            using var cancelled = Capture(window, $"{name}-cancel");
            Assert.Equal(before.GetPixel((int)changed.X, (int)changed.Y), cancelled.GetPixel((int)changed.X, (int)changed.Y));
        }
        finally
        {
            window.Canvas.CancelInteraction();
            session.MarkSaved("presentation-test.cmps", session.History.CurrentId);
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(.625)]
    [InlineData(1)]
    public async Task Panning_beyond_cached_padding_settles_to_a_fresh_viewport(double zoom)
    {
        var session = Scene();
        var window = new MainWindow { Width = 1280, Height = 850 };
        window.Show();
        window.AddSession(session);
        try
        {
            window.SelectTool(Tool.Hand);
            window.Canvas.ZoomTo(zoom);
            window.Canvas.CenterOn(new SKPoint(1000, 830));
            using var prior = Capture(window, $"presentation-pan-{zoom}-before");
            window.Canvas.CenterOn(new SKPoint(1270, 1010));
            using var shifted = Capture(window, $"presentation-pan-{zoom}-shifted");
            if (zoom < 1)
            {
                await Task.Delay(180, TestContext.Current.CancellationToken);
                Dispatcher.UIThread.RunJobs();
            }
            using var settled = Capture(window, $"presentation-pan-{zoom}-settled");
            session.InvalidateAll();
            using var full = Capture(window, $"presentation-pan-{zoom}-full");
            AssertCanvasPixels(window, full, settled);
            if (zoom == 1) AssertCanvasPixels(window, full, shifted);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Wheel_zoom_keeps_its_anchor_and_settles_to_a_fresh_viewport()
    {
        var session = Scene();
        var window = new MainWindow { Width = 1280, Height = 850 };
        window.Show();
        window.AddSession(session);
        try
        {
            window.Canvas.ZoomTo(.625);
            var fixedPoint = new SKPoint(1280, 950);
            window.Canvas.CenterOn(fixedPoint);
            using var before = Capture(window, "presentation-zoom-before");
            var anchor = window.Canvas.TranslatePoint(window.Canvas.ToScreen(fixedPoint), window)!.Value;
            window.MouseWheel(anchor, new Vector(0, 1), RawInputModifiers.Control);
            Assert.Equal(.75, window.Canvas.Zoom, 6);
            var actual = window.Canvas.TranslatePoint(window.Canvas.ToScreen(fixedPoint), window)!.Value;
            Assert.InRange(Math.Abs(anchor.X - actual.X), 0, 1);
            Assert.InRange(Math.Abs(anchor.Y - actual.Y), 0, 1);
            await Task.Delay(180, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            using var settled = Capture(window, "presentation-zoom-settled");
            session.InvalidateAll();
            using var full = Capture(window, "presentation-zoom-full");
            AssertCanvasPixels(window, full, settled);
        }
        finally { window.Close(); }
    }

    private static EditorSession Scene()
    {
        var session = EditorSession.NewCanvas(2600, 1900);
        var pixels = session.ActiveLayer!.Pixels!;
        using (var canvas = new SKCanvas(pixels))
        using (var paint = new SKPaint { Color = new SKColor(60, 150, 183, 193) })
            for (var x = 12; x < pixels.Width; x += 47)
                canvas.DrawRect(x, 155, 31, 1696, paint);
        Pixels.Invalidate(pixels);
        return session;
    }

    private static SKBitmap Capture(MainWindow window, string name)
    {
        Assert.True(Screenshots.Save(window, name));
        return SKBitmap.Decode(Path.Combine(Screenshots.Folder, name + ".png"));
    }

    private static void AssertCanvasPixels(MainWindow window, SKBitmap expected, SKBitmap actual)
    {
        var origin = window.Canvas.TranslatePoint(default, window)!.Value;
        var width = (int)window.Canvas.Bounds.Width;
        var height = (int)window.Canvas.Bounds.Height;
        for (var y = (int)origin.Y; y < origin.Y + height; y++)
        {
            var left = (int)origin.X * 4;
            Assert.True(expected.GetPixelSpan().Slice(y * expected.RowBytes + left, width * 4)
                .SequenceEqual(actual.GetPixelSpan().Slice(y * actual.RowBytes + left, width * 4)),
                $"Canvas row {y} changed after a complete redraw.");
        }
    }
}
