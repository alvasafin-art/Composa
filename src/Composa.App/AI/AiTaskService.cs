using System.Text.Json.Nodes;
using Composa.AI;
using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.AI;

public sealed class AiTaskService : IAiTaskRunner
{
    private readonly Func<string> serverUrl;
    private readonly Func<string, ComfyClient> clientFactory;
    public EngineCatalog Engines { get; }
    public IReadOnlyList<PromptPreset> Presets { get; }
    public EngineProfile? SelectedEngine { get; set; }
    public AiOperationState? Operation { get; private set; }
    public ComfyConnectionState ConnectionState { get; private set; }
    public ComfyServerInfo? ServerInfo { get; private set; }
    public ComfyServerCapabilities? ServerCapabilities { get; private set; }
    public event Action? StateChanged;
    public int ConnectionTimeoutSeconds { get; set; } = 5;

    private CancellationTokenSource? running;

    public AiTaskService(Func<string> serverUrl, string engineRoot) : this(serverUrl, engineRoot, url => new ComfyClient(url)) { }

    internal AiTaskService(Func<string> serverUrl, string engineRoot, Func<string, ComfyClient> clientFactory)
    {
        this.serverUrl = serverUrl;
        this.clientFactory = clientFactory;
        Engines = new EngineCatalog(engineRoot);
        Presets = AppPromptPresets.Load(Path.Combine(Directory.GetParent(engineRoot)?.FullName ?? engineRoot, "presets"));
        SelectedEngine = Engines.Profiles.FirstOrDefault();
    }

    public async Task<EngineCompatibility?> TestConnectionAsync(CancellationToken cancellationToken = default, string? overrideUrl = null)
    {
        ConnectionState = ComfyConnectionState.Connecting;
        StateChanged?.Invoke();
        try
        {
            using var client = Client(overrideUrl);
            (ServerInfo, ServerCapabilities) = await client.TestConnectionAsync(cancellationToken);
            ConnectionState = ComfyConnectionState.Connected;
            StateChanged?.Invoke();
            return SelectedEngine == null ? null : EngineCompatibility.Check(SelectedEngine, ServerCapabilities);
        }
        catch
        {
            ConnectionState = ComfyConnectionState.Error;
            StateChanged?.Invoke();
            throw;
        }
    }

