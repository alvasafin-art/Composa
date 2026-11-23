using System.Text.Json;
using Composa.AI;

namespace Composa.App;

public enum EditorTheme { Classic, DarkNeutral, LightNeutral }

public enum ObjectSelectionSource { AnySubject, Person, PlainBackdrop, ComfyUI, MobileSam = 5, EfficientSamTi = 6, EfficientSamS = 7 }

/// <summary>Preferences remembered between launches, stored in the platform's config directory (<see cref="AppPaths.Config"/>).</summary>
public sealed class Settings
{
    public List<string> RecentFiles { get; set; } = [];
    public List<Dialogs.CanvasPreset> RecentCanvasPresets { get; set; } = [];
    public List<Dialogs.CanvasPreset> UserCanvasPresets { get; set; } = [];
    public AI.WorkflowPreferences Workflows { get; set; } = new();
    public bool RememberAiPrompts { get; set; } = true;
    [System.Text.Json.Serialization.JsonIgnore]
    internal Dictionary<AiTaskKind, string> AiPromptDrafts { get; } = [];

    internal string AiPromptFor(AiTaskKind task) => RememberAiPrompts ? AiPromptDrafts.GetValueOrDefault(task) ?? "" : "";

    internal void RememberAiPrompt(AiTaskKind task, string prompt)
    {
        if (!RememberAiPrompts) return;
        if (prompt.Length == 0) AiPromptDrafts.Remove(task);
        else AiPromptDrafts[task] = prompt;
    }
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;
    public bool Maximized { get; set; }
    public string Language { get; set; } = "en";
    public EditorTheme Theme { get; set; } = EditorTheme.Classic;
    public bool EditorWelcomeSeen { get; set; }
    public bool CanvasOnly { get; set; }
    public Dictionary<string, WorkspaceLayout> Workspaces { get; set; } = [];
    public int JpegQuality { get; set; } = 90;
    public bool ShowPixelGrid { get; set; } = true;
    /// <summary>Toggles that belong to the person rather than to a document, kept the way Photoshop keeps its tool options.</summary>
    public bool ShowTransformControls { get; set; } = true;
    public bool AutoSelect { get; set; } = true;
    public bool AutoUpdateLinkedSources { get; set; } = true;
    public DisplayColorMode DisplayColorMode { get; set; } = DisplayColorMode.Automatic;
    public string? DisplayProfilePath { get; set; }
    public ObjectSelectionSource ObjectSelectionModel { get; set; } = ObjectSelectionSource.EfficientSamS;
    public bool ObjectSelectionQuickOutline { get; set; }
    public string ToolsPosition { get; set; } = "Left";
    public ToolPaletteSettings? ToolPalette { get; set; }
    public int PromptSelectionRevision { get; set; } = 1;
    public string? ObjectSelectionComfyModel { get; set; }
    public Composa.Editing.ViewOptions View { get; set; } = new();
    public Composa.Painting.BrushSettings Brush { get; set; } = new();
    public Composa.Painting.BrushSettings? EraserBrush { get; set; }
    public Dictionary<string, Composa.Painting.BrushSettings> EraserPresets { get; set; } = [];
    public Dictionary<string, Composa.Painting.BrushSettings> BrushPresets { get; set; } = [];
    /// <summary>The panels under the Layers panel by title: whether each is shown, collapsed to its header, and how tall it is.</summary>
    public bool TabbedPanels { get; set; }
    public Dictionary<string, DockPanelState> Dock { get; set; } = [];
    /// <summary>Rebound shortcuts by command id: a gesture string, or empty for none. Missing entries keep the default.</summary>
    public Dictionary<string, string> Shortcuts { get; set; } = [];
    public List<string> DisabledScriptPlugins { get; set; } = [];

