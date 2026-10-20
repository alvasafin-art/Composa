using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Composa.App.Controls;
using Composa.ColorManagement;
using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using Composa.Vision;
using SkiaSharp;

namespace Composa.App.Dialogs;

/// <summary>Local trimap editing and cancellable previews. All source/document pixels remain immutable.</summary>
internal sealed class SelectMaskDialog : DialogWindow, IAsyncDisposable
{
    private SKBitmap source, smallSource;
    private readonly SKBitmap activeSource, activeSmall, original, smallMask, marks;
    private SKBitmap? canvasSource, canvasSmall;
    private Task<SKBitmap>? canvasTask;
    private readonly Func<SKBitmap>? visibleCanvas;
    private readonly bool activeLayerSource;
    private bool requestedAll, loadingSource;
    public bool UsesActiveLayer => activeLayerSource && !requestedAll;
    private readonly RgbProfile displayProfile;
    private readonly MaskRefineView view;
    private readonly TextBlock status = new() { Name = "RefineStatus", Text = "Preview pending…", Foreground = Palette.Secondary };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<Task> tasks = [];
    private CancellationTokenSource? previewCancellation;
    private readonly List<List<(SKPoint From, SKPoint To, double Size, MattingMark Mark)>> strokes = [];
    private readonly ComboBox methodMenu, outputMenu;
    private readonly Control body;
    private readonly double scale;
    private MaskRefinementSettings settings = new();
    private string method = "Image edges", background = "Gray", brush = "Refine edge";
    private int radius = 8, generation;
    private bool showMarks = true, closed, finalizing, fullPreview;
    private double decontamination;
    public string Output { get; private set; } = "Selection";
    public SKBitmap? ResultMask { get; private set; }
    public SKBitmap? ResultColors { get; private set; }
    internal MaskRefineView View => view;

