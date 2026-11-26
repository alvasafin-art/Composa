using Composa.AI;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.AI;

public enum ImageGenerationRoute { ComfyWorkflow, LocalCodex, DirectApi, BuiltIn, RemoteCodex }

internal sealed record DirectImageGenerator(ImageGenerationRoute Route, string CodexPath, string ApiAddress, string ApiModel)
{
    internal static readonly AiTaskKind[] Operations = Enum.GetValues<AiTaskKind>().Where(task => task != AiTaskKind.SelectSubject).ToArray();
    internal static readonly ImageGenerationRoute[] Connections = [ImageGenerationRoute.ComfyWorkflow, ImageGenerationRoute.LocalCodex,
        ImageGenerationRoute.DirectApi, ImageGenerationRoute.RemoteCodex];
    internal AiTaskKind Task { get; init; }
    internal string UtilityPath { get; init; } = "";
    internal string UtilityModel { get; init; } = "";
    internal string CodexDisplayName { get; init; } = "";
    internal string CodexLanguageModel { get; init; } = "";
    internal string CodexReasoningEffort { get; init; } = "default";
    internal string ComfyServer { get; init; } = "";
    internal string RemoteLanguageModel { get; init; } = "";
    internal string RemoteReasoningEffort { get; init; } = "default";

    internal string Model => Route switch
    {
        ImageGenerationRoute.LocalCodex => "gpt-image-2",
        ImageGenerationRoute.RemoteCodex => "image_generation",
        _ => ApiModel.Trim()
    };
    internal bool UsesConfiguredUpscaler => Task == AiTaskKind.Upscale && Route == ImageGenerationRoute.DirectApi && Model.Length > 0;
    internal string Id => Route switch
    {
        ImageGenerationRoute.LocalCodex => "direct-codex-image",
        ImageGenerationRoute.RemoteCodex => "server-codex-image",
        _ => "direct-api-image"
    };
    internal const string CodexImageName = "GPT Image CLI";
    internal string Label => Task switch
    {
        AiTaskKind.Upscale => Route == ImageGenerationRoute.LocalCodex ? "Real-ESRGAN · CLI" : UsesConfiguredUpscaler ? Model : "Polza AI · Topaz Upscale",
        AiTaskKind.ObjectSelection => Route == ImageGenerationRoute.LocalCodex ? "rembg · CLI" : "BiRefNet · fal API",
        _ when Route == ImageGenerationRoute.LocalCodex => string.IsNullOrWhiteSpace(CodexDisplayName) ? CodexImageName : CodexDisplayName,
        _ when Route == ImageGenerationRoute.RemoteCodex => CodexImageName,
        _ => Model.Length > 0 ? $"{Model} · API" : "Direct image API"
    };

    internal static DirectImageGenerator From(Settings settings) =>
        new(settings.ImageGenerationRoute, settings.ImageGenerationCodexPath, settings.ImageGenerationApiUrl, settings.ImageGenerationApiModel)
        { CodexDisplayName = settings.ImageCodexDisplayName, CodexLanguageModel = settings.ImageCodexLanguageModel,
            CodexReasoningEffort = settings.ImageCodexReasoningEffort, ComfyServer = settings.ComfyServerUrl,
            RemoteLanguageModel = settings.ImageRemoteCodexModel, RemoteReasoningEffort = settings.ImageRemoteCodexEffort };

    internal static DirectImageGenerator For(Settings settings, AiTaskKind task) => From(settings) with
    {
        Task = task,
        Route = settings.ImageRouteFor(task),
        ApiModel = settings.ImageOperationApiModels.GetValueOrDefault(task) ??
            (ImageOperationModels.IsUtility(task) ? "" : settings.ImageGenerationApiModel),
        UtilityPath = task == AiTaskKind.Upscale ? settings.UpscaleCliPath : settings.SegmentationCliPath,
        UtilityModel = settings.SegmentationCliModel
    };

    internal EngineProfile PromptProfile() => new()
    {
        Id = Id, DisplayName = Label, ModelName = Model, ApiModel = Model,
        Tasks = Operations.Select(task => new EngineTaskBinding { Task = task }).ToList()
    };

    internal static string RouteLabel(ImageGenerationRoute route) => route switch
    {
        ImageGenerationRoute.LocalCodex => "CLI on this computer",
        ImageGenerationRoute.RemoteCodex => "CLI on ComfyUI server",
        ImageGenerationRoute.DirectApi => "API directly",
        ImageGenerationRoute.BuiltIn => "Built-in",
        _ => "ComfyUI workflow"
    };

    internal static bool IsDirect(ImageGenerationRoute route) => route is ImageGenerationRoute.LocalCodex or ImageGenerationRoute.DirectApi
        or ImageGenerationRoute.RemoteCodex;

    internal static Uri Endpoint(string value) => DirectImageApi.Parse(value).CredentialEndpoint;
    internal bool UsesNativeMask => Route == ImageGenerationRoute.DirectApi && DirectImageApi.Parse(ApiAddress).UsesNativeMask;

