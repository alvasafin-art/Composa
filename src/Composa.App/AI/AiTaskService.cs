using System.Text.Json.Nodes;
using Composa.AI;
using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.App.AI;

public sealed partial class AiTaskService : IAiTaskRunner
{
    private readonly Func<string> serverUrl;
    private readonly Func<string, IComfyConnection> clientFactory;
    private readonly Func<SceneEditInput, SKBitmap, CancellationToken, SKBitmap> sceneSubjectFinder;
    private readonly bool requireSceneSubjectModels;
    public EngineCatalog Engines { get; }
    public IReadOnlyList<PromptPreset> Presets { get; }
    public EngineProfile? SelectedEngine { get; set; }
    internal Func<IReadOnlyDictionary<string,string>?> PreferredModelRoutes { get; set; } = () => null;
    public Func<AiTaskKind, string?> ModelKeyForTask { get; set; } = _ => null;
    public Func<AiTaskKind, string?> EngineIdForTask { get; set; } = _ => null;
    internal Func<WorkflowPreferences> WorkflowSettings { get; set; } = () => new();
    internal Func<int>? SelectionContextPercent { get; set; }
    internal SavedWorkflow? ActiveWorkflow(AiTaskKind task, EngineProfile? engine, bool? useAssigned = null) =>
        WorkflowSettings().Active(task, engine?.Id, useAssigned);
    internal bool SupportsReferenceImages(AiTaskKind task, EngineProfile? engine, bool? useAssigned = null) =>
        engine?.Binding(task)?.SupportsReferenceImages != false || ActiveWorkflow(task, engine, useAssigned) != null;
    public EngineProfile? EngineFor(AiTaskKind task, string? overrideId = null)
    {
        if(task==AiTaskKind.MatchToScene && overrideId==null && EngineIdForTask(task)==null) task=AiTaskKind.Harmonize;
        var id = overrideId ?? EngineIdForTask(task);
        if (id == "seedvr2") id = "seedvr2-native";
        var engine = id == null ? SelectedEngine : Engines.Profiles.FirstOrDefault(pack => pack.Id == id);
        if (overrideId == null && ModelKeyForTask(task) is { } key)
        {
            var choices = ModelSelections(serverUrl());
            var bindingTask = task == AiTaskKind.SelectSubject ? AiTaskKind.ObjectSelection : task;
            var matched = Engines.ImageModels(bindingTask, engine, choices, PreferredModelRoutes()).FirstOrDefault(model => model.ModelKey == key);
            if (engine?.Id == "flux2-klein-intel-xpu" && engine.Binding(bindingTask) != null &&
                key.StartsWith("server:", StringComparison.Ordinal) && WorkflowModels.IsKlein9BVariant(key["server:".Length..]) &&
                WorkflowModels.IsKlein9BVariant(Engines.ModelFor(engine, bindingTask, choices).Identifier))
                return engine;
            if (task == AiTaskKind.MatchToScene && engine != null && ImageOperationModels.Supports(engine, task) &&
                Engines.ModelFor(engine, task, choices).ModelKey == key)
                return engine;
            return matched?.Engine;
        }
        var operation = ImageOperationModels.ModelTask(task);
        if (overrideId == null && operation is AiTaskKind.ObjectSelection or AiTaskKind.Upscale && engine?.Binding(operation) == null)
            engine = Engines.Profiles.FirstOrDefault(pack => !pack.PaidApi && pack.Binding(operation) != null);
        return engine;
    }
    public AiOperationState? Operation { get; private set; }
    public ComfyConnectionState ConnectionState { get; private set; }
    public ComfyServerInfo? ServerInfo { get; private set; }
    public ComfyServerCapabilities? ServerCapabilities { get; private set; }
    public string? ConnectedServerUrl { get; private set; }
    public Func<string, IReadOnlyDictionary<string, string>> ModelSelections { get; set; } = _ => new Dictionary<string, string>();
    public Func<string> AdditionalPrompt { get; set; } = () => AiPromptDefaults.PreserveAppearance;
    public Func<string, string>? AdditionalPromptForPack { get; set; }
    public Func<string?> ApiKey { get; set; } = () => Environment.GetEnvironmentVariable("COMPOSA_COMFY_API_KEY");
    private string? sessionKey;
    private string? sessionKeyAddress;
    public string? SessionApiKey
    {
        get => sessionKeyAddress == serverUrl() ? sessionKey : null;
        set { sessionKeyAddress = serverUrl(); sessionKey = value; }
    }
    public Func<CancellationToken, Task<string?>>? RequestApiKey { get; set; }
    public string? Credential => string.IsNullOrWhiteSpace(SessionApiKey) ? ApiKey()?.Trim() : SessionApiKey.Trim();
    public event Action? StateChanged;
    public int ConnectionTimeoutSeconds { get; set; } = 5;

    private CancellationTokenSource? running;
    private long connectionRevision;

    public AiTaskService(Func<string> serverUrl, string engineRoot) : this(serverUrl, engineRoot, url => new ComfyClient(url)) { }

    internal AiTaskService(Func<string> serverUrl, string engineRoot, Func<string, IComfyConnection> clientFactory,
        Func<SceneEditInput, SKBitmap, CancellationToken, SKBitmap>? sceneSubjectFinder = null)
    {
        this.serverUrl = serverUrl;
        this.clientFactory = clientFactory;
        this.sceneSubjectFinder = sceneSubjectFinder ?? SceneBackgroundSubject.Locate;
        requireSceneSubjectModels = sceneSubjectFinder == null;
        Engines = new EngineCatalog(engineRoot);
        Presets = AppPromptPresets.Load(Path.Combine(Directory.GetParent(engineRoot)?.FullName ?? engineRoot, "presets"));
        SelectedEngine = Engines.Profiles.FirstOrDefault();
    }

    internal AiTaskService SettingsPreview(Func<string>? address = null) => new(address ?? serverUrl, Engines.Root, clientFactory)
    {
        SelectedEngine = SelectedEngine, ConnectionTimeoutSeconds = ConnectionTimeoutSeconds,
        ModelSelections = ModelSelections, WorkflowSettings = WorkflowSettings, ServerInfo = ServerInfo, ServerCapabilities = ServerCapabilities,
        ConnectedServerUrl = ConnectedServerUrl, ConnectionState = ConnectionState
    };