    // source is an owned immutable snapshot, supplied by the caller. The dialog owns its preview copies only.
    public SelectMaskDialog(SKBitmap source, SKBitmap selection, RgbProfile displayProfile, bool activeLayerSource, Func<SKBitmap>? visibleCanvas = null)
        : base("Select and Mask", new Border())
    {
        this.source = activeSource = source; original = selection; this.displayProfile = displayProfile;
        this.visibleCanvas = visibleCanvas; this.activeLayerSource = activeLayerSource;
        scale = Math.Min(1, 1000.0 / Math.Max(source.Width, source.Height));
        smallSource = activeSmall = Resize(source, false); smallMask = Resize(selection, true);
        marks = Pixels.NewMask(source.Width, source.Height);
        view = new MaskRefineView(source.Width, source.Height) { Name = "RefinePreview", Focusable = true, MinHeight = 250, Cursor = new Cursor(StandardCursorType.Cross) };
        var methods = new List<string> { "Keep edge", "Image edges", "Alpha matting" };
        if (TrimapMatting.IsAvailable) { methods.AddRange(["AI matting", "AI fine detail"]); method = "AI matting"; }
        methodMenu = Ui.Combo(methods, method, v => v, v => { method = v; RequestPreview(); }, 220);
        ToolTip.SetTip(methodMenu, "AI fine detail also examines uncertain areas at their original scale. Useful for fine mesh; slower. Compare the result at 100%.");
        methodMenu.Name = "RefineMethod";
        outputMenu = Ui.Combo(new[] { "Selection", "Layer mask", "New layer with mask" }, Output, v => v, v => Output = v, 220);
        outputMenu.Name = "RefineOutput";
        var fullResolution = Ui.Check("Full-resolution preview", false, v => { fullPreview = v; RequestPreview(); });
        var allLayers = Ui.Check("Sample all visible layers", !activeLayerSource, v => { requestedAll = v; Track(ChangeSource()); }); allLayers.IsEnabled = activeLayerSource && visibleCanvas != null;
        var controls = Ui.Column(10, allLayers,
            Ui.Label("Preview", Palette.Secondary),
            Ui.Combo(new[] { "Gray", "Black", "White", "Mask", "Overlay", "Trimap", "Original" }, background, v => v, v => { background = v; RequestPreview(); }, 220),
            Ui.Row(8, Ui.TextButton("Fit", view.Fit), Ui.TextButton("100%", () => { view.ActualSize(); fullResolution.IsChecked = true; })), fullResolution,
            Ui.Label("Refinement", Palette.Secondary), methodMenu,
            Ui.SliderField("Edge radius", radius, 0, 100, v => { radius = (int)v; RequestPreview(); }, width: 220, reset: 0),
            Ui.SliderField("Smooth", 0, 0, 20, v => { settings = settings with { Smooth = v }; RequestPreview(); }, width: 220, reset: 0),
            Ui.SliderField("Feather", 0, 0, 100, v => { settings = settings with { Feather = v }; RequestPreview(); }, width: 220, reset: 0),
            Ui.SliderField("Contrast", 0, 0, 100, v => { settings = settings with { Contrast = v }; RequestPreview(); }, width: 220, reset: 0),
            Ui.SliderField("Shift edge", 0, -100, 100, v => { settings = settings with { Shift = (int)v }; RequestPreview(); }, width: 220, reset: 0),
            Ui.Label("Local brush", Palette.Secondary),
            Ui.Combo(new[] { "Refine edge", "Keep foreground", "Remove background", "Erase local mark" }, brush, v => v, v => brush = v, 220),
            Ui.SliderField("Brush size", 30, 1, 500, v => { view.BrushSize = v; view.InvalidateVisual(); }, width: 220, reset: 30),
            Ui.Row(8, Ui.TextButton("Undo stroke", UndoStroke), Ui.TextButton("Reset marks", () => { if (finalizing) return; strokes.Clear(); marks.Erase(SKColors.Transparent); Pixels.Invalidate(marks); RequestPreview(); })),
            Ui.Check("Show local marks", true, v => { showMarks = v; RequestPreview(); }),
            Ui.Label("Output", Palette.Secondary),
            Ui.SliderField("Decontaminate", 0, 0, 100, v => { decontamination = v / 100; if (v > 0) outputMenu.SelectedItem = "New layer with mask"; outputMenu.IsEnabled = v == 0; RequestPreview(); }, width: 220, reset: 0), outputMenu);
        var grid = new Grid { ColumnDefinitions = new("*,18,250"), RowDefinitions = new("Auto,*,Auto") };
        var sourceHint = activeLayerSource ? "New layer output hides its source when sampling only the active layer" : "Source: visible canvas";
        var help = Ui.Label(sourceHint + " · Wheel: zoom · Middle drag: pan · Alt: background brush", Palette.Secondary); help.TextWrapping = TextWrapping.Wrap;
        Grid.SetColumnSpan(help, 3); grid.Children.Add(help);
        Grid.SetRow(view, 1); grid.Children.Add(view);
        var scroll = new ScrollViewer { Content = controls, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetColumn(scroll, 2); Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        Grid.SetRow(status, 2); Grid.SetColumnSpan(status, 3); grid.Children.Add(status); body = grid;
        var layout = new Grid { RowDefinitions = new("*,Auto"), Margin = new Thickness(20) };
        var old = (StackPanel)Content!; old.Children.Clear(); layout.Children.Add(body); Grid.SetRow(Buttons, 1); layout.Children.Add(Buttons); Content = layout;
        CanResize = true; SizeToContent = SizeToContent.Manual; Width = 1150; Height = 820; MinWidth = 850; MinHeight = 560;
        view.StrokeStarted += () => { if (!finalizing) { strokes.Add([]); if (method is not ("AI matting" or "AI fine detail")) methodMenu.SelectedItem = "Alpha matting"; previewCancellation?.Cancel(); timer.Stop(); } };
        view.Stroke += Paint;
        view.StrokeEnded += () => { if (!finalizing) RequestPreview(); };
        timer.Tick += (_, _) => { timer.Stop(); Track(Preview()); };
        Closed += (_, _) => { closed = true; timer.Stop(); lifetime.Cancel(); previewCancellation?.Cancel(); };
        Opened += (_, _) => RequestPreview();
        KeyDown += (_, e) => { if (!e.Handled && e.Key == Key.Z && e.KeyModifiers == KeyModifiers.Control) { UndoStroke(); e.Handled = true; } };
        using var initial = DrawPreview(smallSource, smallMask, null, "Gray", false, 0, settings, 8, "Keep edge", CancellationToken.None);
        view.SetBitmap(Ui.ToAvaloniaBitmap(initial, 1000, displayProfile));
    }

    private void Track(Task task) { tasks.Add(task); _ = Forget(task); }
    private async Task Forget(Task task) { try { await task; } finally { tasks.Remove(task); } }
    private async Task ChangeSource()
    {
        if (closed || finalizing || visibleCanvas == null) return;
        generation++; timer.Stop(); previewCancellation?.Cancel(); loadingSource = true; CanAccept = false;
        try
        {
            if (requestedAll && canvasSource == null)
            {
                status.Text = "Sampling visible layers…";
                canvasTask ??= Task.Run(visibleCanvas, lifetime.Token);
                var rendered = await canvasTask;
                if (canvasSource == null) { canvasSource = rendered; canvasSmall = Resize(rendered, false); }
            }
            if (!closed) { source = requestedAll ? canvasSource! : activeSource; smallSource = requestedAll ? canvasSmall! : activeSmall; RequestPreview(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closed) status.Text = "Could not sample layers: " + error.Message; }
        finally { loadingSource = false; if (!closed) CanAccept = !requestedAll || canvasSource != null; }
    }
    private void RequestPreview()
    {
        if (closed || finalizing) return;
        generation++; previewCancellation?.Cancel(); status.Text = "Updating preview…"; timer.Stop(); timer.Start();
    }
    private void Paint(SKPoint from, SKPoint to, bool backgroundBrush)
    {
        if (finalizing || strokes.Count == 0) return;
        var mark = backgroundBrush ? MattingMark.Background : brush switch
        { "Keep foreground" => MattingMark.Foreground, "Remove background" => MattingMark.Background, "Erase local mark" => MattingMark.None, _ => MattingMark.Unknown };
        var segment = (From: from, To: to, Size: view.BrushSize, Mark: mark); strokes[^1].Add(segment); DrawMark(segment);
    }
    private void DrawMark((SKPoint From, SKPoint To, double Size, MattingMark Mark) segment)
    {
        using var canvas = new SKCanvas(marks);
        using var paint = new SKPaint { Color = new SKColor(0, 0, 0, (byte)segment.Mark), BlendMode = SKBlendMode.Src, IsAntialias = false, StrokeCap = SKStrokeCap.Round, StrokeWidth = (float)segment.Size };
        if (segment.From == segment.To) canvas.DrawCircle(segment.To, (float)segment.Size / 2, paint); else canvas.DrawLine(segment.From, segment.To, paint);
        Pixels.Invalidate(marks);
    }
    private void UndoStroke()
    {
        if (finalizing || strokes.Count == 0) return;
        strokes.RemoveAt(strokes.Count - 1); marks.Erase(SKColors.Transparent);
        foreach (var stroke in strokes) foreach (var segment in stroke) DrawMark(segment);
        Pixels.Invalidate(marks); RequestPreview();
    }

    private SKBitmap Resize(SKBitmap pixels, bool mask)
    {
        var w = Math.Max(1, (int)Math.Round(source.Width * scale)); var h = Math.Max(1, (int)Math.Round(source.Height * scale));
        var result = mask ? Pixels.NewMask(w, h) : Pixels.NewLike(pixels, w, h);
        using var canvas = new SKCanvas(result); using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        canvas.DrawImage(Pixels.ImageOf(pixels), new SKRect(0, 0, w, h), new SKSamplingOptions(mask ? SKFilterMode.Nearest : SKFilterMode.Linear), paint);
        return result;
    }

    private async Task Preview()
    {
        if (closed || finalizing || loadingSource || view.IsPainting) return;
        var revision = generation; using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); previewCancellation = cancellation;
        var full = fullPreview; using var local = full ? Pixels.Clone(marks) : Resize(marks, true);
        var previewScale = full ? 1 : scale;
        var grade = settings with { Smooth = settings.Smooth * previewScale, Feather = settings.Feather * previewScale, Shift = (int)Math.Round(settings.Shift * previewScale) };
        var edge = radius == 0 ? 0 : Math.Max(1, (int)Math.Round(radius * previewScale));
        var selectedMethod = method; var selectedBackground = background; var show = showMarks; var decontaminate = decontamination;
        var image = full ? source : smallSource;
        var progress = new Progress<(int Done, int Total)>(p => { if (!closed && revision == generation && !cancellation.IsCancellationRequested) status.Text = $"Refining fine detail {p.Done}/{p.Total}…"; });
        try
        {
            using var rendered = await Task.Run(() => DrawPreview(image, full ? original : smallMask, local, selectedBackground, show, decontaminate, grade, edge, selectedMethod, cancellation.Token, progress), cancellation.Token);
            if (closed || revision != generation || cancellation.IsCancellationRequested) return;
            view.SetBitmap(Ui.ToAvaloniaBitmap(rendered, full ? DocumentLimits.MaxSide : 1000, displayProfile)); status.Text = full ? "Full-resolution preview" : "Draft preview · 100% enables full resolution · Final output uses full resolution";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closed && revision == generation) status.Text = "Preview failed: " + error.Message; }
        finally { if (previewCancellation == cancellation) previewCancellation = null; }
    }