    /// <summary>ComfyUI is always addressed as a server URL, whether it runs on this computer or another one.</summary>
    public string ComfyServerUrl { get; set; } = "http://127.0.0.1:8188";
    public int ComfyConnectionTimeoutSeconds { get; set; } = 5;
    /// <summary>Only the environment variable NAME is persisted, never the API credential.</summary>
    public string ComfyApiKeyEnvironment { get; set; } = "COMPOSA_COMFY_API_KEY";
    public string AiApiQuality { get; set; } = "low";
    public string AiApiSize { get; set; } = "auto";
    public bool AiLorasEnabled { get; set; }
    public bool UseSelectionGpu { get; set; } = true;
    public bool UseRasterFilterGpu { get; set; } = Composa.Rendering.GpuAcceleration.CanEnable;
    public bool UseEditorGpu { get; set; } = Composa.Rendering.GpuAcceleration.CanEnable;
    public bool AiShowContextBounds { get; set; } = true;
    public bool AiFloatingCollapsed { get; set; }
    public int AiDefaultsRevision { get; set; } = 1;
    /// <summary>Server-side loader identifiers per normalized endpoint; never filesystem paths on the client.</summary>
    public Dictionary<string, Dictionary<string, string>> ComfyModelSelections { get; set; } = [];

    public IReadOnlyDictionary<string, string> ComfyModelsFor(string serverUrl)
    {
        try
        {
            if (ComfyModelSelections.TryGetValue(AI.ComfyServerAddress.Parse(serverUrl).ToString(), out var choices)) return choices;
        }
        catch (FormatException) { /* An invalid saved address can be corrected in Connection settings. */ }
        return new Dictionary<string, string>();
    }

    public void SetComfyModels(string serverUrl, IReadOnlyDictionary<string, string> choices) =>
        ComfyModelSelections[AI.ComfyServerAddress.Parse(serverUrl).ToString()] = new(choices, StringComparer.Ordinal);

    public void MigrateComfyUpscaler(string serverUrl, IEnumerable<AI.WorkflowModelSlot> slots)
    {
        var address = AI.ComfyServerAddress.Parse(serverUrl).ToString();
        if (ComfyModelSelections.ContainsKey(address) || string.IsNullOrWhiteSpace(AiUpscalerModel) || AiUpscalerModel == DefaultUpscalerModel) return;
        var choices = slots.Where(slot => slot.Kind == Composa.AI.EngineAssetKind.Upscaler && slot.Default != AiUpscalerModel)
            .ToDictionary(slot => slot.Key, _ => AiUpscalerModel, StringComparer.Ordinal);
        if (choices.Count > 0) SetComfyModels(address, choices);
    }
    public string? AiEngineId { get; set; }
    /// <summary>Generation pixel budget; the actual dimensions follow the selection/document aspect ratio.</summary>
    public double AiMegapixels { get; set; } = 1;
    public bool AiOriginalSize { get; set; } = true;
    public Composa.AI.AiExpansionMode AiExpansionMode { get; set; } = Composa.AI.AiExpansionMode.MaskedRegion;
    public int AiExpansionMinimumSide { get; set; } = 1024;
    public int AiWholeExpansionMinimumSide { get; set; }
    /// <summary>Pixel budget for each AI reference image; null keeps the original dimensions.</summary>
    public double? AiReferenceMegapixels { get; set; } = 1;
    // Legacy edge controls roundtrip for older builds; TEST generation ignores them.
    public int AiMaskGrow { get; set; } = 16;
    public int AiMaskBlend { get; set; } = 48;
    public int AiMaskBlur { get; set; } = 16;
    /// <summary>FLUX Fill keeps its smaller context and soft edge independently of other operations.</summary>
    public int AiFluxFillMaskGrow { get; set; } = 4;
    public int AiFluxFillMaskBlend { get; set; } = 8;
    public int AiFluxFillMaskBlur { get; set; } = 4;
    public double AiFluxFillMaskContext { get; set; } = 1.2;
    public string AiColorMatch { get; set; } = "off";
    public int AiGptMaskGrow { get; set; } = 4;
    public int AiGptMaskBlend { get; set; } = 8;
    public bool ComfyAdditionalPromptEnabled { get; set; } = true;
    public string ComfyAdditionalPrompt { get; set; } = Composa.AI.AiPromptDefaults.PreserveAppearance;
    public bool AiAlignMaskedEdits { get; set; } = true;
    public string? EnhancerProvider { get; set; }
    public string EnhancerRemoteCli { get; set; } = "codex";
    public string EnhancerCodexPath { get; set; } = "";
    public string EnhancerCodexModel { get; set; } = "";
    public AI.ImageGenerationRoute ImageGenerationRoute { get; set; }
    public Dictionary<Composa.AI.AiTaskKind, AI.ImageGenerationRoute> ImageOperationRoutes { get; set; } = [];
    public string UpscaleCliPath { get; set; } = "";
    public string SegmentationCliPath { get; set; } = "";
    public string SegmentationCliModel { get; set; } = "";

