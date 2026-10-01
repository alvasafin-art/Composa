using System.Text.Json.Nodes;
using Composa.AI;
using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.App.AI;

public sealed class AiTaskService : IAiTaskRunner
{
    private readonly Func<string> serverUrl;
    private readonly Func<string, IComfyConnection> clientFactory;
    public EngineCatalog Engines { get; }
    public IReadOnlyList<PromptPreset> Presets { get; }
    public EngineProfile? SelectedEngine { get; set; }
    public AiOperationState? Operation { get; private set; }
    public ComfyConnectionState ConnectionState { get; private set; }
    public ComfyServerInfo? ServerInfo { get; private set; }
    public ComfyServerCapabilities? ServerCapabilities { get; private set; }
    public string? ConnectedServerUrl { get; private set; }
    public Func<string, IReadOnlyDictionary<string, string>> ModelSelections { get; set; } = _ => new Dictionary<string, string>();
    public event Action? StateChanged;
    public int ConnectionTimeoutSeconds { get; set; } = 5;

    private CancellationTokenSource? running;
    private long connectionRevision;

    public AiTaskService(Func<string> serverUrl, string engineRoot) : this(serverUrl, engineRoot, url => new ComfyClient(url)) { }

    internal AiTaskService(Func<string> serverUrl, string engineRoot, Func<string, IComfyConnection> clientFactory)
    {
        this.serverUrl = serverUrl;
        this.clientFactory = clientFactory;
        Engines = new EngineCatalog(engineRoot);
        Presets = AppPromptPresets.Load(Path.Combine(Directory.GetParent(engineRoot)?.FullName ?? engineRoot, "presets"));
        SelectedEngine = Engines.Profiles.FirstOrDefault();
    }

