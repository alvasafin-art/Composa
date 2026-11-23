using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SkiaSharp;

namespace Composa.App.AI;

internal sealed class DirectImageApi
{
    private enum Format { OpenAiImages, OpenRouter, Polza }
    private readonly Format format;
    private readonly Uri baseAddress;
    private const string GenerationPath = "/images/generations";
    private const string EditPath = "/images/edits";
    private const string PngPrefix = "data:image/png;base64,";
    internal const int MaximumResponseBytes = 64 * 1024 * 1024;
    internal bool UsesNativeMask => format == Format.OpenAiImages;
    internal Uri CredentialEndpoint => Endpoint(edit: false);

    private DirectImageApi(Uri address, Format format)
    {
        baseAddress = address;
        this.format = format;
    }

    internal static DirectImageApi Parse(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.Scheme == "http" && !uri.IsLoopback || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new FormatException("Enter an HTTPS image API address, or an HTTP loopback address, without credentials or query parameters.");
        var path = uri.AbsolutePath.TrimEnd('/');
        var format = uri.IdnHost.Equals("polza.ai", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/media", StringComparison.OrdinalIgnoreCase)
            ? Format.Polza : uri.IdnHost.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/images", StringComparison.OrdinalIgnoreCase)
                ? Format.OpenRouter : Format.OpenAiImages;
        foreach (var suffix in new[] { GenerationPath, EditPath, "/images", "/media", "/chat/completions" })
        {
            if (!path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            path = path[..^suffix.Length];
            break;
        }
        if (format is Format.Polza or Format.OpenRouter && path is "" or "/api") path = "/api/v1";
        if (format == Format.Polza && path.EndsWith("/v2", StringComparison.OrdinalIgnoreCase)) path = path[..^1] + "1";
        if (uri.IdnHost.Equals("api.selectel.ru", StringComparison.OrdinalIgnoreCase) && path is "" or "/aig") path = "/aig/v1";
        return new(new Uri(uri.GetLeftPart(UriPartial.Authority) + (path.Length == 0 ? "/v1" : path)), format);
    }

    internal Uri Endpoint(bool edit)
    {
        var prefix = baseAddress.AbsoluteUri.TrimEnd('/');
        return format switch
        {
            Format.OpenRouter => new(prefix + "/images"),
            Format.Polza when edit => new(prefix + "/media"),
            Format.Polza => new((prefix.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? prefix[..^1] + "2" : prefix) + GenerationPath),
            _ => new(prefix + (edit ? EditPath : GenerationPath))
        };
    }

    internal static string DataUrl(byte[] image) => PngPrefix + Convert.ToBase64String(image);

    internal static HttpContent CreateUpscaleBody(byte[] png, int factor) => JsonContent.Create(new
    {
        model = "topaz/image-upscale",
        @async = true,
        input = new { prompt = "", upscale_factor = factor.ToString(System.Globalization.CultureInfo.InvariantCulture),
            images = new[] { new { type = "base64", data = DataUrl(png) } } }
    });

    internal HttpContent CreateBody(string model, string prompt, int width, int height, DirectImageFiles? files)
    {
        var size = $"{width}x{height}";
        var aspect = width == height ? "1:1" : width > height ? "3:2" : "2:3";
        if (format == Format.OpenRouter)
        {
            if (files is { Images.Count: > 0 })
                return JsonContent.Create(new { model, prompt, aspect_ratio = aspect, input_references = files.Images.Select(bytes =>
                    new { type = "image_url", image_url = new { url = DataUrl(bytes) } }).ToArray() });
            return JsonContent.Create(new { model, prompt, aspect_ratio = aspect });
        }
        if (format == Format.Polza)
        {
            if (files is { Images.Count: > 0 })
                return JsonContent.Create(new { model, @async = true, input = new { prompt, aspect_ratio = aspect,
                    images = files.Images.Select(bytes => new { type = "base64", data = DataUrl(bytes) }).ToArray() } });
            return JsonContent.Create(new { model, prompt, size, n = 1, response_format = "b64_json" });
        }
        if (files is not { Images.Count: > 0 })
            return JsonContent.Create(new { model, prompt, n = 1, size, quality = "low", output_format = "png" });

        var form = new MultipartFormDataContent();
        form.Add(new StringContent(model), "model");
        form.Add(new StringContent(prompt), "prompt");
        form.Add(new StringContent(size), "size");
        form.Add(new StringContent("png"), "output_format");
        for (var i = 0; i < files.Images.Count; i++)
            AddPng(files.Images[i], "image[]", $"image-{i + 1}.png");
        if (files.Mask != null) AddPng(files.Mask, "mask", "mask.png");
        return form;

        void AddPng(byte[] bytes, string field, string fileName)
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new("image/png");
            form.Add(content, field, fileName);
        }
    }

    internal async Task<SKBitmap> GenerateAsync(string key, string model, string prompt, int width, int height, DirectImageFiles? files, CancellationToken token)
    {
        var edit = files is { Images.Count: > 0 };
        return await GenerateAsync(key, edit, CreateBody(model, prompt, width, height, files), token);
    }

    internal Task<SKBitmap> UpscaleAsync(string key, byte[] png, int factor, CancellationToken token) =>
        GenerateAsync(key, true, CreateUpscaleBody(png, factor), token);

    private async Task<SKBitmap> GenerateAsync(string key, bool edit, HttpContent body, CancellationToken token)
    {
        var endpoint = Endpoint(edit);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = body };
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = ComfyServerAddress.UseProxy(endpoint) })
        {
            MaxResponseContentBufferSize = MaximumResponseBytes,
            Timeout = Timeout.InfiniteTimeSpan
        };
        try
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
            using var json = await SendAsync(client, request, token);
            if (format == Format.Polza && edit)
                return await ReadPolzaMediaAsync(client, json.RootElement, key, token);
            return await ReadImageAsync(client, json.RootElement, token);
        }
        catch (HttpRequestException) { throw new InvalidOperationException("Could not reach the image API or read its response."); }
        catch (JsonException) { throw new InvalidDataException("The image API returned an invalid image response."); }
        catch (FormatException) { throw new InvalidDataException("The image API returned invalid image data."); }
    }

    private static async Task<JsonDocument> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken token)
    {
        using var response = await client.SendAsync(request, token);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("The image API refused the credentials or model access.");
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            throw new InvalidOperationException("The image provider does not offer this endpoint or model at the configured address.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Image API request failed (HTTP {(int)response.StatusCode}).");
        await using var body = await response.Content.ReadAsStreamAsync(token);
        return await JsonDocument.ParseAsync(body, cancellationToken: token);
    }

    private async Task<SKBitmap> ReadPolzaMediaAsync(HttpClient client, JsonElement reply, string key, CancellationToken token)
    {
        if (TryProperty(reply, "data", out var ready) && ready.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            return await ReadImageAsync(client, reply, token);
        if (!TryProperty(reply, "id", out var idNode) || idNode.ValueKind != JsonValueKind.String || idNode.GetString() is not { Length: > 0 and <= 128 } id
            || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new InvalidDataException("The image API returned no generation identifier.");
        var statusUrl = new Uri(Endpoint(edit: true).AbsoluteUri + "/" + id);
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            using var request = new HttpRequestMessage(HttpMethod.Get, statusUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
            using var detail = await SendAsync(client, request, token);
            if (!TryProperty(detail.RootElement, "status", out var state) || state.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("The image API returned no generation status.");
            if (state.GetString() is "pending" or "processing" or "queued" or "running") continue;
            if (state.GetString() is "completed" or "succeeded" or "success")
                return await ReadImageAsync(client, detail.RootElement, token);
            throw new InvalidOperationException("The image provider did not complete the request.");
        }
    }

    private async Task<SKBitmap> ReadImageAsync(HttpClient client, JsonElement response, CancellationToken token)
    {
        if (!TryProperty(response, "data", out var entry)) throw new InvalidDataException("The image API returned no image.");
        if (entry.ValueKind == JsonValueKind.Array)
        {
            if (entry.GetArrayLength() != 1) throw new InvalidDataException("The image API did not return exactly one image.");
            entry = entry[0];
        }
        if (TryProperty(entry, "b64_json", out var encoded) && encoded.ValueKind == JsonValueKind.String && encoded.GetString() is { Length: > 0 } base64)
        {
            using var stream = new MemoryStream(Convert.FromBase64String(base64));
            return DirectImageGenerator.Decode(stream, pngOnly: false);
        }
        if (TryProperty(entry, "url", out var raw) && raw.ValueKind == JsonValueKind.String
            && Uri.TryCreate(raw.GetString(), UriKind.Absolute, out var link) && IsResultAddress(link))
        {
            using var result = await client.GetAsync(link, token);
            if (!result.IsSuccessStatusCode) throw new InvalidDataException("The generated image could not be downloaded.");
            using var stream = new MemoryStream(await result.Content.ReadAsByteArrayAsync(token));
            return DirectImageGenerator.Decode(stream, pngOnly: false);
        }
        throw new InvalidDataException("The image API returned no supported image data.");
    }

    private bool IsResultAddress(Uri uri)
    {
        if (uri.UserInfo.Length > 0 || uri.Fragment.Length > 0) return false;
        var sameOrigin = uri.GetLeftPart(UriPartial.Authority) == baseAddress.GetLeftPart(UriPartial.Authority);
        if (sameOrigin) return true;
        return format == Format.Polza && uri.Scheme == "https" && uri.IsDefaultPort
            && uri.IdnHost is "storage.polza.ai" or "s3.polza.ai" or "cdn.polza.ai";
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value);
    }
}