    internal AI.ImageGenerationRoute ImageRouteFor(Composa.AI.AiTaskKind task)
    {
        if (task == Composa.AI.AiTaskKind.SelectSubject) task = Composa.AI.AiTaskKind.ObjectSelection;
        if (ImageOperationRoutes.TryGetValue(task, out var route) &&
            (route is AI.ImageGenerationRoute.ComfyWorkflow or AI.ImageGenerationRoute.LocalCodex or AI.ImageGenerationRoute.DirectApi
                || route == AI.ImageGenerationRoute.BuiltIn && task == Composa.AI.AiTaskKind.ObjectSelection)) return route;
        if (task == Composa.AI.AiTaskKind.ObjectSelection)
            return ObjectSelectionModel == ObjectSelectionSource.ComfyUI ? AI.ImageGenerationRoute.ComfyWorkflow : AI.ImageGenerationRoute.BuiltIn;
        return task is Composa.AI.AiTaskKind.GenerateImage or Composa.AI.AiTaskKind.ImageEdit or Composa.AI.AiTaskKind.GenerativeFill
            ? ImageGenerationRoute : AI.ImageGenerationRoute.ComfyWorkflow;
    }
    public string ImageGenerationCodexPath { get; set; } = "";
    public string ImageGenerationApiUrl { get; set; } = "";
    public string ImageGenerationApiModel { get; set; } = "";
    public string ImageApiDisplayName { get; set; } = "";
    public Dictionary<Composa.AI.AiTaskKind, string> ImageOperationApiModels { get; set; } = [];
    public Dictionary<string, Dictionary<string, string>> ImageApiCatalogs { get; set; } = [];
    public Dictionary<string, string> EnhancerServerModels { get; set; } = [];
    public string EnhancerServerApiUrl { get; set; } = "";
    public string EnhancerServerApiMode { get; set; } = "auto";
    public string EffectiveEnhancerProvider() => EnhancerProvider ?? (EnhancerUseCompatibleApi ? "direct" : "comfy");
    public bool EnhancerUseCompatibleApi { get; set; }
    public string EnhancerApiUrl { get; set; } = "http://127.0.0.1:1234/v1";
    public string EnhancerApiModel { get; set; } = "";
    public string EnhancerApiDisplayName { get; set; } = "";
    public Dictionary<string, Dictionary<string, string>> EnhancerApiCatalogs { get; set; } = [];
    public string EnhancerModel { get; set; } = "gpt-6-luna";
    public int EnhancerMaxTokens { get; set; } = 4096;
    public string EnhancerEffort { get; set; } = "default";
    public Dictionary<string, string> EnhancerInstructions { get; set; } = [];
    public Dictionary<string, AiPromptSetting> AiPackPrompts { get; set; } = [];
    public AiPromptSetting PromptFor(string engineId) => AiPackPrompts.GetValueOrDefault(engineId)
        ?? new(ComfyAdditionalPromptEnabled, ComfyAdditionalPrompt);
    public double AiMaskContext { get; set; } = 2;
    /// <summary>GPT crop context in source pixels, independent of FLUX context and final mask feathering.</summary>
    public int AiGptContextPadding { get; set; } = AI.PartnerImageInputs.DefaultContextPadding;
    public Dictionary<string, Dictionary<string, AiOperationSettings>> GenerationPresets { get; set; } = [];
    // The model key identifies an API model or a checkpoint. Engine ids remain routing hints for installed workflows.
    public Dictionary<string, string> AiModelRoutes { get; set; } = [];
    public Dictionary<string, string> AiTaskModelKeys { get; set; } = [];
    public Dictionary<string, string> AiTaskEngineIds { get; set; } = [];
    public string? EngineForTask(Composa.AI.AiTaskKind task)
    {
        if (AiTaskEngineIds.TryGetValue((task == Composa.AI.AiTaskKind.SelectSubject ? Composa.AI.AiTaskKind.ObjectSelection : task).ToString(), out var id))
            return string.IsNullOrEmpty(id) ? null : id;
        return task == Composa.AI.AiTaskKind.GenerateImage ? "chatgpt-image-2.5" : "flux2-klein-intel-xpu";
    }
    private const string DefaultUpscalerModel = "4x-UltraSharpV2.safetensors";
    public string AiUpscalerModel { get; set; } = DefaultUpscalerModel;
    public long AiSeed { get; set; } = -1;
    public int AiVariants { get; set; } = 1;
    public Composa.AI.AiVariantMode AiVariantMode { get; set; } = Composa.AI.AiVariantMode.List;
    public int AiUpscaleFactor { get; set; } = 2;
    public List<AiLoraSetting> AiLoras { get; set; } = [];
    /// <summary>Advanced/generation preferences are independent for every workflow and editor task.</summary>
    public Dictionary<string, AiOperationSettings> AiOperations { get; set; } = [];

