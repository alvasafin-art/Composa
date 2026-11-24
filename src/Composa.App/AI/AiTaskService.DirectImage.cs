using Composa.AI;
using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.App.AI;

public sealed partial class AiTaskService
{
    internal (DirectImageGenerator Generator, ImageApiRate Rate)? LastDirectImageCharge { get; private set; }

    internal async Task RunDirectImageAsync(IEditorCommandService editor, DirectImageGenerator generator, AiTaskRequest request,
        Func<bool> targetAvailable, CancellationToken cancellationToken = default)
    {
        if (running != null) throw new InvalidOperationException("Another AI operation is already running.");
        if (request.Task == AiTaskKind.MatchToScene && request.Scene.MatchMode == SceneMatchMode.ToneAndColor)
        {
            await RunAsync(editor, request, cancellationToken);
            return;
        }
        var selectionTask = request.Task is AiTaskKind.ObjectSelection or AiTaskKind.SelectSubject;
        var upscale = request.Task == AiTaskKind.Upscale;
        var utility = selectionTask || upscale;
        if (utility)
        {
            if (editor.Session.Selection is { } empty && SelectionMask.IsEmpty(empty) && request.SelectionRegion == null)
                throw new InvalidOperationException("The selection is empty.");
            generator = generator with { Task = upscale ? AiTaskKind.Upscale : AiTaskKind.ObjectSelection };
            request = request with { Task = generator.Task, ReferenceImages = [], ReferenceImage = null,
                Settings = request.Settings with { Variants = 1 } };
        }
        var unavailable = generator.Availability(request.Task, request.ReferenceImages.Count + (request.ReferenceImage == null ? 0 : 1));
        if (unavailable != null) throw new InvalidOperationException(unavailable);
        if (request.Settings.Variants is < 1 or > 3) throw new ArgumentException("Choose one, two or three variants.");
        var target = editor.Session;
        if (target.IsInteracting) throw new InvalidOperationException("Finish the current interaction before running AI.");
        request = PrepareRequest(target, request);
        var initialState = target.History.CurrentId;
        var initialLayer = target.Document.ActiveLayerId;
        var bounds = target.Document.Bounds;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        running = linked;
        LastDirectImageCharge = null;
        void ReportCharge(ImageApiRate charge) => LastDirectImageCharge = (generator, charge);
        var remaining = new List<SKBitmap>();
        try
        {
            if (request.Task is AiTaskKind.MatchToScene or AiTaskKind.Harmonize or AiTaskKind.Relight or AiTaskKind.ChangeBackground)
            {
                await RunDirectSceneAsync(editor, generator, request, EnsureUnchanged, linked.Token, ReportCharge);
                return;
            }
            var expand = request.Task == AiTaskKind.GenerativeExpand;
            var remove = request.Task == AiTaskKind.RemoveObject;
            var fill = request.Task == AiTaskKind.GenerativeFill;
            var edit = request.Task == AiTaskKind.ImageEdit || fill || remove || expand;
            if (fill && (target.Selection == null || SelectionMask.IsEmpty(target.Selection)))
                throw new InvalidOperationException("Make a selection first.");
            using var selection = fill ? Pixels.Clone(target.Selection!) : null;
            var hasReferences = request.ReferenceImages.Count > 0 || request.ReferenceImage != null;
            Operation = new() { Status = AiOperationStatus.Queued, Stage = "Preparing inputs" };
            StateChanged?.Invoke();
            using var prepared = edit || utility || hasReferences ? await PrepareInputsAsync(target, request, 6, linked.Token) : null;
            if (expand && request.ExpansionMode == AiExpansionMode.MaskedRegion && SelectionMask.IsEmpty(prepared!.PreprocessedMask!))
                throw new InvalidOperationException("No empty canvas in the target area. Extend Crop, expose transparent space, or choose Whole image.");
            var modelMask = expand && request.ExpansionMode == AiExpansionMode.MaskedRegion ? prepared!.PreprocessedMask
                : remove ? prepared!.PreprocessedMask : selection;
            using var editInputs = edit ? await Task.Run(() => new DirectImageEdit(prepared!, request), linked.Token) : null;
            using var expansionMask = expand ? editInputs!.OutputMask() : null;
            var outputMask = expand ? expansionMask : remove ? prepared!.OutputMask : selection;
            DirectImageFiles? files = null;
            var prompt = prepared?.Prompt ?? request.Prompt;
            if (editInputs != null)
            {
                var nativeMask = modelMask != null && generator.UsesNativeMask;
                files = await Task.Run(() => editInputs.Encode(prepared!, modelMask, nativeMask, linked.Token), linked.Token);
                prompt = "Edit the first image. Preserve its framing, proportions and unchanged details. "
                    + (hasReferences ? "Use the following reference images as guidance. " : "")
                    + (modelMask != null ? nativeMask ? "Edit the area indicated by the mask. "
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
            var stage = utility ? generator.Label
                : generator.Route == ImageGenerationRoute.LocalCodex ? "Generating through Codex CLI…" : "Generating through the image API…";
            for (var i = 0; i < variants; i++)
            {
                linked.Token.ThrowIfCancellationRequested();
                EnsureUnchanged();
                Operation = new() { Status = AiOperationStatus.Running, Stage = variants == 1 ? stage : $"{i + 1}/{variants} · {stage}" };
                StateChanged?.Invoke();
                SKBitmap image;
                if (utility)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                    timeout.CancelAfter(TimeSpan.FromMinutes(5));
                    try
                    {
                        image = await Task.Run(() => upscale
                            ? DirectImageUtilities.UpscaleAsync(generator, prepared!.SourceImage, request.Settings.UpscaleFactor, timeout.Token, ReportCharge)
                            : DirectImageUtilities.SelectAsync(generator, prepared!.SourceImage, timeout.Token), timeout.Token);
                    }
                    catch (OperationCanceledException) when (!linked.IsCancellationRequested)
                    { throw new InvalidOperationException("The selected AI operation timed out."); }
                }
                else
                {
                    image = await generator.GenerateAsync(prompt, editInputs?.Width ?? request.Settings.Width,
                        editInputs?.Height ?? request.Settings.Height, linked.Token, files: files, onCharge: ReportCharge);
                    if (editInputs != null) image = await Task.Run(() => editInputs.Finish(image));
                    if (remove && prepared!.SelectionMask is { } removal)
                    {
                        using var unmatched = image;
                        image = await Task.Run(() => AiResultPostprocessor.MatchRemoval(unmatched, prepared.ContextImage, removal, prepared.Seed));
                    }
                }
                remaining.Add(image);
                var total = remaining.Sum(bitmap => (long)bitmap.Width * bitmap.Height) * (target.Document.Depth == ColorDepth.Sixteen ? 4 : 1);
                if (outputMask != null) total += (long)outputMask.Width * outputMask.Height * remaining.Count;
                if (!utility && target.Document.RasterPixels() + total > DocumentLimits.DocumentPixelBudget)
                    throw new InvalidOperationException("These variants exceed the document's raster budget.");
            }
            EnsureUnchanged();
            var mode = selectionTask ? AiOutputMode.Selection : outputMask != null ? AiOutputMode.NewLayerWithMask : AiOutputMode.NewLayer;
            var placement = upscale ? prepared!.TargetBounds : edit || selectionTask ? bounds : Fit(remaining[0], bounds);
            Insert(editor, request.Task, mode, remaining, placement, inputs: prepared, variants: variants > 1, localOutputMask: outputMask);
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

        void EnsureUnchanged()
        {
            linked.Token.ThrowIfCancellationRequested();
            if (!targetAvailable()) throw new OperationCanceledException();
            if (target.History.CurrentId != initialState || target.Document.ActiveLayerId != initialLayer || target.IsInteracting)
                throw new InvalidOperationException("The document changed during generation. Results were discarded.");
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