    private static SKBitmap Refine(SKBitmap image, SKBitmap mask, SKBitmap? local, MaskRefinementSettings grade, int radius, string method, CancellationToken cancellation, IProgress<(int Done, int Total)>? progress = null)
    {
        using var matted = method is "AI matting" or "AI fine detail" ? TrimapMatting.Refine(image, mask, radius, local, cancellation, fineDetail: method == "AI fine detail", progress: progress)
            : method == "Alpha matting" ? AlphaMatting.Refine(image, mask, radius, local, cancellation) : null;
        return MaskRefinement.Refine(matted ?? mask, image, grade with { EdgeAware = method == "Image edges", Radius = radius }, cancellation);
    }

    private static unsafe SKBitmap DrawPreview(SKBitmap image, SKBitmap mask, SKBitmap? local, string background, bool showMarks, double decontamination, MaskRefinementSettings grade, int radius, string method, CancellationToken cancellation, IProgress<(int Done, int Total)>? progress = null)
    {
        using var refined = Refine(image, mask, local, grade, radius, method, cancellation, progress);
        using var cutout = background is "Original" or "Mask" or "Trimap" or "Overlay" ? null : MaskRefinement.Cutout(image, refined, decontamination, cancellation);
        var preview = Pixels.NewWorkingColor(image.Width, image.Height, ColorDepth.Eight, Pixels.ProfileOf(image));
        try
        {
            using var canvas = new SKCanvas(preview);
            canvas.Clear(background switch { "Black" or "Mask" or "Trimap" => SKColors.Black, "White" => SKColors.White, _ => new SKColor(100, 100, 100) });
            if (background is "Mask" or "Trimap")
            {
                using var tri = background == "Trimap" ? AlphaMatting.Trimap(mask, radius, local) : null;
                using var white = new SKPaint { Color = SKColors.White }; canvas.DrawBitmap(tri ?? refined, 0, 0, white);
            }
            else
            {
                canvas.DrawBitmap(cutout ?? image, 0, 0);
                if (background == "Overlay")
                { using var inverted = SelectionMask.Invert(refined); using var red = new SKPaint { Color = new SKColor(230, 35, 70, 130) }; canvas.DrawBitmap(inverted, 0, 0, red); }
            }
            if (showMarks && local != null && !SelectionMask.IsEmpty(local))
            {
                // Preview and tint are explicitly RGB8; editing/model precision is kept in the Core pipeline.
                using var tint = Pixels.NewLike(preview); var target = (byte*)tint.GetPixels(); var targetStride = tint.RowBytes;
                var data = (byte*)local.GetPixels(); var stride = local.RowBytes; var w = local.Width; var h = local.Height;
                for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
                {
                    var color = (MattingMark)data[y * stride + x] switch
                    { MattingMark.Foreground => 0x8c198019u, MattingMark.Background => 0x8c10108cu, MattingMark.Unknown => 0x8c8c4714u, _ => 0u };
                    var pixel = target + (long)y * targetStride + x * 4;
                    pixel[0] = (byte)color; pixel[1] = (byte)(color >> 8); pixel[2] = (byte)(color >> 16); pixel[3] = (byte)(color >> 24);
                }
                canvas.DrawBitmap(tint, 0, 0);
            }
            return preview;
        }
        catch { preview.Dispose(); throw; }
    }