    private static string OperationKey(string? engineId, Composa.AI.AiTaskKind task) =>
        (engineId ?? "default") + "/" + (task == Composa.AI.AiTaskKind.SelectSubject ? Composa.AI.AiTaskKind.ObjectSelection : task);

    public AiOperationSettings OperationFor(string? engineId, Composa.AI.AiTaskKind task, bool paid = false)
    {
        if (AiOperations.TryGetValue(OperationKey(engineId, task), out var saved)) return saved with { Loras = [..saved.Loras] };
        // Legacy fields are a read-only migration seed. Editing one profile never changes another seed.
        var fill = engineId == "flux2-klein-intel-xpu" && task == Composa.AI.AiTaskKind.GenerativeFill;
        var profile = new AiOperationSettings
        {
            Scene = task==Composa.AI.AiTaskKind.ChangeBackground ? new() {Denoise=.95} : new(),
            OriginalSize = AiOriginalSize, Megapixels = AiMegapixels, ReferenceMegapixels = AiReferenceMegapixels,
            MaskGrow = paid ? AiGptMaskGrow : fill ? AiFluxFillMaskGrow : AiMaskGrow,
            MaskBlend = paid ? AiGptMaskBlend : fill ? AiFluxFillMaskBlend : AiMaskBlend,
            MaskBlur = fill ? AiFluxFillMaskBlur : AiMaskBlur, MaskContext = fill ? AiFluxFillMaskContext : AiMaskContext,
            ColorMatch = AiColorMatch, GptContextPadding = AiGptContextPadding,
            Seed = AiSeed, Variants = AiVariants, VariantMode = AiVariantMode,
            LorasEnabled = AiLorasEnabled, Loras = [..AiLoras], ApiQuality = AiApiQuality, UpscaleFactor = AiUpscaleFactor,
            ExpansionMode = AiExpansionMode, ExpansionMinimumSide = AiExpansionMinimumSide, WholeExpansionMinimumSide = AiWholeExpansionMinimumSide
        };
        return engineId == "flux2-klein-intel-xpu" && task == Composa.AI.AiTaskKind.GenerativeExpand
            ? profile with { OriginalSize = true, ReferenceMegapixels = 1, VariantMode = Composa.AI.AiVariantMode.List,
                MaskGrow = 16, MaskBlend = 48, MaskBlur = 16, MaskContext = 2, ColorMatch = "subtle", Seed = -1 }
            : profile;
    }

