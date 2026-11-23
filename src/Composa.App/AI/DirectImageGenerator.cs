using Composa.AI;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.AI;

public enum ImageGenerationRoute { ComfyWorkflow, LocalCodex, DirectApi }

internal sealed record DirectImageGenerator(ImageGenerationRoute Route, string CodexPath, string ApiAddress, string ApiModel)
{
    internal string Model => Route == ImageGenerationRoute.LocalCodex ? "gpt-image-2" : ApiModel.Trim();
    internal string Id => Route == ImageGenerationRoute.LocalCodex ? "direct-codex-image" : "direct-api-image";
    internal string Label => Route == ImageGenerationRoute.LocalCodex ? $"{Model} · Codex CLI" : $"{Model} · API";

    internal static DirectImageGenerator From(Settings settings) =>
        new(settings.ImageGenerationRoute, settings.ImageGenerationCodexPath, settings.ImageGenerationApiUrl, settings.ImageGenerationApiModel);

    internal EngineProfile PromptProfile() => new()
    {
        Id = Id, DisplayName = Label, ModelName = Model, ApiModel = Model,
        Tasks = [new() { Task = AiTaskKind.GenerateImage }, new() { Task = AiTaskKind.ImageEdit },
            new() { Task = AiTaskKind.GenerativeFill }]
    };

    internal static string RouteLabel(ImageGenerationRoute route) => route switch
    {
        ImageGenerationRoute.LocalCodex => "CLI on this computer",
        ImageGenerationRoute.DirectApi => "API directly",
        _ => "ComfyUI workflow"
    };

    internal static bool Controls(AiTaskKind task) => task is not (AiTaskKind.ObjectSelection or AiTaskKind.SelectSubject or AiTaskKind.Upscale);

    internal static Uri Endpoint(string value) => DirectImageApi.Parse(value).CredentialEndpoint;
    internal bool UsesNativeMask => Route == ImageGenerationRoute.DirectApi && DirectImageApi.Parse(ApiAddress).UsesNativeMask;

    internal static (int Width, int Height) RequestSize(int width, int height) =>
        width > height * 1.2 ? (1536, 1024) : height > width * 1.2 ? (1024, 1536) : (1024, 1024);

    internal static string? ReadKey(Uri endpoint) =>
        (Settings.Persist ? UserApiKeys.Read(UserApiKeys.Target("ImageApi", endpoint)) : null)
        ?? Environment.GetEnvironmentVariable("COMPOSA_IMAGE_API_KEY");

    internal string? Availability(AiTaskKind task, int references = 0)
    {
        if (task is not (AiTaskKind.GenerateImage or AiTaskKind.ImageEdit or AiTaskKind.GenerativeFill))
            return "This operation is currently unavailable for this connection.";
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
        string? draftKey = null, DirectImageFiles? files = null)
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new InvalidOperationException("Enter a prompt to generate an image.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            var (requestWidth, requestHeight) = RequestSize(width, height);
            if (Route == ImageGenerationRoute.LocalCodex)
                return await LocalCodex.GenerateAsync(CodexPath, prompt, requestWidth, requestHeight, timeout.Token, files?.Images);
            if (Route != ImageGenerationRoute.DirectApi) throw new InvalidOperationException("Choose a direct image connection.");
            var api = DirectImageApi.Parse(ApiAddress);
            if (string.IsNullOrWhiteSpace(Model)) throw new InvalidOperationException("Choose the image model in AI Settings → Image generation.");
            var key = draftKey ?? ReadKey(api.CredentialEndpoint);
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Add the image API key in AI Settings → Image generation.");
            return await api.GenerateAsync(key, Model, prompt, requestWidth, requestHeight, files, timeout.Token);
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
