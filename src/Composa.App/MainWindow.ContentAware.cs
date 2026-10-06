using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Composa.App.Dialogs;
using Composa.Painting;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.App;

public sealed partial class MainWindow
{
    private async Task ContentAwareFillWorkspace()
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
        using var excluded = Pixels.NewMask(original.Width, original.Height);
        var previewScale = Math.Min(1, 960.0 / Math.Max(original.Width, original.Height));
        using var previewImage = Pixels.NewColor(Math.Max(1, (int)Math.Round(original.Width * previewScale)), Math.Max(1, (int)Math.Round(original.Height * previewScale)));
        var sample = new Image { Width = 460, Height = 360, Stretch = Stretch.Uniform, Cursor = new Cursor(StandardCursorType.Cross) };
        var resultView = new Image { Width = 460, Height = 360, Stretch = Stretch.Uniform };
        Avalonia.Media.Imaging.Bitmap? samplingBitmap = null, resultBitmap = null;
        var erase = false; var outputLayer = true; var ready = false; var painting = false; var filling = false; SKPoint? lastPoint = null;
        using var workspaceCancellation = new CancellationTokenSource();
        DialogWindow? dialog = null; Task? previewTask = null;
        var radius = 30.0;
        static Avalonia.Media.Imaging.Bitmap Display(SKBitmap pixels)
        {
            var scale = Math.Min(1, 960.0 / Math.Max(pixels.Width, pixels.Height));
            using var small = Pixels.NewColor(Math.Max(1, (int)Math.Round(pixels.Width * scale)), Math.Max(1, (int)Math.Round(pixels.Height * scale)));
            using (var canvas = new SKCanvas(small)) canvas.DrawBitmap(pixels, new SKRect(0, 0, small.Width, small.Height));
            using var image = SKImage.FromBitmap(small); using var png = image.Encode(SKEncodedImageFormat.Png, 100); using var stream = png.AsStream(); return new(stream);
        }
        void RefreshSampling(SKPoint? pointer = null)
        {
            using var canvas = new SKCanvas(previewImage); canvas.Clear(); canvas.Scale((float)previewScale); canvas.DrawBitmap(original, 0, 0);
            using var tint = new SKPaint { Color = new SKColor(50, 215, 125, 60) }; canvas.DrawRect(0, 0, original.Width, original.Height, tint);
            using var red = new SKPaint { Color = new SKColor(250, 75, 90, 150) }; canvas.DrawBitmap(excluded, 0, 0, red);
            using var purple = new SKPaint { Color = new SKColor(170, 80, 255, 160) }; canvas.DrawBitmap(mask, 0, 0, purple);
            if (pointer is { } center)
            {
                using var outline = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = (float)(2 / previewScale), IsAntialias = true };
                canvas.DrawCircle(center, (float)radius, outline);
            }
            var next = Display(previewImage); sample.Source = next; samplingBitmap?.Dispose(); samplingBitmap = next;
        }
        SKPoint? Position(Point p)
        {
            var scale = Math.Min(sample.Bounds.Width / original.Width, sample.Bounds.Height / original.Height);
            if (scale <= 0) return null;
            var x = (p.X - (sample.Bounds.Width - original.Width * scale) / 2) / scale; var y = (p.Y - (sample.Bounds.Height - original.Height * scale) / 2) / scale;
            return x >= 0 && y >= 0 && x < original.Width && y < original.Height ? new SKPoint((float)x, (float)y) : null;
        }
        void Paint(SKPoint point)
        {
            using var canvas = new SKCanvas(excluded); using var paint = new SKPaint { Color = erase ? SKColors.Transparent : SKColors.White, BlendMode = SKBlendMode.Src, StrokeWidth = (float)radius * 2, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
            if (lastPoint is { } previous) canvas.DrawLine(previous, point, paint); else canvas.DrawCircle(point, (float)radius, paint);
            lastPoint = point; ready = false; RefreshSampling(point);
        }
        sample.PointerPressed += (_, e) => { if (!e.GetCurrentPoint(sample).Properties.IsLeftButtonPressed || Position(e.GetPosition(sample)) is not { } p) return; painting = true; lastPoint = null; Paint(p); e.Pointer.Capture(sample); };
        sample.PointerMoved += (_, e) => { if (Position(e.GetPosition(sample)) is { } p) { if (painting) Paint(p); else RefreshSampling(p); } };
        sample.PointerReleased += (_, e) => { painting = false; lastPoint = null; e.Pointer.Capture(null); };
        async Task Preview()
        {
            if (filling) { if (previewTask != null) await previewTask; return; }
            filling = true; sample.IsEnabled = false; if (dialog != null) dialog.CanAccept = false;
            try
            {
                using var sampling = Pixels.Clone(excluded);
                var filled = await ProgressWindow.Run(dialog?.IsVisible == true ? dialog : this, "Filling from the sampling area…", async c =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(c, workspaceCancellation.Token);
                    return await Task.Run(() => Inpaint.Fill(original, mask, sampling, linked.Token), linked.Token);
                });
                if (filled == null) return;
                using (filled)
                {
                    if (workspaceCancellation.IsCancellationRequested || !target.IsPreviewing) return;
                    target.PreviewComputedFill(filled); var next = Display(filled); resultView.Source = next; resultBitmap?.Dispose(); resultBitmap = next; ready = true;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { ShowProblem("Content-Aware Fill failed: " + error.Message); }
            finally { filling = false; sample.IsEnabled = true; if (dialog != null) dialog.CanAccept = true; }
        }
        RefreshSampling(); resultBitmap = Display(original); resultView.Source = resultBitmap;
        var tools = Ui.Row(10, Ui.Combo(new[] { "Exclude sampling", "Include sampling" }, "Exclude sampling", s => s, s => erase = s == "Include sampling", 170),
            Ui.SliderField("Brush size", radius * 2, 4, 500, v => radius = v / 2, width: 140), Ui.TextButton("Reset sampling", () => { if (filling) return; excluded.Erase(SKColors.Transparent); ready = false; RefreshSampling(); }),
            Ui.Check("Output to new layer", true, v => outputLayer = v), Ui.TextButton("Preview", () => { if (!filling) previewTask = Preview(); }, accent: true));
        dialog = new DialogWindow("Content-Aware Fill", Ui.Column(12, Ui.Label("Green: sampling area · Red: excluded · Purple: repair area", Palette.Secondary), tools, Ui.Row(14, sample, resultView)));
        try
        {
            if (!await dialog.Ask(this)) { target.CancelPreview(); return; }
            if (!ready) await Preview();
            if (!ready) { target.CancelPreview(); return; }
            if (outputLayer)
            {
                var pixels = target.ActiveLayer!.Pixels!; var transform = target.ActiveLayer.Transform;
                var repair = Pixels.RepairLayer(original, pixels, mask); target.CancelPreview(); target.AddRetouchLayer("Content-aware repair", repair, transform);
            }
            else target.CommitPreview();
        }
        catch (Exception error) { target.CancelPreview(); ShowProblem("Content-Aware Fill failed: " + error.Message); }
        finally { workspaceCancellation.Cancel(); if (previewTask != null) await previewTask; samplingBitmap?.Dispose(); resultBitmap?.Dispose(); }
    }
}