    public void SetOperation(string? engineId, Composa.AI.AiTaskKind task, AiOperationSettings profile) =>
        AiOperations[OperationKey(engineId, task)] = profile with { Loras = [..profile.Loras] };
    public List<string> CustomLayerTags { get; set; } = [];

    public string AssistantServerUrl { get; set; } = "http://127.0.0.1:8080";
    public string AssistantServerExecutable { get; set; } = Environment.GetEnvironmentVariable("COMPOSA_LLAMA_SERVER") ?? "";
    public string AssistantModelPath { get; set; } = Environment.GetEnvironmentVariable("COMPOSA_LLAMA_MODEL") ?? "";
    public int AssistantContextSize { get; set; } = 16384;
    public int AssistantMaxTokens { get; set; } = 4096;
    public string AssistantReasoning { get; set; } = "medium";
    public double AssistantTemperature { get; set; } = 0.15;
    public int AssistantGpuLayers { get; set; } = -1;
    public string AssistantProjectorPath { get; set; } = "";
    public List<string> AssistantReadFolders { get; set; } = [];
    public string AssistantMode { get; set; } = "auto";
    public bool AssistantAutoStart { get; set; } = true;
    public string AssistantProvider { get; set; } = "local";
    public string AssistantApiUrl { get; set; } = "";
    public string AssistantApiModel { get; set; } = "";
    public string AssistantApiKeyEnvironment { get; set; } = "COMPOSA_ASSISTANT_API_KEY";
    public bool AssistantVision { get; set; }
    public bool AssistantJsonResponse { get; set; }
    public bool AssistantApplyEdits { get; set; } = true;
    [System.Text.Json.Serialization.JsonIgnore]
    public string AssistantApiKey { get; set; } = "";

    /// <summary>Whether the MCP server runs, so an AI agent can drive the editor. Off until someone switches it on.</summary>
    public bool AllowAiControl { get; set; }

    /// <summary>Whether to look for a newer version at launch. The manual check in the Help menu ignores this.</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>When the last automatic check ran, so it happens at most once a day.</summary>
    public DateTime? LastUpdateCheck { get; set; }
    /// <summary>A version the user dismissed. Only that one stays quiet; the next is announced.</summary>
    public string? SkippedVersion { get; set; }

    private static string FilePath => Path.Combine(AppPaths.Config, "settings.json");

    /// <summary>Tests and other hosts switch persistence off so they never touch the user's files.</summary>
    public static bool Persist { get; set; } = true;

    [System.Text.Json.Serialization.JsonIgnore]
    public string? StorageNotice { get; private set; }

    public static Settings Load()
    {
        return Persist ? LoadFrom(FilePath) : new Settings();
    }

    internal static Settings LoadFrom(string path)
    {
        try
        {
            Settings settings;
            try { settings = File.Exists(path) || File.Exists(path + ".bak") ? FromJson(File.ReadAllText(path)) : new Settings(); }
            catch (Exception) when (File.Exists(path + ".bak"))
            {
                settings = FromJson(File.ReadAllText(path + ".bak"));
                settings.StorageNotice = "Preferences restored from the last backup.";
                settings.recoveredPreferences = true;
            }
            // Explicit launch overrides must win over paths remembered on another computer.
            if (Environment.GetEnvironmentVariable("COMPOSA_LLAMA_SERVER") is { Length: > 0 } server) settings.AssistantServerExecutable = server;
            if (Environment.GetEnvironmentVariable("COMPOSA_LLAMA_MODEL") is { Length: > 0 } model) settings.AssistantModelPath = model;
            if (Environment.GetEnvironmentVariable("COMPOSA_LLAMA_URL") is { Length: > 0 } url) settings.AssistantServerUrl = url;
            return settings;
        }
        catch { return new Settings { StorageNotice = "Preferences could not be read. Using defaults.", recoveredPreferences = true }; }
    }

