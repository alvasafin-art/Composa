using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Buffers.Binary;
using Composa.AI;
using Composa.IO;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.AI;

/// <summary>HTTP and WebSocket transport for any ComfyUI server URL; it never starts or owns the server process.</summary>
public sealed class ComfyClient : IComfyConnection
{
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private Func<string?> apiKey;
    internal Func<string?> Credential { set => apiKey = value; }
    public ComfyServerAddress Address { get; }
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public ComfyClient(string serverUrl, HttpClient? httpClient = null, Func<string?>? apiKey = null)
    {
        Address = ComfyServerAddress.Parse(serverUrl);
        http = httpClient ?? new HttpClient();
        ownsHttp = httpClient == null;
        this.apiKey = apiKey ?? (() => null);
    }

    public async Task<(ComfyServerInfo Info, ComfyServerCapabilities Capabilities)> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectionTimeout);
        using var stats = await GetJson("system_stats", timeout.Token);
        using var objects = await GetJson("object_info", timeout.Token);
        var info = ParseInfo(stats.RootElement);
        return (info, ParseCapabilities(objects.RootElement) with { Version = info.Version });
    }

    public async Task<ComfyQueueState> QueueAsync(string? promptId = null, CancellationToken cancellationToken = default)
    {
        using var queue = await GetJson("queue", cancellationToken);
        var root = queue.RootElement;
        var running = root.TryGetProperty("queue_running", out var r) && r.ValueKind == JsonValueKind.Array ? r.GetArrayLength() : 0;
        var pending = root.TryGetProperty("queue_pending", out var p) && p.ValueKind == JsonValueKind.Array ? p.GetArrayLength() : 0;
        int? position = null;
        if (promptId != null && p.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in p.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() > 1 && item[1].GetString() == promptId) { position = index + 1; break; }
                index++;
            }
        }
        return new ComfyQueueState(running, pending, position);
    }

    public async Task<ComfyServerInfo?> MemoryInfoAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(ConnectionTimeout);
        using var stats = await GetJson("system_stats", timeout.Token); return ParseInfo(stats.RootElement);
    }

    public async Task<string> UploadPngAsync(string semanticName, SKBitmap bitmap, CancellationToken cancellationToken = default)
    {
        var filename = $"composa-{semanticName}-{Guid.NewGuid():N}.png";
        var bytes = EncodeUploadPng(bitmap);
        using var form = new MultipartFormDataContent();
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new("image/png");
        form.Add(content, "image", filename);
        form.Add(new StringContent("input"), "type");
        using var response = await http.PostAsync(Address.Api("upload/image"), form, cancellationToken);
        await EnsureSuccess(response, cancellationToken);
        return filename;
    }

    /// <summary>Comfy image-mask workflows read RGB channels; Alpha8 must therefore be uploaded as opaque grayscale.</summary>
    internal static byte[] EncodeUploadPng(SKBitmap bitmap)
    {
        if (bitmap.ColorType != SKColorType.Alpha8) return ImageFiles.Encode(bitmap, ExportFormat.Png);
        using var gray = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Gray8, SKAlphaType.Opaque));
        var source = bitmap.GetPixelSpan();
        var target = gray.GetPixelSpan();
        for (var y = 0; y < bitmap.Height; y++)
            source.Slice(y * bitmap.RowBytes, bitmap.Width).CopyTo(target.Slice(y * gray.RowBytes, bitmap.Width));
        return ImageFiles.Encode(gray, ExportFormat.Png);
    }

    public async Task<string> SubmitAsync(JsonObject workflow, Guid clientId, CancellationToken cancellationToken = default)
    {
        var payload = new JsonObject { ["prompt"] = workflow, ["client_id"] = clientId.ToString("N") };
        if (workflow.Any(node => node.Value?["class_type"]?.GetValue<string>() == "OpenAIGPTImageNodeV2"))
        {
            var key = apiKey()?.Trim();
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Add a Comfy.org API key in AI → ComfyUI Settings. Browser login alone does not authorize external requests.");
            payload["extra_data"] = new JsonObject { ["api_key_comfy_org"] = key };
        }
        using var response = await http.PostAsJsonAsync(Address.Api("prompt"), payload, cancellationToken);
        await EnsureSuccess(response, cancellationToken);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        if (!result.RootElement.TryGetProperty("prompt_id", out var id) || string.IsNullOrWhiteSpace(id.GetString()))
            throw new InvalidDataException("ComfyUI accepted the request without returning a prompt id.");
        return id.GetString()!;
    }

    public async Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync(Address.Api("interrupt"), new StringContent("{}", Encoding.UTF8, "application/json"), cancellationToken);
        await EnsureSuccess(response, cancellationToken);
    }

    public async Task<ComfyExecutionResult> ExecuteAsync(JsonObject workflow, IProgress<AiOperationState>? progress = null, CancellationToken cancellationToken = default)
    {
        var clientId = Guid.NewGuid();
        using var socket = new ClientWebSocket();
        using var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connecting.CancelAfter(ConnectionTimeout);
        await socket.ConnectAsync(Address.Socket(clientId), connecting.Token);
        var promptId = await SubmitAsync(workflow, clientId, cancellationToken);
        var queue = await QueueAsync(promptId, cancellationToken);
        var state = new AiOperationState { Status = AiOperationStatus.Queued, PromptId = promptId, QueuePosition = queue.Position, Stage = queue.Position is { } p ? $"Queued ({p})" : "Queued" };
        progress?.Report(state);
        try
        {
            var buffer = new byte[64 * 1024];
            var activePrompt = false;
            while (state.Status is not (AiOperationStatus.Completed or AiOperationStatus.Failed or AiOperationStatus.Cancelled))
            {
                using var message = new MemoryStream();
                var discardBinary = false;
                WebSocketReceiveResult part;
                do
                {
                    part = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (part.MessageType == WebSocketMessageType.Close) throw new IOException("ComfyUI closed the progress connection before completion.");
                    if (part.MessageType == WebSocketMessageType.Text) message.Write(buffer, 0, part.Count);
                    else if (!discardBinary)
                    {
                        // Do not retain large preview images just to read the small TEXT billing/progress event.
                        if (message.Length + part.Count > 8192) discardBinary = true;
                        else
                        {
                            message.Write(buffer, 0, part.Count);
                            if (message.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(message.GetBuffer().AsSpan(0, 4)) != 3) discardBinary = true;
                        }
                        if (discardBinary) message.SetLength(0);
                    }
                } while (!part.EndOfMessage);
                if (part.MessageType == WebSocketMessageType.Binary)
                {
                    if (!activePrompt || discardBinary) continue;
                    state = ComfyEventParser.ParseBinary(message.ToArray(), state);
                }
                else
                {
                    var json = Encoding.UTF8.GetString(message.ToArray());
                    using var eventData = JsonDocument.Parse(json);
                    if (eventData.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("prompt_id", out var eventId)
                        && eventData.RootElement.TryGetProperty("type", out var eventType) && eventType.GetString() is "execution_start" or "executing")
                        activePrompt = eventId.GetString() == promptId;
                    state = ComfyEventParser.Parse(json, promptId, state);
                }
                progress?.Report(state);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { await InterruptAsync(CancellationToken.None); } catch { }
            progress?.Report(state with { Status = AiOperationStatus.Cancelled, Stage = "Cancelled" });
            throw;
        }
        if (state.Status == AiOperationStatus.Failed) throw new InvalidOperationException(state.Error ?? "ComfyUI execution failed.");
        if (state.Status == AiOperationStatus.Cancelled) throw new OperationCanceledException("ComfyUI execution was cancelled.");
        // The success WebSocket event can beat persistence of the history record, especially for a fully cached
        // prompt. Wait briefly for its outputs instead of reporting a false "completed without an image" error.
        JsonDocument? history = null;
        var attempts = Math.Max(2, (int)Math.Ceiling(ConnectionTimeout.TotalMilliseconds / 100));
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            history?.Dispose();
            history = await HistoryAsync(promptId, cancellationToken);
            var images = FindImages(history.RootElement, promptId);
            if (images.Count > 0 || attempt == attempts - 1) return new ComfyExecutionResult(promptId, history, images) { CreditsUsed = state.CreditsUsed };
            await Task.Delay(100, cancellationToken);
        }
        throw new InvalidOperationException("ComfyUI history polling ended unexpectedly.");
    }

    public Task<JsonDocument> HistoryAsync(string promptId, CancellationToken cancellationToken = default) => GetJson("history/" + Uri.EscapeDataString(promptId), cancellationToken);

    public async Task<SKBitmap> DownloadAsync(ComfyImageReference image, CancellationToken cancellationToken = default)
    {
        var query = $"view?filename={Uri.EscapeDataString(image.Filename)}&subfolder={Uri.EscapeDataString(image.Subfolder)}&type={Uri.EscapeDataString(image.Type)}";
        using var response = await http.GetAsync(Address.Api(query), cancellationToken);
        await EnsureSuccess(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return ImageFiles.Load(stream, image.Filename);
    }

    private async Task<JsonDocument> GetJson(string path, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(Address.Api(path), cancellationToken);
        await EnsureSuccess(response, cancellationToken);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }

    private async Task EnsureSuccess(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (apiKey() is { Length: > 0 } credential) body = body.Replace(credential, "[redacted]", StringComparison.Ordinal);
        throw new HttpRequestException($"ComfyUI returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}", null, response.StatusCode);
    }

    internal static ComfyServerInfo ParseInfo(JsonElement root)
    {
        var system = root.TryGetProperty("system", out var value) ? value : default;
        string? Field(string name) => system.ValueKind == JsonValueKind.Object && system.TryGetProperty(name, out var field) ? field.GetString() : null;
        var devices = new List<string>();
        var memory = new List<ComfyDeviceMemory>();
        if (root.TryGetProperty("devices", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var device in list.EnumerateArray())
            {
                var name = device.TryGetProperty("name", out var n) ? n.GetString() ?? "Device" : "Device"; devices.Add(name);
                long Number(string key) => device.TryGetProperty(key, out var f) && f.TryGetInt64(out var v) ? Math.Max(0, v) : 0;
                memory.Add(new(name, device.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "", (int)Number("index"), Number("vram_total"), Number("vram_free")));
            }
        return new(Field("comfyui_version"), Field("os"), Field("python_version"), devices) { Memory = memory };
    }

    internal static ComfyServerCapabilities ParseCapabilities(JsonElement root)
    {
        var nodes = new HashSet<string>(StringComparer.Ordinal);
        var assets = new Dictionary<EngineAssetKind, HashSet<string>>();
        var modelChoices = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var definitions = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object) return new() { NodeTypes = nodes, Assets = assets };
        foreach (var node in root.EnumerateObject())
        {
            nodes.Add(node.Name);
            if ((node.Name == "OpenAIGPTImageNodeV2" || node.Name.StartsWith("SeedVR2", StringComparison.Ordinal))
                && JsonNode.Parse(node.Value.GetRawText()) is JsonObject definition) definitions[node.Name] = definition;
            if (!node.Value.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object) continue;
            foreach (var sectionName in new[] { "required", "optional" })
            {
                if (!input.TryGetProperty(sectionName, out var section) || section.ValueKind != JsonValueKind.Object) continue;
                foreach (var entry in section.EnumerateObject())
                {
                    if (!AssetKind(entry.Name, node.Name, out var kind) || entry.Value.ValueKind != JsonValueKind.Array || entry.Value.GetArrayLength() == 0) continue;
                    var choices = entry.Value[0];
                    // ComfyUI historically returned choices directly as the first array item. Current releases
                    // describe a COMBO there and put its choices in the second item's options array.
                    if (choices.ValueKind != JsonValueKind.Array && entry.Value.GetArrayLength() > 1
                        && entry.Value[1].ValueKind == JsonValueKind.Object
                        && entry.Value[1].TryGetProperty("options", out var options)) choices = options;
                    if (choices.ValueKind != JsonValueKind.Array) continue;
                    if (!assets.TryGetValue(kind, out var names)) assets[kind] = names = new(StringComparer.OrdinalIgnoreCase);
                    var loaderNames = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var choice in choices.EnumerateArray()) if (choice.ValueKind == JsonValueKind.String && choice.GetString() is { } name)
                    { names.Add(name); loaderNames.Add(name); }
                    modelChoices[node.Name + "." + entry.Name] = loaderNames;
                }
            }
        }
        return new() { NodeTypes = nodes, Assets = assets, ModelChoices = modelChoices, NodeDefinitions = definitions };
    }

    internal static bool AssetKind(string input, string node, out EngineAssetKind kind)
    {
        var key = input.ToLowerInvariant();
        if (input == "model" && node == "SeedVR2LoadDiTModel") kind = EngineAssetKind.DiffusionModel;
        else if (input == "model" && node == "SeedVR2LoadVAEModel") kind = EngineAssetKind.Vae;
        else if (key.Contains("bg_removal") || key.Contains("background_removal")) kind = EngineAssetKind.BackgroundRemoval;
        else if (key.Contains("lora")) kind = EngineAssetKind.Lora;
        else if (key.Contains("vae")) kind = EngineAssetKind.Vae;
        else if (key.Contains("clip") || key.Contains("text_encoder")) kind = EngineAssetKind.TextEncoder;
        else if (key.Contains("ckpt") || key.Contains("checkpoint")) kind = EngineAssetKind.Checkpoint;
        else if (key.Contains("unet") || key.Contains("diffusion")) kind = EngineAssetKind.DiffusionModel;
        else if (node.Contains("Upscale", StringComparison.OrdinalIgnoreCase) && key.Contains("model")) kind = EngineAssetKind.Upscaler;
        else { kind = default; return false; }
        return true;
    }

    internal static IReadOnlyList<ComfyImageReference> FindImages(JsonElement root, string promptId)
    {
        if (root.TryGetProperty(promptId, out var prompt)) root = prompt;
        if (!root.TryGetProperty("outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Object) return [];
        var result = new List<ComfyImageReference>();
        foreach (var node in outputs.EnumerateObject())
        {
            if (!node.Value.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) continue;
            foreach (var image in images.EnumerateArray())
            {
                var filename = image.TryGetProperty("filename", out var f) ? f.GetString() : null;
                if (filename == null) continue;
                result.Add(new(filename,
                    image.TryGetProperty("subfolder", out var s) ? s.GetString() ?? "" : "",
                    image.TryGetProperty("type", out var t) ? t.GetString() ?? "output" : "output",
                    node.Name));
            }
        }
        return result;
    }

    public void Dispose() { if (ownsHttp) http.Dispose(); }
}