    public async Task<EngineCompatibility?> TestConnectionAsync(CancellationToken cancellationToken = default, string? overrideUrl = null)
    {
        var revision = Interlocked.Increment(ref connectionRevision);
        ConnectionState = ComfyConnectionState.Connecting;
        ServerInfo = null;
        ServerCapabilities = null;
        ConnectedServerUrl = null;
        StateChanged?.Invoke();
        try
        {
            using var client = Client(overrideUrl);
            var (info, capabilities) = await client.TestConnectionAsync(cancellationToken);
            if (revision != Volatile.Read(ref connectionRevision)) return null;
            (ServerInfo, ServerCapabilities) = (info, capabilities);
            ConnectedServerUrl = client.Address.ToString();
            ConnectionState = ComfyConnectionState.Connected;
            StateChanged?.Invoke();
            return SelectedEngine == null ? null : Compatibility(SelectedEngine, ModelSelections(ConnectedServerUrl));
        }
        catch (Exception error) when ((error is HttpRequestException or OperationCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            if (revision == Volatile.Read(ref connectionRevision))
            {
                ConnectionState = ComfyConnectionState.Error;
                StateChanged?.Invoke();
            }
            throw new InvalidOperationException($"Cannot reach ComfyUI at {overrideUrl ?? serverUrl()}. Start the server or check its address. " +
                "For another computer, use its LAN address instead of 127.0.0.1 and allow the port on that server. " + error.Message, error);
        }
        catch
        {
            if (revision == Volatile.Read(ref connectionRevision))
            {
                ConnectionState = ComfyConnectionState.Error;
                StateChanged?.Invoke();
            }
            throw;
        }
    }

    public EngineCompatibility Compatibility(EngineProfile engine, IReadOnlyDictionary<string, string> choices)
    {
        if (ServerCapabilities is not { } capabilities) return new(false, ["Connect to ComfyUI to read its models."]);
        // Requirements describe the original pack. Selected replacement files are checked against their actual loaders.
        var missing = EngineCompatibility.Check(engine with { RequiredAssets = [] }, capabilities).Missing.ToHashSet(StringComparer.Ordinal);
        var allSlots = Engines.ModelSlots(engine);
        foreach (var asset in engine.RequiredAssets.Where(asset => !asset.Optional
            && !allSlots.Any(slot => slot.Kind == asset.Kind && slot.Default == asset.Name) && !capabilities.Has(asset)))
            missing.Add($"{asset.Kind} {asset.Name}");
        foreach (var workflow in engine.Workflows)
        {
            var graph = Engines.ReadWorkflow(engine, workflow);
            var slots = WorkflowModels.Slots(graph, engine.Id);
            WorkflowModels.ApplyChoices(graph, engine.Id, choices);
            WorkflowModels.ResolvePaths(graph, capabilities);
            foreach (var slot in slots)
            {
                if (engine.RequiredAssets.Any(asset => asset.Optional && asset.Kind == slot.Kind && asset.Name == slot.Default)) continue;
                var name = graph[slot.NodeId]!["inputs"]![slot.Input]!.GetValue<string>();
                if (capabilities.ModelChoices.TryGetValue(slot.LoaderKey, out var available) && !available.Contains(name))
                    missing.Add($"model {name} ({slot.LoaderKey}); choose a file in ComfyUI Settings");
            }
        }
        return new(missing.Count == 0, missing.Order().ToArray());
    }

    public async Task RunAsync(IEditorCommandService editor, AiTaskRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Task == AiTaskKind.ChangeBackground && editor.Session.Selection == null)
        {
            await editor.Session.RunTransactionAsync("AI Change Background", async _ =>
            {
                await RunAsync(editor, new AiTaskRequest { Task = AiTaskKind.SelectSubject }, cancellationToken);
                if (editor.Session.Selection == null || SelectionMask.Bounds(editor.Session.Selection).IsEmpty)
                    throw new InvalidOperationException("No subject was found. Select the subject manually and retry Change Background.");
                await RunAsync(editor, request, cancellationToken);
                editor.Session.Deselect();
            });
            return;
        }
        if (request.Task == AiTaskKind.MatchToScene)
        {
            Operation = new AiOperationState { Status = AiOperationStatus.Running, Stage = "Matching layer to scene" };
            StateChanged?.Invoke();
            try
            {
                editor.Session.MatchActiveLayerToScene();
                Operation = Operation with { Status = AiOperationStatus.Completed, Stage = "Completed" };
                StateChanged?.Invoke();
                return;
            }
            catch (Exception error)
            {
                Operation = Operation with { Status = AiOperationStatus.Failed, Stage = "Failed", Error = error.Message };
                StateChanged?.Invoke();
                throw;
            }
        }
        if (SelectedEngine is not { } engine) throw new InvalidOperationException("Install and select an Engine Pack first.");
        var binding = engine.Binding(request.Task) ?? throw new InvalidOperationException($"{engine.DisplayName} does not support {request.Task.DisplayName()}.");
        var workflow = engine.Workflow(binding.Workflow);
        var graph = Engines.ReadWorkflow(engine, workflow);
        var variants = binding.OutputMode == AiOutputMode.Selection || request.Task == AiTaskKind.Upscale ? 1 : request.Settings.Variants;
        if (variants is not (1 or 3)) throw new ArgumentException("Choose one or three variants.");
        var initialState = editor.Session.History.CurrentId;
        var initialLayer = editor.Session.Document.ActiveLayerId;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        running?.Cancel();
        running = linked;
        Operation = new AiOperationState { Status = AiOperationStatus.Queued, Stage = "Preparing inputs" };
        StateChanged?.Invoke();
        try
        {
            // The bundled stitched removal workflow handles mask growth and its black patch itself.
            // Feeding an already grown/feathered mask into it would grow and blur the selection twice.
            var preparedRequest = request.Task == AiTaskKind.RemoveObject && binding.Preprocess == "remove-object-in-workflow"
                ? request with { RemoveObject = request.RemoveObject with { Dilation = 0, Feather = 0 } } : request;
            using var inputs = AiTaskInputPreparer.Prepare(editor.Session, preparedRequest);
            var seed = inputs.Seed < 0 ? Random.Shared.NextInt64(long.MaxValue) : inputs.Seed;
            using var client = Client();
            if (ConnectedServerUrl != client.Address.ToString() || ServerCapabilities == null || ConnectionState != ComfyConnectionState.Connected)
                await TestConnectionAsync(linked.Token, client.Address.ToString());
            if (ConnectedServerUrl != client.Address.ToString() || ServerCapabilities is not { } capabilities)
                throw new InvalidOperationException("The ComfyUI connection changed during this request. Retry after connecting to the desired server.");
            WorkflowModels.ApplyChoices(graph, engine.Id, ModelSelections(client.Address.ToString()));
            JsonObject Bind(IReadOnlyDictionary<string, string> files, int index = 0)
            {
                var values = inputs.Values(files);
                foreach (var setting in request.Settings.Values) values[setting.Key] = setting.Value;
                values["seed"] = (seed + index) & long.MaxValue;
                if (request.Task == AiTaskKind.GenerativeFill) values["maskGrow"] = 0;
                var boundGraph = WorkflowBinder.Bind(graph, binding, values);
                if (request.Settings.VariantMode == AiVariantMode.Batch) WorkflowExecution.Batch(boundGraph, variants);
                if (request.Task == AiTaskKind.Upscale)
                    WorkflowExecution.Upscale(boundGraph, request.Settings.UpscaleFactor, inputs.SourceImage.Width, inputs.SourceImage.Height);
                WorkflowModels.ResolvePaths(boundGraph, capabilities);
                var compatibility = EngineCompatibility.CheckWorkflow(boundGraph, capabilities);
                if (!compatibility.IsCompatible) throw new InvalidOperationException(CompatibilityMessage(engine, compatibility)
                    + "\nChoose the missing models or refresh the server list in AI → ComfyUI Settings.");
                return boundGraph;
            }
            // Validate before sending source/reference pictures or submitting a generation.
            _ = Bind(inputs.Images().Where(item => binding.Inputs.ContainsKey(item.Key))
                .ToDictionary(item => item.Key, item => "composa-preflight.png", StringComparer.OrdinalIgnoreCase));
            var uploaded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (semantic, bitmap) in inputs.Images())
            {
                if (!binding.Inputs.ContainsKey(semantic)) continue;
                uploaded[semantic] = await client.UploadPngAsync(semantic, bitmap, linked.Token);
            }
            var images = new List<SKBitmap>();
            var applied = false;
            try
            {
                var runs = request.Settings.VariantMode == AiVariantMode.List ? variants : 1;
                for (var index = 0; index < runs; index++)
                {
                    var number = index;
                    var progress = new Progress<AiOperationState>(state =>
                    {
                        if (!ReferenceEquals(running, linked) || linked.IsCancellationRequested) return;
                        Operation = state with { Status = state.Status == AiOperationStatus.Completed ? AiOperationStatus.Running : state.Status,
                            Stage = variants == 1 ? state.Stage : $"{(runs == 1 ? "Batch · 3 variants" : $"Variant {number + 1}/{variants}")} · {state.Stage}" };
                        StateChanged?.Invoke();
                    });
                    var result = await client.ExecuteAsync(Bind(uploaded, index), progress, linked.Token);
                    using (result.History)
                    {
                        var references = workflow.OutputNodes.Count == 0 ? result.Images : result.Images.Where(image => image.NodeId != null && workflow.OutputNodes.Contains(image.NodeId)).ToList();
                        var expected = runs == 1 ? variants : 1;
                        if (references.Count == 0 || variants > 1 && references.Count != expected)
                            throw new InvalidDataException($"ComfyUI returned {references.Count} images; expected {expected}. No result was applied. Try List mode if this workflow does not support Batch.");
                        foreach (var reference in references)
                        {
                            var image = await client.DownloadAsync(reference, linked.Token);
                            images.Add(image);
                            var pixels = images.Sum(bitmap => (long)bitmap.Width * bitmap.Height);
                            if (binding.OutputMode == AiOutputMode.NewLayerWithMask && inputs.SelectionMask != null) pixels *= 2;
                            if (request.Task == AiTaskKind.ChangeBackground)
                                pixels = (long)editor.Session.Document.Width * editor.Session.Document.Height * variants * 3; // backgrounds + subject copies + masks
                            if (editor.Session.Document.RasterPixels() + pixels > DocumentLimits.DocumentPixelBudget)
                                throw new InvalidOperationException($"These variants exceed the document's {DocumentLimits.DocumentBudgetMegapixels} MP budget. Use fewer variants or a smaller image size.");
                        }
                    }
                }
                linked.Token.ThrowIfCancellationRequested();
                if (editor.Session.History.CurrentId != initialState || editor.Session.Document.ActiveLayerId != initialLayer)
                    throw new InvalidOperationException("The document changed during generation. Results were not applied to a different document state; retry after finishing edits.");
                if (!binding.OutputIsComposited && request.Task == AiTaskKind.RemoveObject && inputs.SelectionMask != null)
                    for (var index = 0; index < images.Count; index++)
                    {
                        var matched = AiResultPostprocessor.MatchRemoval(images[index], inputs.ContextImage, inputs.SelectionMask, inputs.Seed);
                        images[index].Dispose();
                        images[index] = matched;
                    }
                Insert(editor, request.Task, binding.OutputMode, images, inputs.TargetBounds, inputs, binding.OutputIsComposited, variants: variants > 1);
                applied = true;
                Operation = Operation! with { Status = AiOperationStatus.Completed, Stage = "Completed" };
                StateChanged?.Invoke();
            }
            catch
            {
                if (!applied) foreach (var image in images) image.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            Operation = Operation! with { Status = AiOperationStatus.Cancelled, Stage = "Cancelled" };
            StateChanged?.Invoke();
            throw;
        }
        catch (Exception error)
        {
            Operation = Operation! with { Status = AiOperationStatus.Failed, Stage = "Failed", Error = error.Message };
            StateChanged?.Invoke();
            throw;
        }
        finally { if (ReferenceEquals(running, linked)) running = null; }
    }