    private static readonly JsonSerializerOptions PreferencesJson = new() { MaxDepth = 128, WriteIndented = true };

    internal Settings CopyPreferences() => FromJson(JsonSerializer.Serialize(this, PreferencesJson));

    internal static Settings FromJson(string json)
    {
        var value = JsonSerializer.Deserialize<Settings>(json, PreferencesJson) ?? new Settings();
        if (value.Language is not ("en" or "ru")) value.Language = "en";
        value.ImageOperationApiModels ??= [];
        value.ImageApiCatalogs ??= [];
        value.EnhancerApiCatalogs ??= [];
        value.EraserPresets ??= [];
        value.BrushPresets ??= [];
        value.RecentCanvasPresets ??= [];
        value.UserCanvasPresets ??= [];
        value.DisabledScriptPlugins ??= [];
        if (!Enum.IsDefined(value.Theme)) value.Theme = EditorTheme.Classic;

        // Preserve explicit native SeedVR2 choices when adopting the official template defaults.
        foreach (var choices in value.ComfyModelSelections.Values)
        foreach (var (node, type, input, oldDefault, newDefault) in new[]
        {
            ("model", "UNETLoader", "unet_name", "seedvr2_3b_fp16.safetensors", "seedvr2_7b_int8_convrot.safetensors"),
            ("vae", "VAELoader", "vae_name", "ema_vae_fp16.safetensors", "seedvr2_ema_vae_fp16.safetensors")
        })
        {
            var oldKey = JsonSerializer.Serialize(new[] { "seedvr2-native", type, node, input, oldDefault });
            if (choices.Remove(oldKey, out var selected))
                choices.TryAdd(JsonSerializer.Serialize(new[] { "seedvr2-native", type, node, input, newDefault }), selected);
        }
        // Only SAM Quality and installed BiRefNet models are offered now.
        if (value.ObjectSelectionModel is not (ObjectSelectionSource.EfficientSamS or ObjectSelectionSource.ComfyUI)
            || value.ObjectSelectionModel == ObjectSelectionSource.ComfyUI && value.ObjectSelectionComfyModel is { } model
                && !model.Contains("birefnet", StringComparison.OrdinalIgnoreCase))
            value.ObjectSelectionModel = ObjectSelectionSource.EfficientSamS;
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = PreferencesJson.MaxDepth });
        // Older releases recorded the build version when the welcome dialog closed.
        if (document.RootElement.TryGetProperty("SeenWelcomeVersions", out var seenVersions)
            && seenVersions.ValueKind == JsonValueKind.Array
            && seenVersions.EnumerateArray().Any(version => version.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(version.GetString())))
            value.EditorWelcomeSeen = true;
        if (!document.RootElement.TryGetProperty(nameof(PromptSelectionRevision), out _))
        {
            if (value.ObjectSelectionModel is ObjectSelectionSource.MobileSam or ObjectSelectionSource.EfficientSamTi)
                value.ObjectSelectionModel = ObjectSelectionSource.EfficientSamS;
            value.PromptSelectionRevision = 1;
        }
        if (!document.RootElement.TryGetProperty(nameof(AiDefaultsRevision), out _))
        {
            // One-time adoption of the requested preview defaults. Afterwards keep all custom values.
            value.AiOriginalSize = true; value.AiLorasEnabled = false;
            value.AiMaskGrow = 16; value.AiMaskBlend = 48; value.AiMaskBlur = 16; value.AiColorMatch = "off"; value.AiMaskContext = 2;
            value.AiGptMaskGrow = 4; value.AiGptMaskBlend = 8; value.AiGptContextPadding = 0;
            foreach (var task in new[] { Composa.AI.AiTaskKind.GenerativeFill, Composa.AI.AiTaskKind.GenerativeExpand, Composa.AI.AiTaskKind.GenerateImage })
                if (!value.AiTaskEngineIds.TryGetValue(task.ToString(), out var id) || id is "" or "chatgpt-image-2.5" or "flux2-klein-intel-xpu")
                    value.AiTaskEngineIds[task.ToString()] = task == Composa.AI.AiTaskKind.GenerateImage ? "chatgpt-image-2.5" : "flux2-klein-intel-xpu";
            value.AiDefaultsRevision = 1;
        }
        return value;
    }

    private bool recoveredPreferences;

    public void Save()
    {
        if (Persist) SaveTo(FilePath);
    }

    internal void SaveWithWorkflows(AI.WorkflowPreferences workflows)
    {
        var previous = Workflows;
        Workflows = workflows;
        if (!Persist || SaveTo(FilePath)) return;
        Workflows = previous;
        throw new IOException(StorageNotice);
    }

    internal bool SaveTo(string path)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(this, PreferencesJson));
            if (File.Exists(path) && !recoveredPreferences) File.Replace(temp, path, path + ".bak");
            else File.Move(temp, path, true);
            recoveredPreferences = false;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StorageNotice = "Preferences could not be saved: " + error.Message;
            return false;
        }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    public void AddRecent(string path)
    {
        RecentFiles.Remove(path);
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 12) RecentFiles.RemoveRange(12, RecentFiles.Count - 12);
        Save();
    }
}

