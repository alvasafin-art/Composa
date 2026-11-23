using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Composa.AI;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.AI;

public enum ImageGenerationRoute { ComfyWorkflow, LocalCodex, DirectApi }

internal sealed record DirectImageGenerator(ImageGenerationRoute Route, string CodexPath, string ApiAddress, string ApiModel)
{
    private const int MaximumResponseBytes = 64 * 1024 * 1024;
    internal string Model => Route == ImageGenerationRoute.LocalCodex ? "gpt-image-2" : ApiModel.Trim();
    internal string Id => Route == ImageGenerationRoute.LocalCodex ? "direct-codex-image" : "direct-api-image";
    internal string Label => Route == ImageGenerationRoute.LocalCodex ? $"{Model} · Codex CLI" : $"{Model} · API";

    internal static DirectImageGenerator From(Settings settings) =>
        new(settings.ImageGenerationRoute, settings.ImageGenerationCodexPath, settings.ImageGenerationApiUrl, settings.ImageGenerationApiModel);

    internal EngineProfile PromptProfile() => new()
    {
        Id = Id, DisplayName = Label, ModelName = Model, ApiModel = Model,
        Tasks = [new EngineTaskBinding { Task = AiTaskKind.GenerateImage }]
    };

    internal static string RouteLabel(ImageGenerationRoute route) => route switch
    {
        ImageGenerationRoute.LocalCodex => "CLI on this computer",
        ImageGenerationRoute.DirectApi => "API directly",
        _ => "ComfyUI workflow"
    };

    internal static bool Controls(AiTaskKind task) => task is not (AiTaskKind.ObjectSelection or AiTaskKind.SelectSubject or AiTaskKind.Upscale);

    internal static Uri Endpoint(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.Scheme == "http" && !uri.IsLoopback || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new FormatException("Enter an HTTPS image API address, or an HTTP loopback address, without credentials or query parameters.");
        var path = uri.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/images/generations", StringComparison.OrdinalIgnoreCase))
            path = (path.Length == 0 ? "/v1" : path) + "/images/generations";
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + path);
    }

    internal static string? ReadKey(Uri endpoint) =>
        (Settings.Persist ? UserApiKeys.Read(UserApiKeys.Target("ImageApi", endpoint)) : null)
        ?? Environment.GetEnvironmentVariable("COMPOSA_IMAGE_API_KEY");

    internal string? Availability(AiTaskKind task, int references = 0)
    {
        if (task != AiTaskKind.GenerateImage) return "This connection currently supports text-to-image only.";
        if (references > 0) return "Remove the references to generate by text with this connection.";
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

    internal async Task<SKBitmap> GenerateAsync(string prompt, int width, int height, CancellationToken token, string? draftKey = null)
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new InvalidOperationException("Enter a prompt to generate an image.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            if (Route == ImageGenerationRoute.LocalCodex)
                return await LocalCodex.GenerateAsync(CodexPath, prompt, width, height, timeout.Token);
            if (Route != ImageGenerationRoute.DirectApi) throw new InvalidOperationException("Choose a direct image connection.");
            return await GenerateApiAsync(prompt, width, height, draftKey, timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException("The image generator timed out.");
        }
    }

    private async Task<SKBitmap> GenerateApiAsync(string prompt, int width, int height, string? draftKey, CancellationToken token)
    {
        var endpoint = Endpoint(ApiAddress);
        if (string.IsNullOrWhiteSpace(Model)) throw new InvalidOperationException("Choose the image model in AI Settings → Image generation.");
        var key = draftKey ?? ReadKey(endpoint);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Add the image API key in AI Settings → Image generation.");
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = ComfyServerAddress.UseProxy(endpoint) })
        {
            MaxResponseContentBufferSize = MaximumResponseBytes
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new("Bearer", key.Trim());
        var size = width > height * 1.2 ? "1536x1024" : height > width * 1.2 ? "1024x1536" : "1024x1024";
        request.Content = JsonContent.Create(new { model = Model, prompt, n = 1, size, quality = "low", output_format = "png" });
        try
        {
            using var response = await client.SendAsync(request, token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new InvalidOperationException("The image API refused the credentials or model access.");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Image API request failed (HTTP {(int)response.StatusCode}).");
            await using var body = await response.Content.ReadAsStreamAsync(token);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: token);
            if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() != 1
                || !data[0].TryGetProperty("b64_json", out var image) || image.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("The image API returned no PNG.");
            using var png = new MemoryStream(Convert.FromBase64String(image.GetString()!));
            return Decode(png);
        }
        catch (HttpRequestException) { throw new InvalidOperationException("Could not reach the image API or read its response."); }
        catch (JsonException) { throw new InvalidDataException("The image API returned an invalid image response."); }
        catch (FormatException) { throw new InvalidDataException("The image API returned an invalid PNG."); }
    }

    internal static SKBitmap Decode(Stream file)
    {
        if (file.CanSeek && file.Length > MaximumResponseBytes) throw new InvalidDataException("The generated PNG exceeds the response size limit.");
        using var codec = SKCodec.Create(file, out var status);
        if (codec == null || status != SKCodecResult.Success || codec.EncodedFormat != SKEncodedImageFormat.Png
            || !DocumentLimits.FitsSurface(codec.Info.Width, codec.Info.Height))
            throw new InvalidDataException("The generator returned an unreadable PNG or unsupported dimensions.");
        var bitmap = Pixels.NewColor(codec.Info.Width, codec.Info.Height);
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) == SKCodecResult.Success) return bitmap;
        bitmap.Dispose();
        throw new InvalidDataException("The generated PNG could not be decoded.");
    }
}