    public void Cancel() => running?.Cancel();

    internal void SetConnectedForTests()
    {
        ConnectionState = ComfyConnectionState.Connected;
        StateChanged?.Invoke();
    }

    private IComfyConnection Client(string? overrideUrl = null)
    {
        var client = clientFactory(overrideUrl ?? serverUrl());
        client.ConnectionTimeout = TimeSpan.FromSeconds(Math.Clamp(ConnectionTimeoutSeconds, 1, 120));
        return client;
    }

    internal static void Insert(IEditorCommandService editor, AiTaskKind task, AiOutputMode mode, IReadOnlyList<SKBitmap> images, SKRectI targetBounds, AiTaskInputs? inputs = null, bool outputIsComposited = false, bool variants = false)
    {
        var session = editor.Session;
        if (mode == AiOutputMode.Selection)
        {
            var mask = ToMask(images[0], session.Document.Width, session.Document.Height);
            foreach (var image in images) image.Dispose();
            session.ApplyAiSelection(task, mask);
            return;
        }
        if (task == AiTaskKind.Upscale)
        {
            if (session.Selection is { } selection && !targetBounds.IsEmpty)
            {
                var upscaleOutputs = images.Select((image, index) =>
                {
                    var sourceBounds = inputs?.UpscaleSourceBounds ?? targetBounds;
                    var fitted = image.Width == sourceBounds.Width && image.Height == sourceBounds.Height
                        ? image : Resize(image, sourceBounds.Width, sourceBounds.Height);
                    if (!ReferenceEquals(fitted, image)) image.Dispose();
                    if (sourceBounds != targetBounds)
                    {
                        var patch = Pixels.NewColor(targetBounds.Width,targetBounds.Height);
                        using (var canvas = new SKCanvas(patch)) canvas.DrawImage(Pixels.ImageOf(fitted),sourceBounds.Left-targetBounds.Left,sourceBounds.Top-targetBounds.Top);
                        fitted.Dispose(); fitted=patch;
                    }
                    var mask = MaskForBounds(selection, targetBounds, targetBounds.Width, targetBounds.Height);
                    return new AiOutput(images.Count == 1 ? "AI Upscale Selection" : $"AI Upscale Selection {index + 1}", fitted, mask,
                        Bounds: new SKRect(targetBounds.Left, targetBounds.Top, targetBounds.Right, targetBounds.Bottom));
                }).ToList();
                session.InsertAiOutput(task, upscaleOutputs);
                return;
            }
            editor.Transaction("AI Upscale", target =>
            {
                target.ResizeImage(images[0].Width, images[0].Height);
                target.InsertAiOutput(task, images.Select((image, index) =>
                    new AiOutput(images.Count == 1 ? "AI Upscale" : $"AI Upscale {index + 1}", image)).ToList());
            });
            return;
        }
        if (task == AiTaskKind.ChangeBackground && inputs?.SelectionMask != null && inputs.BackgroundMask != null)
        {
            if (variants && images.Count > 1)
            {
                editor.Transaction("AI Background Variants", target =>
                {
                    var folder = Layer.Group("AI Background Variants"); folder.Tags.Add("ai-variants");
                    target.Document.InsertAboveActive(folder);
                    for (var i = 0; i < images.Count; i++)
                    {
                        var variant = Layer.Group($"Background {i + 1}"); variant.Visible = i == 0;
                        folder.Children.Add(variant); target.Document.SetActive(variant.Id);
                        Insert(editor, task, mode, [images[i]], targetBounds, inputs, outputIsComposited);
                    }
                    target.Document.SetActive(folder.Id);
                });
                return;
            }
            var backgroundOutputs = new List<AiOutput>();
            foreach (var image in images)
            {
                var fitted = image.Width == session.Document.Width && image.Height == session.Document.Height
                    ? image : Resize(image, session.Document.Width, session.Document.Height);
                if (!ReferenceEquals(fitted, image)) image.Dispose();
                backgroundOutputs.Add(new AiOutput("AI Background", fitted, Tags: ["background"]));
            }
            backgroundOutputs.Add(new AiOutput("Original Subject", Pixels.Clone(inputs.ContextImage), Pixels.Clone(inputs.SelectionMask), ["product", "editable"]));
            session.InsertAiOutput(task, backgroundOutputs, group: true);
            return;
        }
        var outputs = images.Select((image, index) =>
        {
            SKBitmap? mask = null;
            var documentSized = image.Width == session.Document.Width && image.Height == session.Document.Height;
            var selection = inputs?.OutputMask ?? inputs?.SelectionMask ?? session.Selection;
            if (mode == AiOutputMode.NewLayerWithMask && selection != null)
            {
                if (outputIsComposited)
                {
                    if (!documentSized || inputs == null) throw new InvalidDataException("A stitched workflow must return the original canvas dimensions. Refusing to stretch a cropped result over the selection.");
                    using var support = SelectionMask.Expand(inputs.PreprocessedMask ?? inputs.SelectionMask!, inputs.TransitionMargin);
                    mask = AiResultPostprocessor.CompositedMask(image, inputs.ContextImage, support, inputs.SelectionMask);
                }
                else mask = documentSized ? Pixels.Clone(selection) : MaskForBounds(selection, targetBounds, image.Width, image.Height);
            }
            SKRect? placement = documentSized ? null : new SKRect(targetBounds.Left, targetBounds.Top, targetBounds.Right, targetBounds.Bottom);
            return new AiOutput(images.Count == 1 ? "AI " + task.DisplayName() : $"AI {task.DisplayName()} {index + 1}", image, mask, Bounds: placement);
        }).ToList();
        session.InsertAiOutput(task, outputs, group: mode == AiOutputMode.LayerGroup, variants: variants);
    }