    protected override void Accept()
    {
        if (finalizing || loadingSource || requestedAll && canvasSource == null || view.IsPainting) return;
        finalizing = true; timer.Stop(); previewCancellation?.Cancel(); body.IsEnabled = false; CanAccept = false; Track(FinalizeResult());
    }
    private async Task FinalizeResult()
    {
        status.Text = "Refining full resolution…";
        try
        {
            var output = decontamination > 0 ? "New layer with mask" : Output; var selectedMethod = method; var grade = settings; var edge = radius; var amount = decontamination;
            using var local = Pixels.Clone(marks);
            var progress = new Progress<(int Done, int Total)>(p => { if (!closed) status.Text = $"Refining fine detail {p.Done}/{p.Total}…"; });
            var result = await Task.Run(() =>
            {
                var mask = Refine(source, original, local, grade, edge, selectedMethod, lifetime.Token, progress);
                try { return (Mask: mask, Colors: output == "New layer with mask" ? MaskRefinement.DecontaminateColors(source, mask, amount, lifetime.Token) : null); }
                catch { mask.Dispose(); throw; }
            }, lifetime.Token);
            if (closed) { result.Mask.Dispose(); result.Colors?.Dispose(); return; }
            ResultMask = result.Mask; ResultColors = result.Colors; Output = output; Close(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closed) status.Text = "Refinement failed: " + error.Message; }
        finally { if (!closed) { finalizing = false; body.IsEnabled = true; CanAccept = true; } }
    }

    public void AdoptResult() { ResultMask = null; ResultColors = null; }
    public async ValueTask DisposeAsync()
    {
        closed = true; timer.Stop(); lifetime.Cancel(); previewCancellation?.Cancel();
        await Task.WhenAll(tasks.ToArray()); view.ReleaseBitmap(); activeSmall.Dispose(); canvasSource?.Dispose(); canvasSmall?.Dispose(); smallMask.Dispose(); Pixels.Invalidate(marks); marks.Dispose();
        ResultMask?.Dispose(); ResultColors?.Dispose(); lifetime.Dispose();
    }
}
