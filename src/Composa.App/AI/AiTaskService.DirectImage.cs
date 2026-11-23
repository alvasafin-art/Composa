using Composa.AI;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.AI;

public sealed partial class AiTaskService
{
    internal async Task RunDirectImageAsync(IEditorCommandService editor, DirectImageGenerator generator, AiTaskRequest request,
        Func<bool> targetAvailable, CancellationToken cancellationToken = default)
    {
        if (running != null) throw new InvalidOperationException("Another AI operation is already running.");
        var unavailable = generator.Availability(request.Task, request.ReferenceImages.Count + (request.ReferenceImage == null ? 0 : 1));
        if (unavailable != null) throw new InvalidOperationException(unavailable);
        if (request.Settings.Variants is < 1 or > 3) throw new ArgumentException("Choose one, two or three variants.");
        var target = editor.Session;
        var initialState = target.History.CurrentId;
        var initialLayer = target.Document.ActiveLayerId;
        var bounds = target.Document.Bounds;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        running = linked;
        var remaining = new List<SKBitmap>();
        try
        {
            var variants = request.Settings.Variants;
            var stage = generator.Route == ImageGenerationRoute.LocalCodex ? "Generating through Codex CLI…" : "Generating through the image API…";
            for (var i = 0; i < variants; i++)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (!targetAvailable()) throw new OperationCanceledException();
                Operation = new() { Status = AiOperationStatus.Running, Stage = variants == 1 ? stage : $"{i + 1}/{variants} · {stage}" };
                StateChanged?.Invoke();
                var image = await generator.GenerateAsync(request.Prompt, request.Settings.Width, request.Settings.Height, linked.Token);
                remaining.Add(image);
                var total = remaining.Sum(bitmap => (long)bitmap.Width * bitmap.Height) * (target.Document.Depth == ColorDepth.Sixteen ? 4 : 1);
                if (target.Document.RasterPixels() + total > DocumentLimits.DocumentPixelBudget)
                    throw new InvalidOperationException("These variants exceed the document's raster budget.");
            }
            linked.Token.ThrowIfCancellationRequested();
            if (!targetAvailable()) throw new OperationCanceledException();
            if (target.History.CurrentId != initialState || target.Document.ActiveLayerId != initialLayer
                || target.IsStroking || target.IsInTransaction || target.IsEditingText)
                throw new InvalidOperationException("The document changed during generation. Results were discarded.");
            Insert(editor, request.Task, AiOutputMode.NewLayer, remaining, Fit(remaining[0], bounds), variants: variants > 1);
            remaining.Clear(); // The document and Undo now own these pixels.
            Operation = new() { Status = AiOperationStatus.Completed, Stage = "Completed" };
            StateChanged?.Invoke();
        }
        catch (OperationCanceledException)
        {
            Operation = new() { Status = AiOperationStatus.Cancelled, Stage = "Cancelled" };
            StateChanged?.Invoke();
            throw;
        }
        catch (Exception error)
        {
            Operation = new() { Status = AiOperationStatus.Failed, Stage = "Failed", Error = error.Message };
            StateChanged?.Invoke();
            throw;
        }
        finally
        {
            foreach (var image in remaining) image.Dispose();
            if (ReferenceEquals(running, linked)) running = null;
        }
    }

    private static SKRectI Fit(SKBitmap image, SKRectI bounds)
    {
        var scale = Math.Min((double)bounds.Width / image.Width, (double)bounds.Height / image.Height);
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        return SKRectI.Create(bounds.Left + (bounds.Width - width) / 2, bounds.Top + (bounds.Height - height) / 2, width, height);
    }
}
