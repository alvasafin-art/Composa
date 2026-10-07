using System.Text.Json;

namespace Composa.App;

public enum ObjectSelectionSource { AnySubject, Person, PlainBackdrop, ComfyUI, MobileSam = 5, EfficientSamTi = 6 }

/// <summary>Preferences remembered between launches, stored in the platform's config directory (<see cref="AppPaths.Config"/>).</summary>
public sealed class Settings
{
    public List<string> RecentFiles { get; set; } = [];
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;
    public bool Maximized { get; set; }
    public int JpegQuality { get; set; } = 90;
    public bool ShowPixelGrid { get; set; } = true;
    /// <summary>Toggles that belong to the person rather than to a document, kept the way Photoshop keeps its tool options.</summary>
    public bool ShowTransformControls { get; set; } = true;
    public bool AutoSelect { get; set; } = true;
    public ObjectSelectionSource ObjectSelectionModel { get; set; } = ObjectSelectionSource.AnySubject;
    public string? ObjectSelectionComfyModel { get; set; }
    public Composa.Editing.ViewOptions View { get; set; } = new();
    public Composa.Painting.BrushSettings Brush { get; set; } = new();
    public Dictionary<string, Composa.Painting.BrushSettings> BrushPresets { get; set; } = [];
    /// <summary>The panels under the Layers panel by title: whether each is shown, collapsed to its header, and how tall it is.</summary>
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
    public bool AiShowContextBounds { get; set; } = true;
    public bool AiFloatingCollapsed { get; set; }
    public int AiDefaultsRevision { get; set; } = 1;
    /// <summary>Server-side loader identifiers per normalized endpoint; never filesystem paths on the client.</summary>
    public Dictionary<string, Dictionary<string, string>> ComfyModelSelections { get; set; } = [];

    public IReadOnlyDictionary<string, string> ComfyModelsFor(string serverUrl) =>
        ComfyModelSelections.TryGetValue(AI.ComfyServerAddress.Parse(serverUrl).ToString(), out var choices)
            ? choices : new Dictionary<string, string>();

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
    public Dictionary<string, AiPromptSetting> AiPackPrompts { get; set; } = [];
    public AiPromptSetting PromptFor(string engineId) => AiPackPrompts.GetValueOrDefault(engineId)
        ?? new(ComfyAdditionalPromptEnabled, ComfyAdditionalPrompt);
    public double AiMaskContext { get; set; } = 2;
    /// <summary>GPT crop context in source pixels, independent of FLUX context and final mask feathering.</summary>
    public int AiGptContextPadding { get; set; } = AI.PartnerImageInputs.DefaultContextPadding;
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
    public int AssistantMaxTokens { get; set; } = 1536;
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

    public static Settings Load()
    {
        if (!Persist) return new Settings();
        try { return File.Exists(FilePath) ? FromJson(File.ReadAllText(FilePath)) : new Settings(); }
        catch { return new Settings(); } // A damaged settings file only costs the remembered preferences.
    }

    internal static Settings FromJson(string json)
    {
        var value = JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
        // Preview 16's optional native model was removed. Keep all other preferences intact.
        if (!Enum.IsDefined(value.ObjectSelectionModel)) value.ObjectSelectionModel = ObjectSelectionSource.AnySubject;
        using var document = JsonDocument.Parse(json);
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

    public void Save()
    {
        if (!Persist) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* Preferences are a convenience; failing to store them must not interrupt editing. */ }
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
public sealed record DockPanelState(bool Visible = true, bool Collapsed = false, double Height = 220);

public sealed record AiLoraSetting(string Name = "", double Strength = 1, bool Enabled = true);
public sealed record AiPromptSetting(bool Enabled, string Text);

public sealed record AiOperationSettings
{
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
    public int Variants { get; init; } = 1;
    public Composa.AI.AiVariantMode VariantMode { get; init; } = Composa.AI.AiVariantMode.List;
    public bool LorasEnabled { get; init; }
    public List<AiLoraSetting> Loras { get; init; } = [];
    public string ApiQuality { get; init; } = "low";
    public int UpscaleFactor { get; init; } = 2;
    public Composa.AI.AiExpansionMode ExpansionMode { get; init; } = Composa.AI.AiExpansionMode.MaskedRegion;
    public int ExpansionMinimumSide { get; init; } = 1024;
    public int WholeExpansionMinimumSide { get; init; }
}