    public async Task RunAsync(IEditorCommandService editor, AiTaskRequest request, CancellationToken cancellationToken = default)
    {
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
        if (ServerCapabilities is { } capabilities && !EngineCompatibility.Check(engine, capabilities).IsCompatible)
            throw new InvalidOperationException(CompatibilityMessage(engine, EngineCompatibility.Check(engine, capabilities)));
        var workflow = engine.Workflow(binding.Workflow);
        var path = Path.GetFullPath(Path.Combine(Engines.DirectoryOf(engine), workflow.File));
        var engineDirectory = Path.GetFullPath(Engines.DirectoryOf(engine)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(engineDirectory, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new FileNotFoundException($"Workflow \"{workflow.Id}\" is missing from Engine Pack \"{engine.DisplayName}\".", path);
        var graph = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken)) as JsonObject
            ?? throw new InvalidDataException($"Workflow \"{workflow.Id}\" is not a JSON object.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        running?.Cancel();
        running = linked;
        Operation = new AiOperationState { Status = AiOperationStatus.Queued, Stage = "Preparing inputs" };
        StateChanged?.Invoke();
        try
        {
            using var inputs = AiTaskInputPreparer.Prepare(editor.Session, request);
            using var client = Client();
            var uploaded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (semantic, bitmap) in inputs.Images())
            {
                if (!binding.Inputs.ContainsKey(semantic)) continue;
                uploaded[semantic] = await client.UploadPngAsync(semantic, bitmap, linked.Token);
            }
            var values = inputs.Values(uploaded);
            foreach (var setting in request.Settings.Values) values[setting.Key] = setting.Value;
            if (request.Task == AiTaskKind.GenerativeFill) values["maskGrow"] = 0;
            var bound = WorkflowBinder.Bind(graph, binding, values);
            var progress = new Progress<AiOperationState>(state => { Operation = state; StateChanged?.Invoke(); });
            var result = await client.ExecuteAsync(bound, progress, linked.Token);
            var references = workflow.OutputNodes.Count == 0 ? result.Images : result.Images.Where(image => image.NodeId != null && workflow.OutputNodes.Contains(image.NodeId)).ToList();
            if (references.Count == 0) throw new InvalidDataException("ComfyUI completed without returning an image from the configured output nodes.");
            var images = new List<SKBitmap>();
            try
            {
                foreach (var reference in references) images.Add(await client.DownloadAsync(reference, linked.Token));
                if (request.Task == AiTaskKind.RemoveObject && inputs.SelectionMask != null)
                    for (var index = 0; index < images.Count; index++)
                    {
                        var matched = AiResultPostprocessor.MatchRemoval(images[index], inputs.ContextImage, inputs.SelectionMask, inputs.Seed);
                        images[index].Dispose();
                        images[index] = matched;
                    }
                Insert(editor, request.Task, binding.OutputMode, images, inputs.TargetBounds, inputs);
                Operation = Operation! with { Status = AiOperationStatus.Completed, Stage = "Completed" };
                StateChanged?.Invoke();
            }
            catch
            {
                foreach (var image in images) image.Dispose();
                throw;
            }
            finally { result.History.Dispose(); }
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

    private ComfyClient Client(string? overrideUrl = null)
    {
        var client = clientFactory(overrideUrl ?? serverUrl());
        client.ConnectionTimeout = TimeSpan.FromSeconds(Math.Clamp(ConnectionTimeoutSeconds, 1, 120));
        return client;
    }

    internal static void Insert(IEditorCommandService editor, AiTaskKind task, AiOutputMode mode, IReadOnlyList<SKBitmap> images, SKRectI targetBounds, AiTaskInputs? inputs = null)
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
                    var fitted = image.Width == targetBounds.Width && image.Height == targetBounds.Height
                        ? image : Resize(image, targetBounds.Width, targetBounds.Height);
                    if (!ReferenceEquals(fitted, image)) image.Dispose();
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
            var backgroundOutputs = new List<AiOutput>();
            foreach (var image in images)
            {
                var fitted = image.Width == session.Document.Width && image.Height == session.Document.Height
                    ? image : Resize(image, session.Document.Width, session.Document.Height);
                if (!ReferenceEquals(fitted, image)) image.Dispose();
                backgroundOutputs.Add(new AiOutput("AI Background", fitted, Pixels.Clone(inputs.BackgroundMask), ["background"]));
            }
            backgroundOutputs.Add(new AiOutput("Original Subject", Pixels.Clone(inputs.ContextImage), Pixels.Clone(inputs.SelectionMask), ["product", "editable"]));
            session.InsertAiOutput(task, backgroundOutputs, group: true);
            return;
        }
        var outputs = images.Select((image, index) =>
        {
            SKBitmap? mask = null;
            var documentSized = image.Width == session.Document.Width && image.Height == session.Document.Height;
            if (mode == AiOutputMode.NewLayerWithMask && session.Selection is { } selection)
                mask = documentSized ? Pixels.Clone(selection) : MaskForBounds(selection, targetBounds, image.Width, image.Height);
            SKRect? placement = documentSized ? null : new SKRect(targetBounds.Left, targetBounds.Top, targetBounds.Right, targetBounds.Bottom);
            return new AiOutput(images.Count == 1 ? "AI " + task.DisplayName() : $"AI {task.DisplayName()} {index + 1}", image, mask, Bounds: placement);
        }).ToList();
        session.InsertAiOutput(task, outputs, group: mode == AiOutputMode.LayerGroup);
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
