using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Composa.App.Dialogs;
using Composa.Painting;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.App;

public sealed partial class MainWindow
{
    private Task ContentAwareFillWorkspace() => RunContentAwareFill(interactive: true);

    private async Task RunContentAwareFill(bool interactive)
    {
        var target = session!; var selected = target.Selection;
        if (selected == null || !target.CanEditPixels || target.IsEditingMask) return;
        var bounds = SelectionMask.Bounds(selected);
        if ((long)bounds.Width * bounds.Height > Inpaint.MaxArea) { ShowProblem("Select a smaller area for Content-Aware Fill."); return; }
        if (!target.BeginPreview("Content-Aware Fill", coverCanvas: true)) return;
        var original = target.PreviewOriginal!; var layer = target.ActiveLayer!;
        if (!target.TargetMatrix(layer).TryInvert(out var inverse)) { target.CancelPreview(); return; }
        using var mask = SelectionMask.Remap(selected, original.Width, original.Height, inverse);
        if (mask == null) { target.CancelPreview(); return; }
        void CommitRepairLayer()
        {
            var pixels = layer.Pixels!;
            var transform = layer.Transform;
            var repair = Pixels.RepairLayer(original, pixels, mask);
            target.CancelPreview();
            target.AddRetouchLayer("Content-aware repair", repair, transform);
        }
        if (!interactive)
        {
            var surface = (Control)Content!;
            surface.IsEnabled = false;
            try
            {
                using var filled = await ProgressWindow.Run(this, "Filling selection…",
                    ct => Task.Run(() => Inpaint.Fill(original, mask, cancellation: ct), ct));
                if (!target.IsPreviewing || target.PreviewOriginal != original || target.ActiveLayer != layer) return;
                if (filled == null) { target.CancelPreview(); return; }
                target.PreviewComputedFill(filled);
                CommitRepairLayer();
            }
            catch (OperationCanceledException) { target.CancelPreview(); }
            catch (Exception error) { target.CancelPreview(); ShowProblem("Content-Aware Fill failed: " + error.Message); }
            finally { surface.IsEnabled = true; }
            return;
        }
        using var excluded = Pixels.NewMask(original.Width, original.Height);
        var previewScale = Math.Min(1, 960.0 / Math.Max(original.Width, original.Height));
        var fullView = SKRectI.Create(original.Width, original.Height);
        var detailView = SelectionMask.Bounds(mask, 1);
        int padding = Math.Max(32, Math.Max(detailView.Width, detailView.Height) / 2);
        detailView.Inflate(padding, padding);
        detailView = SKRectI.Intersect(fullView, detailView);
        bool zoomToSelection = (long)detailView.Width * detailView.Height * 4 < (long)fullView.Width * fullView.Height;
        var view = zoomToSelection ? detailView : fullView;
        var sample = new Image { Stretch = Stretch.Uniform, Cursor = new Cursor(StandardCursorType.Cross) };
        var resultView = new Image { Stretch = Stretch.Uniform };
        Avalonia.Media.Imaging.Bitmap? samplingBitmap = null, resultBitmap = null;
        var erase = false; var outputLayer = true; var ready = false; var painting = false; var closing = false; SKPoint? lastPoint = null;
        using var workspaceCancellation = new CancellationTokenSource();
        DialogWindow? dialog = null;
        Task previewTask = Task.CompletedTask;
        CancellationTokenSource? request = null;
        var status = Ui.Label("Preview", Palette.Secondary);
        var brushRing = new Avalonia.Controls.Shapes.Ellipse { Stroke = Brushes.White, StrokeThickness = 1, IsVisible = false };
        var overlay = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
        overlay.Children.Add(brushRing);
        var radius = 30.0;
        // Review thin glyphs and small repairs at the same resolution that Apply commits.
        var draft = (long)detailView.Width * detailView.Height > 1_500_000;
        unsafe WriteableBitmap Display(SKBitmap pixels, bool showSampling = false)
        {
            var scale = Math.Min(1, 960.0 / Math.Max(view.Width, view.Height));
            using var small = Pixels.NewWorkingColor(Math.Max(1, (int)Math.Round(view.Width * scale)), Math.Max(1, (int)Math.Round(view.Height * scale)), Composa.Model.ColorDepth.Eight, DisplayProfile);
            using (var canvas = new SKCanvas(small))
            {
                canvas.Scale((float)small.Width / view.Width, (float)small.Height / view.Height);
                canvas.Translate(-view.Left, -view.Top);
                canvas.DrawBitmap(pixels, (SKRect)fullView);
                if (showSampling)
                {
                    using var green = new SKPaint { Color = new SKColor(50, 215, 125, 60) };
                    using var red = new SKPaint { Color = new SKColor(250, 75, 90, 150) };
                    using var purple = new SKPaint { Color = new SKColor(170, 80, 255, 160) };
                    canvas.DrawRect((SKRect)view, green);
                    canvas.DrawBitmap(excluded, 0, 0, red);
                    canvas.DrawBitmap(mask, 0, 0, purple);
                }
            }
            var bitmap = new WriteableBitmap(new PixelSize(small.Width, small.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Premul);
            using var frame = bitmap.Lock();
            for (var y = 0; y < small.Height; y++)
                Buffer.MemoryCopy((byte*)small.GetPixels() + (long)y * small.RowBytes, (byte*)frame.Address + (long)y * frame.RowBytes, frame.RowBytes, small.Width * 4L);
            return bitmap;
        }
        void RefreshSampling()
        {
            var next = Display(original, showSampling: true); sample.Source = next; samplingBitmap?.Dispose(); samplingBitmap = next;
        }
        SKPoint? Position(Point p)
        {
            var scale = Math.Min(sample.Bounds.Width / view.Width, sample.Bounds.Height / view.Height);
            if (scale <= 0) return null;
            var x = (p.X - (sample.Bounds.Width - view.Width * scale) / 2) / scale; var y = (p.Y - (sample.Bounds.Height - view.Height * scale) / 2) / scale;
            return x >= 0 && y >= 0 && x < view.Width && y < view.Height ? new SKPoint(view.Left + (float)x, view.Top + (float)y) : null;
        }
        void Paint(SKPoint point)
        {
            using var canvas = new SKCanvas(excluded); using var paint = new SKPaint { Color = erase ? SKColors.Transparent : SKColors.White, BlendMode = SKBlendMode.Src, StrokeWidth = (float)radius * 2, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
            if (lastPoint is { } previous) canvas.DrawLine(previous, point, paint); else canvas.DrawCircle(point, (float)radius, paint);
            lastPoint = point; ready = false; RefreshSampling();
        }
        sample.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(sample).Properties.IsLeftButtonPressed || Position(e.GetPosition(sample)) is not { } p) return;
            request?.Cancel(); painting = true; lastPoint = null; Paint(p); e.Pointer.Capture(sample);
        };
        sample.PointerMoved += (_, e) =>
        {
            var point = e.GetPosition(sample);
            var position = Position(point);
            brushRing.IsVisible = position.HasValue;
            if (position is not { } p) return;
            var diameter = radius * 2 * Math.Min(sample.Bounds.Width / view.Width, sample.Bounds.Height / view.Height);
            brushRing.Width = brushRing.Height = diameter;
            Avalonia.Controls.Canvas.SetLeft(brushRing, point.X - diameter / 2); Avalonia.Controls.Canvas.SetTop(brushRing, point.Y - diameter / 2);
            if (painting) Paint(p);
        };
        sample.PointerExited += (_, _) => brushRing.IsVisible = false;
        void EndSamplingStroke()
        {
            if (!painting) return;
            painting = false; lastPoint = null; QueuePreview();
        }
        sample.PointerReleased += (_, e) => { EndSamplingStroke(); e.Pointer.Capture(null); };
        sample.PointerCaptureLost += (_, _) => EndSamplingStroke();

        void QueuePreview()
        {
            if (closing) return;
            ready = false;
            request?.Cancel();
            var next = CancellationTokenSource.CreateLinkedTokenSource(workspaceCancellation.Token);
            request = next;
            previewTask = RunAfter(previewTask, next);
        }
        async Task RunAfter(Task previous, CancellationTokenSource current)
        {
            try
            {
                await previous;
                await Task.Delay(100, current.Token);
                await Preview(false, current.Token);
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (request == current) request = null;
                current.Dispose();
            }
        }
        async Task Preview(bool final, CancellationToken token)
        {
            status.Text = final ? "Applying full-resolution fill…" : "Updating preview…";
            try
            {
                var scale = !final && draft ? previewScale : 1;
                SKBitmap Resize(SKBitmap image, bool alpha)
                {
                    var w = Math.Max(1, (int)Math.Round(image.Width * scale)); var h = Math.Max(1, (int)Math.Round(image.Height * scale));
                    var resized = alpha ? Pixels.NewMask(w, h) : Pixels.NewColor(w, h);
                    using var draw = new SKCanvas(resized); draw.DrawBitmap(image, new SKRect(0, 0, w, h));
                    return resized;
                }
                using var sampling = SelectionMask.Bounds(excluded, 1).IsEmpty ? null : Pixels.Clone(excluded);
                Task<SKBitmap> Compute(CancellationToken ct) => Task.Run(() =>
                {
                    using var inputImage = scale < 1 ? Resize(original, false) : null;
                    using var inputMask = scale < 1 ? Resize(mask, true) : null;
                    using var inputSampling = scale < 1 && sampling != null ? Resize(sampling, true) : null;
                    return Inpaint.Fill(inputImage ?? original, inputMask ?? mask, inputSampling ?? sampling, ct);
                }, ct);
                var filled = final ? await ProgressWindow.Run(this, "Filling from the sampling area…", Compute) : await Compute(token);
                if (filled == null) return;
                using (filled)
                {
                    if (token.IsCancellationRequested || !target.IsPreviewing) return;
                    if (scale == 1) target.PreviewComputedFill(filled);
                    var next = Display(filled); resultView.Source = next; resultBitmap?.Dispose(); resultBitmap = next; ready = scale == 1;
                    status.Text = ready ? "Full-resolution result" : "Draft preview";
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { status.Text = "Preview failed"; ShowProblem("Content-Aware Fill failed: " + error.Message); }
        }
        RefreshSampling(); resultBitmap = Display(original); resultView.Source = resultBitmap;
        var tools = Ui.Row(10, Ui.Combo(new[] { "Exclude sampling", "Include sampling" }, "Exclude sampling", s => s, s => erase = s == "Include sampling", 170),
            Ui.SliderField("Brush size", radius * 2, 4, 500, v => radius = v / 2, width: 140), Ui.TextButton("Reset sampling", () => { excluded.Erase(SKColors.Transparent); RefreshSampling(); QueuePreview(); }),
            Ui.Check("Zoom to selection", zoomToSelection, v =>
            {
                view = v ? detailView : fullView;
                RefreshSampling();
                var next = Display(ready ? layer.Pixels! : original);
                resultView.Source = next; resultBitmap?.Dispose(); resultBitmap = next;
                if (!ready) QueuePreview();
            }),
            Ui.Check("Fast preview", draft, v => { draft = v; QueuePreview(); }),
            Ui.Check("Output to new layer", true, v => outputLayer = v));
        var toolWrap = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        var toolItems = tools.Children.ToArray(); tools.Children.Clear();
        foreach (var control in toolItems) { control.Margin = new Thickness(0, 0, 10, 6); toolWrap.Children.Add(control); }
        var views = new Grid { ColumnDefinitions = new ColumnDefinitions("*,14,*") };
        views.RowDefinitions = new RowDefinitions("Auto,*");
        views.Children.Add(Ui.Label("Sampling area", Palette.Secondary));
        Grid.SetColumn(status, 2); views.Children.Add(status);
        Grid.SetRow(sample, 1); views.Children.Add(sample); Grid.SetRow(overlay, 1); views.Children.Add(overlay);
        Grid.SetColumn(resultView, 2); Grid.SetRow(resultView, 1); views.Children.Add(resultView);
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        body.Children.Add(Ui.Label("Green: sampling area · Red: excluded · Purple: repair area · Final fill uses full resolution", Palette.Secondary));
        Grid.SetRow(toolWrap, 1); toolWrap.Margin = new Thickness(0, 10, 0, 10); body.Children.Add(toolWrap);
        Grid.SetRow(views, 2); body.Children.Add(views);
        dialog = new DialogWindow("Content-Aware Fill", body) { CanResize = true, SizeToContent = SizeToContent.Manual,
            Width = Math.Max(900, Math.Min(1500, Width * .92)), Height = Math.Max(560, Math.Min(1000, Height * .9)), MinWidth = 900, MinHeight = 560 };
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(20) };
        var oldLayout = (StackPanel)dialog.Content!; var buttons = oldLayout.Children[1];
        oldLayout.Children.Clear(); layout.Children.Add(body); Grid.SetRow(buttons, 1); layout.Children.Add(buttons); dialog.Content = layout;
        dialog.Opened += (_, _) => QueuePreview();
        try
        {
            var accepted = await dialog.Ask(this);
            closing = true;
            if (!accepted || !ready) request?.Cancel();
            await previewTask;
            if (!accepted) { target.CancelPreview(); return; }
            if (!ready) await Preview(true, workspaceCancellation.Token);
            if (!ready) { target.CancelPreview(); return; }
            if (outputLayer) CommitRepairLayer();
            else target.CommitPreview();
        }
        catch (Exception error) { target.CancelPreview(); ShowProblem("Content-Aware Fill failed: " + error.Message); }
        finally { workspaceCancellation.Cancel(); await previewTask; samplingBitmap?.Dispose(); resultBitmap?.Dispose(); }
    }
}