    private static SKBitmap MaskForBounds(SKBitmap documentMask, SKRectI bounds, int width, int height)
    {
        var result = Pixels.NewMask(width, height);
        var source = documentMask.GetPixelSpan();
        var target = result.GetPixelSpan();
        for (var y = 0; y < height; y++)
        {
            var sy = bounds.Top + Math.Min(bounds.Height - 1, (int)((long)y * bounds.Height / height));
            for (var x = 0; x < width; x++)
            {
                var sx = bounds.Left + Math.Min(bounds.Width - 1, (int)((long)x * bounds.Width / width));
                if (sx >= 0 && sy >= 0 && sx < documentMask.Width && sy < documentMask.Height)
                    target[y * result.RowBytes + x] = source[sy * documentMask.RowBytes + sx];
            }
        }
        Pixels.Invalidate(result);
        return result;
    }

    private static SKBitmap ToMask(SKBitmap image, int width, int height)
    {
        using var resized = image.Width == width && image.Height == height ? null : Resize(image, width, height);
        var source = resized ?? image;
        var mask = Pixels.NewMask(width, height);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var color = source.GetPixel(x, y);
                mask.GetPixelSpan()[y * mask.RowBytes + x] = color.Alpha < 255 ? color.Alpha : (byte)((color.Red * 54 + color.Green * 183 + color.Blue * 19) >> 8);
            }
        Pixels.Invalidate(mask);
        return mask;
    }

    private static SKBitmap Resize(SKBitmap source, int width, int height)
    {
        var target = Pixels.NewColor(width, height);
        using var canvas = new SKCanvas(target);
        canvas.DrawImage(Pixels.ImageOf(source), new SKRect(0, 0, width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        return target;
    }

    public static string CompatibilityMessage(EngineProfile engine, EngineCompatibility compatibility) =>
        compatibility.IsCompatible ? $"{engine.DisplayName} is ready." : $"{engine.DisplayName}\n\nMissing:\n- {string.Join("\n- ", compatibility.Missing)}";
}