/// <summary>How one panel of the side dock was left: shown or not, collapsed to its header or not, and its height when open.</summary>
public sealed record DockPanelState(bool Visible = true, bool Collapsed = false, double Height = 220, bool Left = false,
    int Order = 0, int Column = 0, double ColumnWeight = 1, Guid? TabGroup = null, bool ActiveTab = false, bool Standalone = false);

/// <summary>View chrome only. Documents, pixels, tool options and shortcuts never belong to a workspace.</summary>
public sealed record WorkspaceLayout(Dictionary<string, DockPanelState> Panels, bool CanvasOnly, bool TabbedPanels = false, string ToolsPosition = "Left");

public sealed record AiLoraSetting(string Name = "", double Strength = 1, bool Enabled = true);
public sealed record AiPromptSetting(bool Enabled, string Text);

public sealed record AiOperationSettings
{
    public bool? AlignPixels { get; init; }
    public Composa.AI.SceneEditSettings Scene { get; init; } = new();
    public bool OriginalSize { get; init; } = true;
    public double Megapixels { get; init; } = 1;
    public double? ReferenceMegapixels { get; init; } = 1;
    public string FluxMemory { get; init; } = "auto";
    public int MaskGrow { get; init; } = 16;
    public int MaskBlend { get; init; } = 48;
    public int MaskBlur { get; init; } = 16;
    public double MaskContext { get; init; } = 2;
    public string ColorMatch { get; init; } = "off";
    public int GptContextPadding { get; init; }
    public long Seed { get; init; } = -1;
    public int? WorkflowSteps { get; init; }
    public string? WorkflowSampler { get; init; }
    public int Variants { get; init; } = 1;
    public Composa.AI.AiVariantMode VariantMode { get; init; } = Composa.AI.AiVariantMode.List;
    internal IReadOnlyList<Composa.AI.AiLora> ActiveLoras(Composa.AI.EngineProfile? engine) =>
        engine?.Lora.Supported == true && LorasEnabled
            ? Loras.Take(3).Select(lora => new Composa.AI.AiLora(lora.Name, lora.Strength, lora.Enabled)).ToArray() : [];
    public bool LorasEnabled { get; init; }
    public List<AiLoraSetting> Loras { get; init; } = [];
    public string ApiQuality { get; init; } = "low";
    public int UpscaleFactor { get; init; } = 2;
    public Composa.AI.AiExpansionMode ExpansionMode { get; init; } = Composa.AI.AiExpansionMode.MaskedRegion;
    public int ExpansionMinimumSide { get; init; } = 1024;
    public int WholeExpansionMinimumSide { get; init; }
}
