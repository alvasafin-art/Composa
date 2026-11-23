using Composa.AI;
using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
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
            var edit = request.Task is AiTaskKind.ImageEdit or AiTaskKind.GenerativeFill;
            var masked = request.Task == AiTaskKind.GenerativeFill;
            if (masked && (target.Selection == null || SelectionMask.IsEmpty(target.Selection)))
                throw new InvalidOperationException("Make a selection first.");
            using var selection = masked ? Pixels.Clone(target.Selection!) : null;
            var hasReferences = request.ReferenceImages.Count > 0 || request.ReferenceImage != null;
            Operation = new() { Status = AiOperationStatus.Queued, Stage = "Preparing inputs" };
            StateChanged?.Invoke();
            using var prepared = edit || hasReferences ? await PrepareInputsAsync(target, request, 6, linked.Token) : null;
            using var editInputs = edit ? await Task.Run(() => new DirectImageEdit(prepared!, request), linked.Token) : null;
            DirectImageFiles? files = null;
            var prompt = prepared?.Prompt ?? request.Prompt;
            if (editInputs != null)
            {
                var nativeMask = masked && generator.UsesNativeMask;
                files = await Task.Run(() => editInputs.Encode(prepared!, selection, nativeMask, linked.Token), linked.Token);
                prompt = "Edit the first image. Preserve its framing, proportions and unchanged details. "
                    + (hasReferences ? "Use the following reference images as guidance. " : "")
                    + (masked ? nativeMask ? "Edit the area indicated by the mask. "
                        : "The last image is the edit mask. Edit only its white region in the first image. " : "") + prompt;
            }
            else if (prepared != null)
            {
                files = await Task.Run(() => new DirectImageFiles(prepared.ReferenceImages.Select(image =>
                {
                    linked.Token.ThrowIfCancellationRequested();
                    return DirectImageEdit.EncodePng(image);
                }).ToArray()), linked.Token);
                prompt = "Use the attached images as visual references for the new image. " + prompt;
            }
            var variants = request.Settings.Variants;
            var stage = generator.Route == ImageGenerationRoute.LocalCodex ? "Generating through Codex CLI…" : "Generating through the image API…";
            for (var i = 0; i < variants; i++)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (!targetAvailable()) throw new OperationCanceledException();
                Operation = new() { Status = AiOperationStatus.Running, Stage = variants == 1 ? stage : $"{i + 1}/{variants} · {stage}" };
                StateChanged?.Invoke();
                var image = await generator.GenerateAsync(prompt, editInputs?.Width ?? request.Settings.Width,
                    editInputs?.Height ?? request.Settings.Height, linked.Token, files: files);
                if (editInputs != null) image = await Task.Run(() => editInputs.Finish(image));
                remaining.Add(image);
                var total = remaining.Sum(bitmap => (long)bitmap.Width * bitmap.Height) * (target.Document.Depth == ColorDepth.Sixteen ? 4 : 1);
                if (selection != null) total += (long)selection.Width * selection.Height * remaining.Count;
                if (target.Document.RasterPixels() + total > DocumentLimits.DocumentPixelBudget)
                    throw new InvalidOperationException("These variants exceed the document's raster budget.");
            }
            linked.Token.ThrowIfCancellationRequested();
            if (!targetAvailable()) throw new OperationCanceledException();
            if (target.History.CurrentId != initialState || target.Document.ActiveLayerId != initialLayer
                || target.IsStroking || target.IsPaintingSelection || target.IsInTransaction || target.IsEditingText)
                throw new InvalidOperationException("The document changed during generation. Results were discarded.");
            Insert(editor, request.Task, masked ? AiOutputMode.NewLayerWithMask : AiOutputMode.NewLayer, remaining,
                edit ? bounds : Fit(remaining[0], bounds), inputs: prepared, variants: variants > 1, localOutputMask: selection);
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
