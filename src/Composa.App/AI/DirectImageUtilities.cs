using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Composa.AI;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.AI;

internal static class DirectImageUtilities
{
    internal const string PolzaAddress = "https://polza.ai/api/v1";
    internal static readonly Uri FalEndpoint = new("https://fal.run/fal-ai/birefnet/v2");
    internal static readonly string PolzaKeyTarget = UserApiKeys.Target("ImageApi", DirectImageGenerator.Endpoint(PolzaAddress));
    internal static readonly string FalKeyTarget = UserApiKeys.Target("SegmentationApi", FalEndpoint);
    private const string DataPrefix = "data:image/png;base64,";

    private static string? ApiKey(bool upscale) =>
        (Settings.Persist ? UserApiKeys.Read(upscale ? PolzaKeyTarget : FalKeyTarget) : null)
        ?? (upscale
            ? Environment.GetEnvironmentVariable("COMPOSA_UPSCALE_API_KEY")
            : Environment.GetEnvironmentVariable("FAL_KEY"));

    internal static string? Availability(DirectImageGenerator connection)
    {
        var upscale = connection.Task == AiTaskKind.Upscale;
        if (connection.Route == ImageGenerationRoute.LocalCodex)
        {
            var command = upscale ? "realesrgan-ncnn-vulkan" : "rembg";
            return FindExecutable(command, connection.UtilityPath) == null
                ? (upscale ? "Real-ESRGAN CLI was not found." : "rembg CLI was not found.") : null;
        }
        try
        {
            return !string.IsNullOrWhiteSpace(ApiKey(upscale)) ? null
                : upscale ? "Add the Polza AI key in AI Settings → Image generation." : "Add the fal API key in AI Settings → Image generation.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return "Composa could not read the saved image API key.";
        }
    }

    internal static async Task<SKBitmap> UpscaleAsync(DirectImageGenerator connection, SKBitmap source, int factor, CancellationToken token)
    {
        if (factor is not (2 or 4)) throw new ArgumentException("Choose an upscale factor of ×2 or ×4.");
        var width = checked(source.Width * factor);
        var height = checked(source.Height * factor);
        if (!DocumentLimits.FitsSurface(width, height))
            throw new InvalidOperationException("The selected upscale factor exceeds the maximum image size.");
        var png = DirectImageEdit.EncodePng(source);
        var result = connection.Route == ImageGenerationRoute.LocalCodex
            ? await RunCliAsync("realesrgan-ncnn-vulkan", connection.UtilityPath, png, (input, output) =>
                ["-i", input, "-o", output, "-s", factor.ToString(System.Globalization.CultureInfo.InvariantCulture), "-f", "png"], token)
            : await DirectImageApi.Parse(PolzaAddress).UpscaleAsync(ApiKey(upscale: true)!, png, factor, token);
        if (result.Width == width && result.Height == height) return result;
        result.Dispose();
        throw new InvalidDataException("The upscaler returned dimensions that do not match the requested scale.");
    }

    internal static async Task<SKBitmap> SelectAsync(DirectImageGenerator connection, SKBitmap source, CancellationToken token)
    {
        var png = DirectImageEdit.EncodePng(source);
        using var result = connection.Route == ImageGenerationRoute.LocalCodex
            ? await RunCliAsync("rembg", connection.UtilityPath, png, (input, output) =>
                ["i", "-m", connection.UtilityModel, "-om", input, output], token)
            : await SelectThroughApiAsync(png, ApiKey(upscale: false)!, token);
        if (Math.Abs((double)result.Width / result.Height / ((double)source.Width / source.Height) - 1) > 0.025)
            throw new InvalidDataException("The segmentation model returned a mask with a different aspect ratio.");
        return MaskCoverage(result, token);
    }

    internal static SKBitmap MaskCoverage(SKBitmap image, CancellationToken token)
    {
        var mask = Pixels.NewMask(image.Width, image.Height);
        try
        {
            var plane = mask.GetPixelSpan();
            for (var y = 0; y < image.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (var x = 0; x < image.Width; x++)
                {
                    var pixel = image.GetPixel(x, y);
                    plane[y * mask.RowBytes + x] = (byte)((pixel.Red * 54 + pixel.Green * 183 + pixel.Blue * 19) * pixel.Alpha / (255 * 256));
                }
            }
            Pixels.Invalidate(mask);
            return mask;
        }
        catch { mask.Dispose(); throw; }
    }

    internal static HttpContent CreateSegmentationBody(byte[] png) => JsonContent.Create(new
    {
        image_url = DirectImageApi.DataUrl(png), mask_only = true, sync_mode = true, output_format = "png"
    });

    private static async Task<SKBitmap> SelectThroughApiAsync(byte[] png, string key, CancellationToken token)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = ComfyServerAddress.UseProxy(FalEndpoint) })
            { MaxResponseContentBufferSize = DirectImageApi.MaximumResponseBytes, Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Post, FalEndpoint) { Content = CreateSegmentationBody(png) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Key", key.Trim());
        request.Headers.Add("X-Fal-Store-IO", "0");
        using var response = await client.SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"The segmentation API request failed (HTTP {(int)response.StatusCode}).");
        await using var body = await response.Content.ReadAsStreamAsync(token);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: token);
        if (!json.RootElement.TryGetProperty("image", out var image) || !image.TryGetProperty("url", out var url)
            || url.GetString() is not { } data || !data.StartsWith(DataPrefix, StringComparison.Ordinal))
            throw new InvalidDataException("The segmentation API returned no PNG mask.");
        using var stream = new MemoryStream(Convert.FromBase64String(data[DataPrefix.Length..]));
        return DirectImageGenerator.Decode(stream);
    }

    private static string? FindExecutable(string command, string selectedPath)
    {
        bool IsExecutable(string path) => Path.IsPathFullyQualified(path) && File.Exists(path)
            && (!OperatingSystem.IsWindows() || Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(selectedPath)) return IsExecutable(selectedPath) ? selectedPath : null;
        var file = OperatingSystem.IsWindows() ? command + ".exe" : command;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), file);
            if (IsExecutable(candidate)) return candidate;
        }
        return null;
    }

    private static async Task<SKBitmap> RunCliAsync(string command, string path, byte[] png,
        Func<string, string, string[]> arguments, CancellationToken token)
    {
        var executable = FindExecutable(command, path) ?? throw new InvalidOperationException($"Could not find {command}.");
        var workspace = Directory.CreateTempSubdirectory("composa-image-cli-");
        try
        {
            var input = Path.Combine(workspace.FullName, "input.png");
            var output = Path.Combine(workspace.FullName, "output.png");
            await File.WriteAllBytesAsync(input, png, token);
            var exit = await LocalCodex.RunAsync(executable, Path.GetDirectoryName(executable)!, arguments(input, output), null, token);
            if (exit != 0 || !File.Exists(output))
                throw new InvalidOperationException($"{command} did not produce an image (exit code {exit}). Check the installation and model.");
            using var stream = File.OpenRead(output);
            return DirectImageGenerator.Decode(stream);
        }
        finally
        {
            try { workspace.Delete(recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
