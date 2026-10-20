using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Composa.App.Controls;
using Composa.ColorManagement;
using Composa.Painting;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Dialogs;

/// <summary>Original-source Liquify with immutable local field history and serialized cancellable renders.</summary>
internal sealed class LiquifyDialog : DialogWindow, IAsyncDisposable
{
    private readonly SKBitmap source;
    private readonly SKBitmap? selection;
    private readonly RgbProfile displayProfile;
    private readonly LiquifyHistory history;
    private LiquifyField current;
    private readonly MaskRefineView view;
    private readonly TextBlock status = new() { Name = "LiquifyStatus", Text = "Preparing preview…", Foreground = Palette.Secondary };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(45) };
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim renderGate = new(1);
    private readonly HashSet<Task> tasks = [];
    private CancellationTokenSource? previewCancellation;
    private readonly Control body;
    private readonly Button undo, redo;
    private LiquifyTool tool;
    private double strength = .5, hardness = .5;
    private bool showFreeze = true, showOriginal, fullPreview, closed, finalizing;
    private int generation;
    public SKBitmap? Result { get; private set; }
    internal MaskRefineView View => view;
    internal LiquifyField Field => current;
    internal bool IsRendering => tasks.Count > 0 || timer.IsEnabled;

    // Both inputs are borrowed immutable planes. The caller retains them until DisposeAsync has joined all work.
    public LiquifyDialog(SKBitmap source, SKBitmap? selection, RgbProfile displayProfile) : base("Liquify", new Border())
    {
        this.source = source; this.selection = selection; this.displayProfile = displayProfile;
        current = new(source.Width, source.Height); history = new(current);
        view = new(source.Width, source.Height) { Name = "LiquifyPreview", Focusable = true, MinHeight = 250, BrushSize = 150,
            ShowStrokeOverlay = false, Cursor = new Cursor(StandardCursorType.Cross) };
        undo = Ui.TextButton("Undo stroke", () => { if (BusyStroke) return; history.Undo(); current = history.Current; Changed(); }); undo.Name = "LiquifyUndo";
        redo = Ui.TextButton("Redo stroke", () => { if (BusyStroke) return; history.Redo(); current = history.Current; Changed(); }); redo.Name = "LiquifyRedo";
        var toolMenu = Ui.Combo(Enum.GetValues<LiquifyTool>(), tool, t => t == LiquifyTool.Push ? "Forward Warp" : t.ToString(), t => tool = t, 220); toolMenu.Name = "LiquifyTool";
        var fullResolution = Ui.Check("Full-resolution preview", false, v => { fullPreview = v; RequestPreview(); });
        var controls = Ui.Column(12, Ui.Label("Tool", Palette.Secondary), toolMenu,
            Ui.SliderField("Size", 150, 1, 2000, v => { view.BrushSize = v; view.InvalidateVisual(); }, width: 220, reset: 150),
            Ui.SliderField("Strength", 50, 1, 100, v => strength = v / 100, width: 220, reset: 50),
            Ui.SliderField("Hardness", 50, 0, 100, v => hardness = v / 100, width: 220, reset: 50),
            Ui.Row(8, undo, redo),
            Ui.TextButton("Restore all distortions", () => Change(current.RestoreAll())),
            Ui.TextButton("Thaw all marks", () => Change(current.ThawAll())),
            Ui.Check("Show frozen areas", true, v => { showFreeze = v; RequestPreview(); }),
            Ui.Check("Show original", false, v => { showOriginal = v; RequestPreview(); }),
            Ui.Row(8, Ui.TextButton("Fit", view.Fit), Ui.TextButton("100%", () => { view.ActualSize(); fullResolution.IsChecked = true; })), fullResolution,
            Ui.Label(selection == null ? "Freeze protects the current shape." : "The selection protects unselected pixels.", Palette.Secondary));
        var grid = new Grid { ColumnDefinitions = new("*,18,250"), RowDefinitions = new("Auto,*,Auto") };
        var help = Ui.Label("Wheel: zoom · Middle drag: pan · Alt: reconstruct / thaw · Ctrl+Z: undo stroke", Palette.Secondary);
        Grid.SetColumnSpan(help, 3); grid.Children.Add(help); Grid.SetRow(view, 1); grid.Children.Add(view);
        var scroll = new ScrollViewer { Content = controls, Margin = new Thickness(0, 12, 0, 0), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetColumn(scroll, 2); Grid.SetRow(scroll, 1); grid.Children.Add(scroll); Grid.SetRow(status, 2); Grid.SetColumnSpan(status, 3); grid.Children.Add(status); body = grid;
        var old = (StackPanel)Content!; old.Children.Clear(); var layout = new Grid { RowDefinitions = new("*,Auto"), Margin = new Thickness(20) };
        layout.Children.Add(body); Grid.SetRow(Buttons, 1); layout.Children.Add(Buttons); Content = layout;
        CanResize = true; SizeToContent = SizeToContent.Manual; Width = 1150; Height = 800; MinWidth = 850; MinHeight = 560;
        view.StrokeStarted += () => { previewCancellation?.Cancel(); undo.IsEnabled = redo.IsEnabled = false; };
        view.Stroke += Paint;
        view.StrokeEnded += () => { if (closed || finalizing) return; history.Push(current); Changed(); };
        timer.Tick += (_, _) => { timer.Stop(); Track(Preview()); };
        Opened += (_, _) => Changed();
        Closed += (_, _) => { closed = true; timer.Stop(); lifetime.Cancel(); previewCancellation?.Cancel(); };
    }
    private bool BusyStroke => closed || finalizing || view.IsPainting;
    private void Change(LiquifyField value) { if (BusyStroke || !history.Push(value)) return; current = value; Changed(); }
    private void Changed() { undo.IsEnabled = history.CanUndo; redo.IsEnabled = history.CanRedo; RequestPreview(); }
    private void Paint(SKPoint from, SKPoint to, bool alt)
    {
        if (closed || finalizing || showOriginal) return;
        var mode = alt ? tool is LiquifyTool.Freeze or LiquifyTool.Thaw ? LiquifyTool.Thaw : LiquifyTool.Reconstruct : tool;
        try { current = current.Stroke(from, to, mode, view.BrushSize, strength, hardness, selection); RequestPreview(); }
        catch (Exception error) { status.Text = error.Message; }
    }
    private void RequestPreview()
    {
        if (closed || finalizing) return;
        generation++; previewCancellation?.Cancel(); if (!timer.IsEnabled) timer.Start();
    }
    private void Track(Task task) { tasks.Add(task); _ = Forget(task); }
    private async Task Forget(Task task) { try { await task; } finally { tasks.Remove(task); } }
    private async Task Preview()
    {
        if (closed || finalizing) return;
        var field = current; var marks = showFreeze; var original = showOriginal; var native = fullPreview; var version = generation;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); previewCancellation = cancellation;
        try
        {
            await renderGate.WaitAsync(cancellation.Token);
            Avalonia.Media.Imaging.Bitmap preview;
            try { preview = await Task.Run(() =>
            {
                using var rendered = DrawPreview(original ? new(source.Width, source.Height) : field, field, marks, native ? 0 : 1100, cancellation.Token);
                return Ui.ToAvaloniaBitmap(rendered, Math.Max(rendered.Width, rendered.Height), displayProfile);
            }, cancellation.Token); }
            finally { renderGate.Release(); }
            if (!closed && !finalizing && version == generation)
            { view.SetBitmap(preview); status.Text = $"{preview.PixelSize.Width}×{preview.PixelSize.Height} · {field.AllocatedBytes / (1024.0 * 1024):0.0} MB deformation · {(native ? "Full-resolution preview" : "Draft preview")}"; }
            else preview.Dispose();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closed) status.Text = "Preview failed: " + error.Message; }
        finally { if (ReferenceEquals(previewCancellation, cancellation)) previewCancellation = null; }
    }
    private SKBitmap DrawPreview(LiquifyField appearance, LiquifyField field, bool marks, int maxSide, CancellationToken token)
    {
        using var rendered = appearance.Render(source, maxSide, token);
        var result = Pixels.NewWorkingColor(rendered.Width, rendered.Height, Model.ColorDepth.Eight, Pixels.ProfileOf(source));
        try
        {
            using var canvas = new SKCanvas(result); canvas.Clear(new SKColor(65, 65, 65)); canvas.DrawBitmap(rendered, 0, 0);
            if (marks)
            {
                using var freeze = field.FreezePreview(result.Width, result.Height, selection);
                using var tint = new SKPaint { Color = new SKColor(235, 35, 70, 115) }; canvas.DrawBitmap(freeze, 0, 0, tint);
            }
            token.ThrowIfCancellationRequested(); return result;
        }
        catch { result.Dispose(); throw; }
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!e.Handled && e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control) && !BusyStroke)
        { if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) history.Redo(); else history.Undo(); current = history.Current; Changed(); e.Handled = true; }
        base.OnKeyDown(e);
    }
    protected override void Accept()
    {
        if (BusyStroke) return;
        if (!current.HasDeformation) { Close(true); return; }
        finalizing = true; CanAccept = false; body.IsEnabled = false; timer.Stop(); previewCancellation?.Cancel(); Track(FinalizeResult());
    }
    private async Task FinalizeResult()
    {
        status.Text = "Rendering full resolution…";
        try
        {
            await renderGate.WaitAsync(lifetime.Token);
            SKBitmap result;
            try { result = await Task.Run(() => current.Render(source, token: lifetime.Token), lifetime.Token); }
            finally { renderGate.Release(); }
            if (closed) result.Dispose(); else { Result = result; Close(true); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closed) { status.Text = "Liquify failed: " + error.Message; finalizing = false; CanAccept = true; body.IsEnabled = true; } }
    }
    public void AdoptResult() => Result = null;
    public async ValueTask DisposeAsync()
    {
        closed = true; timer.Stop(); lifetime.Cancel(); previewCancellation?.Cancel();
        while (tasks.Count > 0) await Task.WhenAll(tasks.ToArray());
        view.ReleaseBitmap(); Result?.Dispose(); Result = null; renderGate.Dispose(); lifetime.Dispose();
    }
}