    internal void AcceptSettingsPreview(AiTaskService preview, string address)
    {
        Interlocked.Increment(ref connectionRevision);
        ConnectionTimeoutSeconds = preview.ConnectionTimeoutSeconds;
        SelectedEngine = Engines.Find(preview.SelectedEngine?.Id);
        var matches = preview.ConnectedServerUrl == address;
        ConnectedServerUrl = matches ? address : null;
        ServerInfo = matches ? preview.ServerInfo : null;
        ServerCapabilities = matches ? preview.ServerCapabilities : null;
        ConnectionState = matches ? preview.ConnectionState : ComfyConnectionState.Disconnected;
        StateChanged?.Invoke();
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
            try { return SelectedEngine == null ? null : Compatibility(SelectedEngine, ModelSelections(ConnectedServerUrl)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or KeyNotFoundException)
            {
                return new(false, [error.Message]);
            }
        }
        catch (Exception error) when ((error is HttpRequestException or OperationCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            if (revision == Volatile.Read(ref connectionRevision))
            {
                ConnectionState = ComfyConnectionState.Error;
                StateChanged?.Invoke();
            }
            throw new InvalidOperationException($"Cannot reach ComfyUI at {overrideUrl ?? serverUrl()}. Start the server or check its address. " +
                "For another computer, use its LAN address instead of 127.0.0.1 and allow the port on that server.", error);
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
        if (engine.PaidApi && engine.ApiModel is { } apiModel && !PartnerPricing.SupportsModel(capabilities, apiModel))
            missing.Add($"API model {apiModel}; update ComfyUI to a version offering it");
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

    public Task RunAsync(IEditorCommandService editor, AiTaskRequest request, CancellationToken cancellationToken = default) =>
        RunAsync(editor, request, null, cancellationToken);

    internal async Task<bool> RunAsync(IEditorCommandService editor, AiTaskRequest request,
        Func<bool>? targetAvailable, CancellationToken cancellationToken = default, Func<bool>? expansionFrameUnchanged = null)
    {
        if (editor.Session.IsInteracting && (targetAvailable != null || !editor.Session.IsInTransaction))
            throw new InvalidOperationException("Finish the current interaction before running AI.");
        var requestedTask = request.Task;
        request = PrepareRequest(editor.Session, request);
        if (request.Task == AiTaskKind.MatchToScene && request.Scene.MatchMode == SceneMatchMode.ToneAndColor)
        {
            Operation = new AiOperationState { Status = AiOperationStatus.Running, Stage = "Matching layer to scene" };
            StateChanged?.Invoke();
            try
            {
                editor.Session.MatchActiveLayerToScene();
                Operation = Operation with { Status = AiOperationStatus.Completed, Stage = "Completed" };
                StateChanged?.Invoke();
                return false;
            }
            catch (Exception error)
            {
                Operation = Operation with { Status = AiOperationStatus.Failed, Stage = "Failed", Error = error.Message };
                StateChanged?.Invoke();
                throw;
            }
        }
        if (request.Task is AiTaskKind.MatchToScene or AiTaskKind.Harmonize or AiTaskKind.Relight or AiTaskKind.ChangeBackground)
        {
            await RunSceneAsync(editor, request, targetAvailable, cancellationToken);
            return false;
        }
        var engine = EngineFor(requestedTask, request.EngineId);
        var preferences = WorkflowSettings();
        if (preferences.ActiveId(requestedTask, engine?.Id, request.UseAssignedWorkflow) is { } active &&
            active == preferences.Assignments.GetValueOrDefault(requestedTask))
            engine = preferences.AssignedEngine(Engines, requestedTask, engine);
        var saved = preferences.Active(requestedTask, engine?.Id, request.UseAssignedWorkflow);
        if (saved != null)
        {
            EditableWorkflows.Validate(saved, requestedTask);
            request = request with { Settings = request.Settings with { VariantMode = AiVariantMode.List } };
        }
        engine ??= saved != null ? new EngineProfile { Id = "custom-workflow", DisplayName = saved.Name }
            : throw new InvalidOperationException("The workflow pack assigned to this task is unavailable. Choose one in ComfyUI Settings.");
        var binding = saved != null
            ? new EngineTaskBinding { Task = request.Task, Inputs = saved.Bindings, OutputIsComposited = saved.OutputIsComposited,
                OutputMode = request.Task == AiTaskKind.GenerateImage ? AiOutputMode.NewLayer : AiOutputMode.NewLayerWithMask }
            : engine.Binding(request.Task) ?? throw new InvalidOperationException($"{engine.DisplayName} does not support {request.Task.DisplayName()}.");
        var fullEdit = request.Task == AiTaskKind.ImageEdit || editor.Session.Selection == null && request.Task is AiTaskKind.RemoveObject or AiTaskKind.Harmonize or AiTaskKind.Relight or AiTaskKind.ChangeBackground
            || request.Task == AiTaskKind.GenerativeExpand && request.ExpansionMode == AiExpansionMode.WholeImage;
        if (saved == null && fullEdit) binding = engine.Binding(AiTaskKind.ImageEdit) ?? throw new InvalidOperationException("This pack has no full-image edit workflow.");
        if (!binding.SupportsReferenceImages && (request.ReferenceImages.Count > 0 || request.ReferenceImage != null))
            throw new InvalidOperationException(Locale.T("This model accepts a text prompt only."));
        var workflow = saved == null ? engine.Workflow(binding.Workflow) : new EngineWorkflow();
        var graph = saved != null ? (JsonObject)saved.Graph.DeepClone() : Engines.ReadWorkflow(engine, workflow);
        var qwen = saved == null && engine.Id == QwenImageWorkflow.EngineId;
        var paid = saved == null && engine.PaidApi;
        if (paid && NanoBanana.IsModel(engine.ApiModel) && request.ReferenceImages.Count + (request.Task == AiTaskKind.GenerateImage ? 0 : 1) > 14)
            throw new InvalidOperationException("Nano Banana accepts up to 14 input images, including the source.");
        int referenceLimit = engine.ApiModel switch { PartnerNodes.Seedream => 10, NanoBanana.Model => 14, _ => 6 };
        if (paid && engine.ApiModel == PartnerNodes.Seedream && request.ReferenceImages.Count + (request.Task == AiTaskKind.GenerateImage ? 0 : 1) > referenceLimit)
            throw new InvalidOperationException("Seedream accepts up to ten input images, including the source.");
        if((NanoBanana.IsModel(engine.ApiModel) || engine.ApiModel == PartnerNodes.Seedream || engine.Id == Flux3Workflow.EngineId || qwen) && request.Settings.VariantMode==AiVariantMode.Batch)
            request=request with { Settings=request.Settings with { VariantMode=AiVariantMode.List } };
        var variants = binding.OutputMode == AiOutputMode.Selection || request.Task == AiTaskKind.Upscale ? 1 : request.Settings.Variants;
        if (variants is < 1 or > 3) throw new ArgumentException("Choose one, two or three variants.");
        var initialState = editor.Session.History.CurrentId;
        var initialLayer = editor.Session.Document.ActiveLayerId;
        var initialBounds = editor.Session.Document.Bounds;
        var initialRasterPixels = request.Task == AiTaskKind.Upscale ? editor.Session.Document.RasterPixels() : 0;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        running?.Cancel();
        running = linked;
        Operation = new AiOperationState { Status = AiOperationStatus.Queued, Stage = "Preparing inputs" };
        StateChanged?.Invoke();
        try
        {
            // The bundled stitched removal workflow handles mask growth and its black patch itself.
            // Feeding an already grown/feathered mask into it would grow and blur the selection twice.
            var preparedRequest = request.Task == AiTaskKind.RemoveObject && (binding.Preprocess == "remove-object-in-workflow" || engine.PaidApi)
                ? request with { RemoveObject = request.RemoveObject with { Dilation = 0, Feather = 0 } } : request;
            preparedRequest = preparedRequest with { AdditionalPrompt = AdditionalPromptForPack?.Invoke(engine.Id) ?? AdditionalPrompt() };
            using var inputs = await PrepareInputsAsync(editor.Session, preparedRequest, referenceLimit, linked.Token);
            using var qwenInputs = saved == null && (qwen || engine.Id == Flux3Workflow.EngineId) ? await Task.Run(() => new QwenImageInputs(inputs, preparedRequest), linked.Token) : null;
            using var editable = saved == null && engine.Id == "flux2-klein-intel-xpu" && graph["crop"]?["class_type"]?.GetValue<string>() == "InpaintCropImproved"
                && (inputs.SelectionMask != null || request.Task == AiTaskKind.GenerativeExpand && !fullEdit || inputs.BackgroundMask != null)
                ? await Task.Run(() => new EditableMaskedWorkflow(inputs, preparedRequest), linked.Token) : null;
            if (request.Task == AiTaskKind.GenerativeExpand && request.ExpansionMode == AiExpansionMode.MaskedRegion && SelectionMask.IsEmpty(inputs.PreprocessedMask!))
                throw new InvalidOperationException("No empty canvas in the target area. Extend Crop, expose transparent space, or choose Whole image.");
            var seed = inputs.Seed < 0 ? Random.Shared.NextInt64(long.MaxValue) : inputs.Seed;
            using var client = Client();
            if (ConnectedServerUrl != client.Address.ToString() || ServerCapabilities == null || ConnectionState != ComfyConnectionState.Connected)
                await TestConnectionAsync(linked.Token, client.Address.ToString());
            if (ConnectedServerUrl != client.Address.ToString() || ServerCapabilities is not { } capabilities)
                throw new InvalidOperationException("The ComfyUI connection changed during this request. Retry after connecting to the desired server.");
            if (paid || saved != null && ComfyClient.UsesApiCredits(graph))
            {
                if (string.IsNullOrWhiteSpace(Credential) && RequestApiKey != null)
                {
                    var key = await RequestApiKey(linked.Token);
                    if (string.IsNullOrWhiteSpace(key)) throw new OperationCanceledException("API key entry cancelled.");
                    SessionApiKey = key.Trim();
                }
                if (string.IsNullOrWhiteSpace(Credential)) throw new InvalidOperationException("Add a Comfy.org API key in AI → ComfyUI Settings. Browser login alone is not enough.");
                if (paid && (engine.ApiModel == null || !PartnerPricing.SupportsModel(capabilities, engine.ApiModel)))
                    throw new InvalidOperationException($"The connected ComfyUI does not offer {engine.ApiModel}. Update ComfyUI and refresh its models.");
                if (paid && !NanoBanana.IsModel(engine.ApiModel) && !PartnerPricing.Choices(capabilities, engine.ApiModel!, engine.ApiModel == PartnerNodes.Seedream ? "size_preset" : "size").Contains("Custom"))
                    throw new InvalidOperationException("Update ComfyUI: this model needs Custom dimensions to preserve image/mask proportions.");
                var quality=request.Settings.Values.GetValueOrDefault("apiQuality")?.ToString() ?? "low";
                if(NanoBanana.IsModel(engine.ApiModel)) quality=NanoBanana.Resolution(quality);
                if (paid && engine.ApiModel != PartnerNodes.Seedream && !PartnerPricing.Choices(capabilities, engine.ApiModel!, NanoBanana.IsModel(engine.ApiModel) ? "resolution" : "quality").Contains(quality))
                    throw new InvalidOperationException("The connected server does not support this image quality or resolution. Choose an available value in Advanced.");
            }
            var nativeMask = graph["gpt"]?["class_type"]?.GetValue<string>() == "OpenAIGPTImageNodeV2"
                && capabilities.NodeTypes.Contains("ImageToMask");
            using var apiInputs = paid ? await Task.Run(() => new PartnerImageInputs(inputs, preparedRequest, engine.ApiModel, nativeMask), linked.Token) : null;
            using var localOutputMask = apiInputs?.OutputMask() ?? (qwenInputs?.Mask is { } qwenMask ? Pixels.Clone(qwenMask) : editable == null ? null : Pixels.Clone(editable.Mask));
            var modelFiles = ModelSelections(client.Address.ToString());
            if (request.ModelFileOverrides is { Count: > 0 } overrides)
            {
                var merged = new Dictionary<string, string>(modelFiles);
                foreach (var (slot, file) in overrides) merged[slot] = file;
                modelFiles = merged;
            }
            WorkflowModels.ApplyChoices(graph, engine.Id, modelFiles, savedGraph: saved != null);
            if (saved != null)
            {
                WorkflowModels.ResolvePaths(graph, capabilities);
                saved = saved with { Graph = graph };
            }
            JsonObject Bind(IReadOnlyDictionary<string, string> files, int index = 0)
            {
                var variantSeed = (seed + index) & long.MaxValue;
                var boundGraph = saved != null ? EditableWorkflows.Bind(saved, binding, inputs, request, files, variantSeed)
                    : BuildExecutionGraph(graph, engine, binding, inputs, request, files, variantSeed,
                        capabilities, initialBounds, editable, qwenInputs, apiInputs, variants);
                var compatibility = EngineCompatibility.CheckWorkflow(boundGraph, capabilities);
                if (!compatibility.IsCompatible) throw new InvalidOperationException(saved != null
                    ? $"Workflow \"{saved.Name}\" needs {string.Join(", ", compatibility.Missing)}. Install these on the server or edit the graph in Settings → Workflows."
                    : CompatibilityMessage(engine, compatibility) + "\nChoose the missing models or refresh the server list in AI → ComfyUI Settings.");
                return boundGraph;
            }
            // Validate before sending source/reference pictures or submitting a generation.
            var imagesToUpload = qwenInputs?.Images ?? apiInputs?.Images ?? inputs.Images().Where(item => binding.Inputs.ContainsKey(item.Key)).ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
            if (saved != null && requestedTask == AiTaskKind.GenerativeExpand)
            {
                var frame = inputs.PreprocessedImage ?? inputs.SourceImage;
                foreach (var semantic in new[] { "sourceImage", "contextImage" })
                    if (binding.Inputs.ContainsKey(semantic)) imagesToUpload[semantic] = frame;
                imagesToUpload["selectionMask"] = inputs.PreprocessedMask ?? inputs.SelectionMask!;
            }
            if (editable?.ConditioningImage is { } conditioning) imagesToUpload["conditioningImage"] = conditioning;
            if (saved == null && request.Task == AiTaskKind.GenerativeExpand && fullEdit && apiInputs == null && qwenInputs == null) imagesToUpload["sourceImage"] = inputs.PreprocessedImage!;
            var previewGraph = Bind(imagesToUpload
                .ToDictionary(item => item.Key, item => "composa-preflight.png", StringComparer.OrdinalIgnoreCase));
            // The BiRefNet workflow already supplies a complete matte for its input frame.
            var preserveBiRefNetMatte = binding.OutputMode == AiOutputMode.Selection && previewGraph.Any(entry =>
                entry.Value?["class_type"]?.GetValue<string>() == "LoadBackgroundRemovalModel" &&
                entry.Value["inputs"]?["bg_removal_name"] is JsonValue model &&
                model.TryGetValue<string>(out var file) && file.Contains("birefnet", StringComparison.OrdinalIgnoreCase));
            var uploaded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (imagesToUpload.Count > 0)
            {
                Operation = Operation! with { Stage = "Uploading inputs" };
                StateChanged?.Invoke();
            }
            foreach (var (semantic, bitmap) in imagesToUpload)
            {
                uploaded[semantic] = await client.UploadPngAsync(semantic, bitmap, linked.Token);
            }
            var images = new List<SKBitmap>();
            var applied = false;
            double? reportedCredits = null;
            try
            {
                var runs = request.Settings.VariantMode == AiVariantMode.List ? variants : 1;
                for (var index = 0; index < runs; index++)
                {
                    EnsureTargetAvailable(targetAvailable, linked.Token);
                    var number = index;
                    var progress = new Progress<AiOperationState>(state =>
                    {
                        if (!ReferenceEquals(running, linked) || linked.IsCancellationRequested) return;
                        var stage = state.Status == AiOperationStatus.Completed ? "Applying result" : state.Stage;
                        Operation = state with { Status = state.Status == AiOperationStatus.Completed ? AiOperationStatus.Running : state.Status,
                            Stage = variants == 1 ? stage : $"{(runs == 1 ? $"Batch · {variants} variants" : $"Variant {number + 1}/{variants}")} · {stage}",
                            CreditsUsed = state.CreditsUsed is { } cost ? (reportedCredits ?? 0) + cost : reportedCredits };
                        StateChanged?.Invoke();
                    });
                    if (saved == null && engine.Id.StartsWith("seedvr2", StringComparison.Ordinal))
                    {
                        if (initialRasterPixels + (long)inputs.SourceImage.Width * inputs.SourceImage.Height * request.Settings.UpscaleFactor * request.Settings.UpscaleFactor > DocumentLimits.DocumentPixelBudget)
                            throw new InvalidOperationException($"Upscale exceeds the document's {DocumentLimits.DocumentBudgetMegapixels} MP budget.");
                    }
                    var result = await client.ExecuteAsync(Bind(uploaded, index), progress, linked.Token);
                    using (result.History)
                    {
                        if (result.CreditsUsed is { } cost) reportedCredits = (reportedCredits ?? 0) + cost;
                        var references = workflow.OutputNodes.Count == 0 ? result.Images : result.Images.Where(image => image.NodeId != null && workflow.OutputNodes.Contains(image.NodeId)).ToList();
                        var expected = runs == 1 ? variants : 1;
                        if (references.Count == 0 || variants > 1 && references.Count != expected)
                            throw new InvalidDataException($"ComfyUI returned {references.Count} images; expected {expected}. No result was applied. Try List mode if this workflow does not support Batch.");
                        foreach (var reference in references)
                        {
                            var image = await client.DownloadAsync(reference, linked.Token);
                            // CPU finishing reads immutable snapshots; keep the editor responsive.
                            if (apiInputs != null) image = await Task.Run(() => apiInputs.FinishUnmasked(image));
                            else if (qwenInputs != null) image = await Task.Run(() => qwenInputs.Finish(image));
                            else if (editable != null) image = await Task.Run(() => editable.Finish(image));
                            else if (request.Task == AiTaskKind.GenerativeExpand)
                            {
                                var fitted = Resize(image, inputs.PreprocessedImage!.Width, inputs.PreprocessedImage.Height); image.Dispose(); image = fitted;
                                if (request.ExpansionMode == AiExpansionMode.MaskedRegion)
                                {
                                    using var context = inputs.ExpandedContext();
                                    using var finalMask = AutomaticAiMask.OutputMask(inputs.OutputMask ?? inputs.PreprocessedMask!, context, inputs.MaskPlan!, expansion: true);
                                    var constrained = AiResultPostprocessor.Constrain(image, context, finalMask); image.Dispose(); image = constrained;
                                }
                            }
                            images.Add(image);
                            // An expansion already maps the decoded frame to the target canvas.
                            // Free translation here would copy edge pixels into uncovered areas.
                            if (fullEdit && request.AlignMaskedEdits && request.Task != AiTaskKind.GenerativeExpand && apiInputs?.AlignsFullEdit != true)
                                await Task.Run(() => AiSeamlessFinisher.AlignEdit(image, inputs.ContextImage), linked.Token);
                            var pixels = images.Sum(bitmap => (long)bitmap.Width * bitmap.Height);
                            if (binding.OutputMode == AiOutputMode.NewLayerWithMask && inputs.SelectionMask != null) pixels *= 2;
                            if (pixels > DocumentLimits.DocumentPixelBudget)
                                throw new InvalidOperationException($"These variants exceed the document's {DocumentLimits.DocumentBudgetMegapixels} MP budget. Use fewer variants or a smaller image size.");
                        }
                    }
                }
                if (!binding.OutputIsComposited && request.Task == AiTaskKind.RemoveObject && inputs.SelectionMask != null)
                    for (var index = 0; index < images.Count; index++)
                    {
                        var matched = AiResultPostprocessor.MatchRemoval(images[index], inputs.ContextImage, inputs.SelectionMask, inputs.Seed);
                        images[index].Dispose();
                        images[index] = matched;
                    }
                await WaitForResultTargetAsync(editor.Session, targetAvailable, linked.Token);
                var layerOnly = ResultNeedsSeparateLayer(editor.Session, initialState, initialLayer, binding.OutputMode);
                var hasExpansionFrame = request.Task == AiTaskKind.GenerativeExpand && inputs.ExpansionBounds != null;
                layerOnly |= hasExpansionFrame && expansionFrameUnchanged?.Invoke() == false;
                Insert(editor, request.Task, binding.OutputMode, images, inputs.TargetBounds, inputs, binding.OutputIsComposited,
                    variants: variants > 1, localOutputMask: localOutputMask, preserveBiRefNetMatte: preserveBiRefNetMatte,
                    layerOnly: layerOnly);
                applied = true;
                Operation = Operation! with { Status = AiOperationStatus.Completed, Stage = "Completed", CreditsUsed = reportedCredits };
                StateChanged?.Invoke();
                return hasExpansionFrame && !layerOnly;
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

    private static void EnsureTargetAvailable(Func<bool>? targetAvailable, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (targetAvailable?.Invoke() == false) throw new OperationCanceledException();
    }

    private static async Task WaitForResultTargetAsync(EditorSession session, Func<bool>? targetAvailable, CancellationToken token)
    {
        EnsureTargetAvailable(targetAvailable, token);
        // Scripts may be awaiting AI inside their own transaction.
        while (session.IsInteracting && (targetAvailable != null || !session.IsInTransaction))
        {
            await Task.Delay(50, token);
            EnsureTargetAvailable(targetAvailable, token);
        }
    }

    private static bool ResultNeedsSeparateLayer(EditorSession session, long initialState, Guid? initialLayer,
        AiOutputMode mode = AiOutputMode.NewLayer)
    {
        var changed = session.History.CurrentId != initialState || session.Document.ActiveLayerId != initialLayer;
        if (changed && mode == AiOutputMode.Selection)
            throw new InvalidOperationException("The document changed during AI selection. The current selection was kept.");
        return changed;
    }

    private static async Task<AiTaskInputs> PrepareInputsAsync(EditorSession session, AiTaskRequest request, int maxReferences, CancellationToken token)
    {
        // Document.Clone shares immutable committed pixels. Hold independent Skia handles to UI
        // references so removing a thumbnail during preparation cannot dispose a worker's source.
        var snapshot = new EditorSession(session.Document.Clone());
        var handles = new List<SKBitmap>();
        SKBitmap Retain(SKBitmap source)
        {
            var view = new SKBitmap();
            handles.Add(view);
            if (!source.ExtractSubset(view, source.Info.Rect)) throw new InvalidOperationException("Unable to retain the reference image.");
            return view;
        }
        try
        {
            var captured = request with { ReferenceImage = request.ReferenceImage == null ? null : Retain(request.ReferenceImage),
                ReferenceImages = request.ReferenceImages.Select(Retain).ToArray() };
            return await Task.Run(() => AiTaskInputPreparer.Prepare(snapshot, captured, maxReferences), token);
        }
        finally { foreach (var handle in handles) handle.Dispose(); }
    }

    private AiTaskRequest PrepareRequest(EditorSession session, AiTaskRequest request, bool direct = false)
    {
        if (request.Regions.Length > 0)
        {
            if (!RegionPrompt.Supports(request.Task))
                throw new InvalidOperationException("AI regions require Generate Image, Image Edit or Generative Fill.");
            if (request.Regions.Any(r => !r.Valid || string.IsNullOrWhiteSpace(r.Prompt) || !session.Document.Bounds.Contains(r.Bounds)))
                throw new InvalidOperationException("Each enabled AI region needs a prompt and must fit inside the canvas.");
            if (request.RegionDelivery == RegionDelivery.Overlay)
            {
                if (request.Task == AiTaskKind.GenerateImage)
                    throw new InvalidOperationException("Visual regions need a source image. Choose Image Edit or Generative Fill.");
                if (request.Regions.Select(r => r.Ink.Name).Distinct().Count() != request.Regions.Length)
                    throw new InvalidOperationException("Enabled visual regions must have distinct colors.");
                if (request.Task != AiTaskKind.GenerativeFill)
                    request = request with { Prompt = request.Prompt + "\n" + RegionPrompt.OverlayInstructions(request) };
            }
        }
        request = AutomaticAiMask.IgnoreLegacyControls(request) with
        {
            SelectionContextPercent = SelectionContextPercent?.Invoke() ?? request.SelectionContextPercent
        };
        // Other workflows receive an explicit reference-frame convention for textual coordinates.
        // The native FLUX/Qwen binders convert the boxes after their final padding is known.
        if (request.Task != AiTaskKind.GenerativeFill && request.Regions.Length > 0 && request.RegionDelivery == RegionDelivery.Coordinates &&
            (direct || request.EngineId is not (Flux3Workflow.EngineId or QwenImageWorkflow.EngineId) ||
             WorkflowSettings().Active(request.Task, request.EngineId, request.UseAssignedWorkflow) != null))
        {
            var prompt = RegionPrompt.Coordinates(request.Prompt, request.Regions, session.Document.Bounds, 1, 1, request.Task == AiTaskKind.ImageEdit, fluxCaption: false);
            request = request with { Prompt = prompt, Regions = [] };
        }
        // Crop bounds take precedence over a selection on the old canvas.
        if (request.Task != AiTaskKind.GenerativeExpand || request.ExpansionBounds != null || session.Selection == null) return request;
        return request with { Task = AiTaskKind.GenerativeFill, Prompt = AiPromptDefaults.Expand, BlackEditRegion = true, ExpansionMode = AiExpansionMode.MaskedRegion,
            Settings = request.Settings with { Values = new(request.Settings.Values) { ["imageOriginalSize"] = request.ExpansionMinimumSide == 0 } } };
    }

    private JsonObject BuildExecutionGraph(JsonObject graph, EngineProfile engine, EngineTaskBinding binding, AiTaskInputs inputs,
        AiTaskRequest request, IReadOnlyDictionary<string, string> files, long seed, ComfyServerCapabilities capabilities, SKRectI documentBounds,
        EditableMaskedWorkflow? editable, QwenImageInputs? qwenInputs, PartnerImageInputs? apiInputs, int variants)
    {
        var qwen = engine.Id == QwenImageWorkflow.EngineId;
        var values = inputs.Values(files);
        if (qwenInputs == null && apiInputs == null && editable == null)
        {
            var frame = inputs.SourceImage.Info.Rect;
            values["prompt"] = RegionPrompt.MaskedPrompt(inputs.Prompt, request, frame, frame,
                new SKSizeI(frame.Width, frame.Height), annotationBounds: inputs.MaskPlan?.Bounds);
        }
        foreach (var setting in request.Settings.Values) values[setting.Key] = setting.Value;
        if (apiInputs == null)
        {
            // Original pixel sizes still need latent-friendly dimensions. Inpaint uses
            // original context dimensions, not a tiny selection's width stretched to the crop.
            var width = inputs.CanvasWidth; var height = inputs.CanvasHeight;
            if (graph["crop"]?["class_type"]?.GetValue<string>() == "InpaintCropImproved"
                && Convert.ToBoolean(values.GetValueOrDefault("imageOriginalSize") ?? false)
                && request.Task != AiTaskKind.GenerativeExpand)
            {
                var contextBounds = inputs.MaskPlan?.Bounds ?? documentBounds;
                width = Math.Max(64, contextBounds.Width); height = Math.Max(64, contextBounds.Height);
            }
            var original = request.Task == AiTaskKind.GenerativeExpand ? request.ExpansionMinimumSide == 0
                : Convert.ToBoolean(values.GetValueOrDefault("imageOriginalSize") ?? false);
            if (original && request.Task is not (AiTaskKind.Upscale or AiTaskKind.ObjectSelection or AiTaskKind.SelectSubject))
                (width, height) = AiDimensions.OriginalGenerationSize(width, height);
            values["width"] = Math.Max(16, (int)(original ? Math.Ceiling(width / 16.0) : Math.Round(width / 16.0)) * 16);
            values["height"] = Math.Max(16, (int)(original ? Math.Ceiling(height / 16.0) : Math.Round(height / 16.0)) * 16);
        }
        values["seed"] = seed;
        if (editable == null && request.Task is (AiTaskKind.GenerativeFill or AiTaskKind.GenerativeExpand)) values["maskGrow"] = 0;
        var boundGraph = qwenInputs != null ? qwenInputs.Bind(graph, files, seed, engine.Id == Flux3Workflow.EngineId)
            : apiInputs == null ? WorkflowBinder.Bind(graph, binding, values) : apiInputs.Bind(graph, engine, files, seed);
        if (request.Task == AiTaskKind.GenerativeExpand && !engine.PaidApi && qwenInputs == null && request.ExpansionMode == AiExpansionMode.MaskedRegion)
        {
            var expandedInputs = new AiTaskInputs { ContextImage = inputs.PreprocessedImage!, SelectionMask = inputs.PreprocessedMask,
                PreprocessedMask = inputs.PreprocessedMask, TargetBounds = SelectionMask.Bounds(inputs.PreprocessedMask!, 1) };
            WorkflowExecution.MaskedEdit(boundGraph, expandedInputs, request, capabilities);
        }
        WorkflowExecution.Loras(boundGraph, engine, request.Settings.Loras, capabilities);
        if (qwen) QwenImageWorkflow.Configure(boundGraph, ServerInfo, align: false);
        if (engine.Id == "flux2-klein-intel-xpu" && binding.OutputIsComposited)
            WorkflowExecution.MaskedEdit(boundGraph, inputs, request, capabilities);
        editable?.Bind(boundGraph, files.GetValueOrDefault("conditioningImage"));
        if (engine.Id == "flux2-klein-intel-xpu")
        {
            WorkflowExecution.NativeFluxSchedule(boundGraph);
            WorkflowMemory.Apply(boundGraph, request.Settings.Values.GetValueOrDefault("fluxMemory")?.ToString() ?? "auto", ServerInfo, capabilities);
        }
        if (request.Settings.VariantMode == AiVariantMode.Batch) WorkflowExecution.Batch(boundGraph, variants);
        if (request.Task == AiTaskKind.Upscale)
            WorkflowExecution.Upscale(boundGraph, request.Settings.UpscaleFactor, inputs.SourceImage.Width, inputs.SourceImage.Height);
        if (engine.Id.StartsWith("seedvr2", StringComparison.Ordinal))
        {
            boundGraph["upscale"]!["inputs"]!["resize_type.multiplier"] = request.Settings.UpscaleFactor;
        }
        WorkflowModels.ResolvePaths(boundGraph, capabilities);
        return boundGraph;
    }

    internal EngineCompatibility OperationCompatibility(EngineProfile engine, AiTaskKind operation, IReadOnlyDictionary<string, string> choices,
        AiOperationSettings profile, bool? useAssigned = null)
    {
        if (ServerCapabilities is not { } capabilities) return new(false, ["Connect to ComfyUI"]);
        var preferences = WorkflowSettings();
        var activeId = preferences.ActiveId(operation, engine.Id, useAssigned);
        if (preferences.Active(operation, engine.Id, useAssigned) is { } saved)
        {
            EditableWorkflows.Validate(saved, operation);
            if (activeId == preferences.Assignments.GetValueOrDefault(operation))
                engine = preferences.AssignedEngine(Engines, operation, engine) ?? engine;
            var assignedGraph = (JsonObject)saved.Graph.DeepClone();
            WorkflowModels.ApplyChoices(assignedGraph, engine.Id, choices, savedGraph: true);
            WorkflowModels.ResolvePaths(assignedGraph, capabilities);
            return EngineCompatibility.CheckWorkflow(assignedGraph, capabilities);
        }
        var binding = engine.Binding(operation) ?? (operation == AiTaskKind.MatchToScene && ImageOperationModels.Supports(engine, operation)
            ? engine.Binding(AiTaskKind.ImageEdit) : null);
        if (binding == null) return new(false, ["No workflow for this operation"]);
        var scene = operation is AiTaskKind.MatchToScene or AiTaskKind.Harmonize or AiTaskKind.Relight or AiTaskKind.ChangeBackground;
        var task = scene && engine.PaidApi ? AiTaskKind.ImageEdit : operation;
        if (task != operation) binding = engine.Binding(task)!;
        var graph = Engines.ReadWorkflow(engine, EngineCatalog.WorkflowFor(engine, operation));
        WorkflowModels.ApplyChoices(graph, engine.Id, choices);
        var missing = EngineCompatibility.Check(engine with { RequiredAssets = [], RequiredNodeTypes = [] }, capabilities with { Version = capabilities.Version ?? ServerInfo?.Version }).Missing.ToList();
        var nano = NanoBanana.IsModel(engine.ApiModel);
        if (engine.PaidApi)
        {
            if (engine.ApiModel == null || !PartnerPricing.SupportsModel(capabilities, engine.ApiModel))
                missing.Add("API model " + engine.ApiModel);
            else
            {
                if (!nano && !PartnerPricing.Choices(capabilities, engine.ApiModel, engine.ApiModel == PartnerNodes.Seedream ? "size_preset" : "size").Contains("Custom"))
                    missing.Add("Server support for custom dimensions");
                var quality = nano ? NanoBanana.Resolution(profile.ApiQuality) : profile.ApiQuality;
                if (engine.ApiModel != PartnerNodes.Seedream && !PartnerPricing.Choices(capabilities, engine.ApiModel, nano ? "resolution" : "quality").Contains(quality))
                    missing.Add("Server support for quality " + quality);
            }
        }
        var loras = profile.ActiveLoras(engine);
        if (scene && !engine.PaidApi)
        {
            if (engine.Id == Flux3Workflow.EngineId)
                Flux3Workflow.BindScene(graph, new Dictionary<string, string> { ["sourceImage"] = "preview.png" }, "", 1, 96, 96);
            else if (engine.Id == QwenImageWorkflow.EngineId)
            {
                QwenImageWorkflow.Bind(graph, new Dictionary<string, string> { ["sourceImage"] = "preview.png" }, "", 1);
                QwenImageWorkflow.Configure(graph, ServerInfo, align: profile.AlignPixels ?? true);
            }
            else WorkflowMemory.Apply(graph, profile.FluxMemory, ServerInfo, capabilities);
            WorkflowExecution.Loras(graph, engine, loras, capabilities);
        }
        else
        {
            // Use the production binder with a small in-memory sample. No uploads or jobs are submitted.
            using var source = Pixels.NewColor(96, 96);
            using var mask = SelectionMask.FromRect(96, 96, new(30, 25, 70, 65));
            var masked = task is AiTaskKind.GenerativeFill or AiTaskKind.RemoveObject or AiTaskKind.GenerativeExpand;
            var inputs = new AiTaskInputs { SourceImage = source, ContextImage = source, CanvasWidth = 96, CanvasHeight = 96,
                SelectionMask = masked ? mask : null, PreprocessedMask = mask, PreprocessedImage = source, BackgroundMask = mask, AlphaMask = mask,
                TargetBounds = masked ? new(30, 25, 70, 65) : source.Info.Rect, ExpansionBounds = source.Info.Rect };
            var request = new AiTaskRequest { Task = task, ExpansionMinimumSide = 96, Settings = new AiGenerationSettings { Width = 96, Height = 96,
                UpscaleFactor = profile.UpscaleFactor, Loras = loras, Variants = Math.Clamp(profile.Variants, 1, 3),
                  VariantMode = nano || engine.ApiModel == PartnerNodes.Seedream || engine.Id is QwenImageWorkflow.EngineId or Flux3Workflow.EngineId ? AiVariantMode.List : profile.VariantMode,
                  Values = new() { ["apiQuality"] = profile.ApiQuality, ["fluxMemory"] = profile.FluxMemory } } };
            using var api = engine.PaidApi ? new PartnerImageInputs(inputs, request, engine.ApiModel) : null;
            using var qwen = engine.Id is QwenImageWorkflow.EngineId or Flux3Workflow.EngineId ? new QwenImageInputs(inputs, request) : null;
            using var editable = masked && engine.Id == "flux2-klein-intel-xpu" && graph["crop"]?["class_type"]?.GetValue<string>() == "InpaintCropImproved"
                ? new EditableMaskedWorkflow(inputs, request) : null;
            var files = (qwen?.Images.Keys.AsEnumerable() ?? api?.Images.Keys.AsEnumerable() ?? inputs.Images().Keys)
                .ToDictionary(k => k, k => "preview.png", StringComparer.OrdinalIgnoreCase);
            graph = BuildExecutionGraph(graph, engine, binding, inputs, request, files, 1, capabilities, source.Info.Rect, editable, qwen, api, request.Settings.Variants);
        }
        WorkflowModels.ResolvePaths(graph, capabilities);
        missing.AddRange(EngineCompatibility.CheckWorkflow(graph, capabilities).Missing);
        return new(missing.Count == 0, missing.Distinct().ToArray());
    }

    internal void SetConnectedForTests(ComfyServerCapabilities? capabilities = null)
    {
        ConnectionState = ComfyConnectionState.Connected;
        ServerCapabilities = capabilities;
        StateChanged?.Invoke();
    }

    private IComfyConnection Client(string? overrideUrl = null)
    {
        var client = clientFactory(overrideUrl ?? serverUrl());
        if (client is ComfyClient transport)
            transport.Credential = () => transport.Address == ComfyServerAddress.Parse(serverUrl()) ? Credential : null;
        client.ConnectionTimeout = TimeSpan.FromSeconds(Math.Clamp(ConnectionTimeoutSeconds, 1, 120));
        return client;
    }

    internal static void Insert(IEditorCommandService editor, AiTaskKind task, AiOutputMode mode, IReadOnlyList<SKBitmap> images, SKRectI targetBounds, AiTaskInputs? inputs = null, bool outputIsComposited = false, bool variants = false, SKBitmap? localOutputMask = null, bool preserveBiRefNetMatte = false, bool layerOnly = false)
    {
        var session = editor.Session;
        if (task == AiTaskKind.GenerativeExpand && inputs?.PreprocessedImage is { } expanded)
        {
            var origin = layerOnly && inputs.ExpansionBounds is { } originalFrame ? originalFrame.Location : SKPointI.Empty;
            editor.Transaction("AI Generative Expand", target =>
            {
                if (!layerOnly && inputs.ExpansionBounds is { } bounds) target.Crop(bounds, "Expand Canvas");
                target.InsertAiOutput(task, images.Select((image, index) => new AiOutput(images.Count == 1 ? "AI Generative Expand" : $"AI Generative Expand {index + 1}", image,
                    Mask: localOutputMask == null ? null : Pixels.Clone(localOutputMask),
                    Bounds: SKRect.Create(origin.X, origin.Y, expanded.Width, expanded.Height))).ToList(), variants: variants, atTop: layerOnly);
            });
            return;
        }
        if (mode == AiOutputMode.Selection)
        {
            SKBitmap mask;
            if (inputs?.SegmentationSourceBounds is { } roi)
            {
                using var patch = ToMask(images[0], roi.Width, roi.Height);
                mask = Pixels.NewMask(session.Document.Width, session.Document.Height);
                using (var canvas = new SKCanvas(mask)) canvas.DrawImage(Pixels.ImageOf(patch), roi.Left, roi.Top);
                if (inputs.SelectionMask != null)
                {
                    var coverage = mask.GetPixelSpan(); var limit = inputs.SelectionMask.GetPixelSpan();
                    for (var y = 0; y < mask.Height; y++) for (var x = 0; x < mask.Width; x++)
                        coverage[y * mask.RowBytes + x] = (byte)(coverage[y * mask.RowBytes + x] * limit[y * inputs.SelectionMask.RowBytes + x] / 255);
                    Pixels.Invalidate(mask);
                }
            }
            else mask = ToMask(images[0], session.Document.Width, session.Document.Height);
            foreach (var image in images) image.Dispose();
            if (!preserveBiRefNetMatte && task == AiTaskKind.ObjectSelection && inputs?.SelectionPoint is { } point)
            {
                // Other server matte routes can keep just the clicked instance. Keep its
                // translucent fringe when several objects stand out.
                SKBitmap? instance;
                using (mask) instance = ObjectSelection.FromMatte(mask, point.X, point.Y, 0, connectivityThreshold: 8);
                if (instance == null) return;
                mask = instance;
            }
            session.ApplyAiSelection(task, mask, inputs?.SelectionOperation ?? SelectionMode.Replace,
                recoverForeground: !preserveBiRefNetMatte);
            return;
        }
        if (task == AiTaskKind.Upscale)
        {
            if (layerOnly)
            {
                var image = images[0];
                var frame = inputs?.UpscaleSourceBounds ?? targetBounds;
                var mask = inputs?.SelectionMask is { } captured ? MaskForBounds(captured, frame, image.Width, image.Height) : null;
                session.InsertAiOutput(task, [new("AI Upscale", image, mask,
                    Bounds: SKRect.Create(frame.Left, frame.Top, frame.Width, frame.Height))], atTop: true);
                return;
            }
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
            backgroundOutputs.Add(new AiOutput("Original Subject", Pixels.Clone(inputs.ContextImage), Pixels.Clone(inputs.SubjectMatte ?? inputs.SelectionMask), ["product", "editable"]));
            session.InsertAiOutput(task, backgroundOutputs, group: true);
            return;
        }
        var canvasWidth = inputs?.ContextImage.Width ?? session.Document.Width;
        var canvasHeight = inputs?.ContextImage.Height ?? session.Document.Height;
        var outputs = images.Select((image, index) =>
        {
            SKBitmap? mask = null;
            var documentSized = image.Width == canvasWidth && image.Height == canvasHeight;
            var selection = inputs?.OutputMask ?? inputs?.SelectionMask ?? (layerOnly ? null : session.Selection);
            if (mode == AiOutputMode.NewLayerWithMask && selection != null)
            {
                if (localOutputMask != null) mask = Pixels.Clone(localOutputMask);
                else if (outputIsComposited)
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
        session.InsertAiOutput(task, outputs, group: mode == AiOutputMode.LayerGroup, variants: variants, atTop: layerOnly);
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
        var coverage = image.ColorType == SKColorType.Alpha8 ? image : ReadWorkflowCoverage(image);
        if (coverage != image && coverage.Width == width && coverage.Height == height) return coverage;
        try
        {
            var result = Pixels.NewMask(width, height);
            using var canvas = new SKCanvas(result);
            canvas.DrawImage(Pixels.ImageOf(coverage), SKRect.Create(width, height), new SKSamplingOptions(SKFilterMode.Linear));
            return result;
        }
        finally
        {
            if (coverage != image) coverage.Dispose();
        }
    }

    private static SKBitmap ReadWorkflowCoverage(SKBitmap image)
    {
        // An RGBA cutout uses alpha for the whole frame, including dark opaque
        // pixels. An opaque image such as MaskToImage carries coverage in RGB.
        using var pixmap = image.PeekPixels();
        var useAlpha = !pixmap.ComputeIsOpaque();
        var mask = Pixels.NewMask(image.Width, image.Height);
        var coverage = mask.GetPixelSpan();
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                var color = image.GetPixel(x, y);
                coverage[y * mask.RowBytes + x] = useAlpha ? color.Alpha
                    : (byte)((color.Red * 54 + color.Green * 183 + color.Blue * 19) >> 8);
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
