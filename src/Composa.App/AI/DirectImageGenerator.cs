using Composa.AI;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.AI;

public enum ImageGenerationRoute { ComfyWorkflow, LocalCodex, DirectApi, BuiltIn }

internal sealed record DirectImageGenerator(ImageGenerationRoute Route, string CodexPath, string ApiAddress, string ApiModel)
{
    internal static readonly AiTaskKind[] Operations = Enum.GetValues<AiTaskKind>().Where(task => task != AiTaskKind.SelectSubject).ToArray();
    internal static readonly ImageGenerationRoute[] Connections = [ImageGenerationRoute.ComfyWorkflow, ImageGenerationRoute.LocalCodex, ImageGenerationRoute.DirectApi];
    internal AiTaskKind Task { get; init; }
    internal string UtilityPath { get; init; } = "";
    internal string UtilityModel { get; init; } = "";
    internal string CodexDisplayName { get; init; } = "";
    internal string CodexLanguageModel { get; init; } = "";
    internal string CodexReasoningEffort { get; init; } = "default";

    internal string Model => Route == ImageGenerationRoute.LocalCodex ? "gpt-image-2" : ApiModel.Trim();
    internal bool UsesConfiguredUpscaler => Task == AiTaskKind.Upscale && Route == ImageGenerationRoute.DirectApi && Model.Length > 0;
    internal string Id => Route == ImageGenerationRoute.LocalCodex ? "direct-codex-image" : "direct-api-image";
    internal string Label => Task switch
    {
        AiTaskKind.Upscale => Route == ImageGenerationRoute.LocalCodex ? "Real-ESRGAN · CLI" : UsesConfiguredUpscaler ? Model : "Polza AI · Topaz Upscale",
        AiTaskKind.ObjectSelection => Route == ImageGenerationRoute.LocalCodex ? "rembg · CLI" : "BiRefNet · fal API",
        _ => Route == ImageGenerationRoute.LocalCodex ? $"{Model} · Codex CLI" : Model.Length > 0 ? $"{Model} · API" : "Direct image API"
    };

    internal static DirectImageGenerator From(Settings settings) =>
        new(settings.ImageGenerationRoute, settings.ImageGenerationCodexPath, settings.ImageGenerationApiUrl, settings.ImageGenerationApiModel)
        { CodexDisplayName = settings.ImageCodexDisplayName, CodexLanguageModel = settings.ImageCodexLanguageModel,
            CodexReasoningEffort = settings.ImageCodexReasoningEffort };

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
        ImageGenerationRoute.DirectApi => "API directly",
        ImageGenerationRoute.BuiltIn => "Built-in",
        _ => "ComfyUI workflow"
    };

    internal static bool IsDirect(ImageGenerationRoute route) => route is ImageGenerationRoute.LocalCodex or ImageGenerationRoute.DirectApi;

    internal static Uri Endpoint(string value) => DirectImageApi.Parse(value).CredentialEndpoint;
    internal bool UsesNativeMask => Route == ImageGenerationRoute.DirectApi && DirectImageApi.Parse(ApiAddress).UsesNativeMask;

    internal static (int Width, int Height) RequestSize(int width, int height) =>
        width > height * 1.2 ? (1536, 1024) : height > width * 1.2 ? (1024, 1536) : (1024, 1024);

    internal static string? ReadKey(Uri endpoint) =>
        (Settings.Persist ? UserApiKeys.Read(UserApiKeys.Target("ImageApi", endpoint)) : null)
        ?? Environment.GetEnvironmentVariable("COMPOSA_IMAGE_API_KEY");

    internal string? Availability(AiTaskKind task, int references = 0)
    {
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
        string? draftKey = null, DirectImageFiles? files = null, Action<ImageApiRate>? onCharge = null)
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new InvalidOperationException("Enter a prompt to generate an image.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            var (requestWidth, requestHeight) = RequestSize(width, height);
            if (Route == ImageGenerationRoute.LocalCodex)
                return await LocalCodex.GenerateAsync(CodexPath, prompt, requestWidth, requestHeight, timeout.Token,
                    files?.Images, CodexLanguageModel, CodexReasoningEffort);
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