    internal bool SupportsCustomSize => (Model is "gpt-image-2" or "gpt-image-2.5-sunburst" or "gpt-image-2.5-flare"
        or "gpt-image-2.6-sunburst" or "gpt-image-2.6-flare" ||
        Model.StartsWith("gpt-image-2-20", StringComparison.Ordinal) ||
        Model.StartsWith("gpt-image-2.5-sunburst-20", StringComparison.Ordinal) ||
        Model.StartsWith("gpt-image-2.5-flare-20", StringComparison.Ordinal)) &&
        Route == ImageGenerationRoute.DirectApi && DirectImageApi.SupportsNativeImageRequest(ApiAddress);

    internal (int Width, int Height) GenerationSize(int width, int height)
    {
        if (!SupportsCustomSize || width <= 0 || height <= 0) return RequestSize(width, height);
        // Preserve the requested pixel budget when an extreme source aspect needs letterboxing.
        if (width > 3L * height || height > 3L * width)
        {
            var longEdge = (int)Math.Round(Math.Sqrt(3.0 * width * height));
            var shortEdge = (longEdge + 2) / 3;
            (width, height) = width > height ? (longEdge, shortEdge) : (shortEdge, longEdge);
        }
        return PartnerImageSize.Plan(width, height, DocumentLimits.MinimumGenerationPixels);
    }

    internal static (int Width, int Height) RequestSize(int width, int height) =>
        width > height * 1.2 ? (1536, 1024) : height > width * 1.2 ? (1024, 1536) : (1024, 1024);

    internal static string? ReadKey(Uri endpoint) =>
        (Settings.Persist ? UserApiKeys.Read(UserApiKeys.Target("ImageApi", endpoint)) : null)
        ?? Environment.GetEnvironmentVariable("COMPOSA_IMAGE_API_KEY");

    internal string? Availability(AiTaskKind task, int references = 0)
    {
        if (Route == ImageGenerationRoute.RemoteCodex)
        {
            if (task is AiTaskKind.Upscale or AiTaskKind.ObjectSelection or AiTaskKind.SelectSubject)
                return "Codex CLI on the ComfyUI server supports generative image operations.";
            if (references > 6) return "Choose at most six reference images.";
            try { _ = ComfyServerAddress.Parse(ComfyServer); return null; }
            catch (FormatException error) { return error.Message; }
        }
        if (task == AiTaskKind.Upscale && !UsesConfiguredUpscaler || task is AiTaskKind.ObjectSelection or AiTaskKind.SelectSubject)
            return DirectImageUtilities.Availability(this);
        if (references > 6) return "Choose at most six reference images.";
        if (Route == ImageGenerationRoute.LocalCodex)
            return LocalCodex.FindExecutable(CodexPath) != null ? null : "Codex CLI was not found. Select its executable in AI Settings → Image generation.";
        if (string.IsNullOrWhiteSpace(ApiModel)) return "Choose the image model in AI Settings → Image generation.";
        try
        {
            return string.IsNullOrWhiteSpace(ReadKey(Endpoint(ApiAddress)))
                ? "Add the image API key in AI Settings → Image generation." : null;
        }
        catch (FormatException error) { return error.Message; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return "Composa could not read the saved image API key."; }
    }

    internal async Task<SKBitmap> GenerateAsync(string prompt, int width, int height, CancellationToken token,
        string? draftKey = null, DirectImageFiles? files = null, Action<ImageApiRate>? onCharge = null, bool edit = false)
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new InvalidOperationException("Enter a prompt to generate an image.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            var (requestWidth, requestHeight) = edit ? (width, height) : GenerationSize(width, height);
            if (Route == ImageGenerationRoute.LocalCodex)
                return await LocalCodex.GenerateAsync(CodexPath, prompt, requestWidth, requestHeight, timeout.Token,
                    files?.Images, CodexLanguageModel, CodexReasoningEffort);
            if (Route == ImageGenerationRoute.RemoteCodex)
                return await RemoteCodexImage.GenerateAsync(ComfyServer, RemoteLanguageModel, RemoteReasoningEffort,
                    prompt, requestWidth, requestHeight, files, edit, timeout.Token);
            if (Route != ImageGenerationRoute.DirectApi) throw new InvalidOperationException("Choose a direct image connection.");
            var api = DirectImageApi.Parse(ApiAddress);
            if (string.IsNullOrWhiteSpace(Model)) throw new InvalidOperationException("Choose the image model in AI Settings → Image generation.");
            var key = draftKey ?? ReadKey(api.CredentialEndpoint);
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Add the image API key in AI Settings → Image generation.");
            return await api.GenerateAsync(key, Model, prompt, requestWidth, requestHeight, files, timeout.Token, onCharge);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException("The image generator timed out.");
        }
    }

    internal static SKBitmap Decode(Stream file, bool pngOnly = true)
    {
        if (file.CanSeek && file.Length > DirectImageApi.MaximumResponseBytes) throw new InvalidDataException("The generated image exceeds the response size limit.");
        using var codec = SKCodec.Create(file, out var status);
        if (codec == null || status != SKCodecResult.Success || (pngOnly ? codec.EncodedFormat != SKEncodedImageFormat.Png
                : codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp))
            || !DocumentLimits.FitsSurface(codec.Info.Width, codec.Info.Height))
            throw new InvalidDataException("The generator returned an unreadable image or unsupported dimensions.");
        var bitmap = Pixels.NewColor(codec.Info.Width, codec.Info.Height);
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) == SKCodecResult.Success) return bitmap;
        bitmap.Dispose();
        throw new InvalidDataException("The generated image could not be decoded.");
    }
}
