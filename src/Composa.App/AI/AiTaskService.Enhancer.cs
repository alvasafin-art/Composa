using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Composa.AI;

namespace Composa.App.AI;

public sealed partial class AiTaskService
{
    internal static readonly Dictionary<string, string> Providers = new()
    {
        ["comfy"] = "ChatGPT · Comfy credits",
        ["codex"] = "Codex CLI · ComfyUI server",
        ["claude"] = "Claude Code CLI · ComfyUI server",
        ["kimi"] = "Kimi CLI · ComfyUI server",
        ["compatible"] = "Compatible API · ComfyUI server",
        ["openai"] = "OpenAI API · ComfyUI server",
        ["direct"] = "Compatible API · this computer",
        ["local-codex"] = "Codex CLI · this computer"
    };

    internal static bool IsRemoteCli(string provider) => provider is "codex" or "claude" or "kimi";

    internal sealed record ServerTextModel(string Id, string[]? Efforts);

    internal static async Task<ServerTextModel[]> DiscoverServerModels(string url, string adapter, string baseUrl, CancellationToken token = default)
    {
        var endpoint = ComfyServerAddress.Parse(url).Api("avs-prompt/test");
        using var client = new HttpClient(new HttpClientHandler { UseProxy = ComfyServerAddress.UseProxy(endpoint), AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
        using var result = await client.PostAsJsonAsync(endpoint, new { adapter, base_url = baseUrl }, token);
        if (result.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("AVS Prompt Helper is unavailable on this ComfyUI server.");
        if (!result.IsSuccessStatusCode)
            throw new InvalidOperationException(IsRemoteCli(adapter) ? "The CLI was not found or is not signed in on the ComfyUI server."
                : "The text provider could not discover models on the ComfyUI server.");
        var data = await result.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: token);
        return (data?["models"] as JsonArray ?? []).OfType<JsonObject>().Where(row => (string?)row["id"] is { Length: > 0 })
            .Select(row => new ServerTextModel((string)row["id"]!, (row["reasoning"] as JsonArray)?.Select(e => (string)e!).ToArray()))
            .DistinctBy(row => row.Id).ToArray();
    }

    internal IReadOnlyList<string> ChatModels =>
        ServerCapabilities?.NodeDefinitions.GetValueOrDefault("OpenAIChatNode")?["input"]?["required"]?["model"]?[1]?["options"]
            is JsonArray choices ? choices.Select(value => value!.GetValue<string>()).ToArray() : [];

    internal string[] ChatReasoningEfforts
    {
        get
        {
            var config = ServerCapabilities?.NodeDefinitions.GetValueOrDefault("OpenAIChatConfig")?["input"];
            var effortInput = config?["optional"]?["reasoning_effort"] ?? config?["required"]?["reasoning_effort"];
            if (effortInput is not JsonArray { Count: > 0 } descriptor) return [];
            var choices = descriptor[0] as JsonArray ??
                (descriptor.Count > 1 && descriptor[1] is JsonObject properties ? properties["options"] as JsonArray : null);
            return choices?.OfType<JsonValue>().Select(item => item.TryGetValue<string>(out var value) ? value : null)
                .OfType<string>().Where(value => value is not ("auto" or "default" or "")).Distinct().ToArray() ?? [];
        }
    }

    private static string GuidanceId(EngineProfile engine)
    {
        if (engine.Id == "server-codex-image") return "direct-codex-image";
        if (engine.Id != "direct-api-image" || engine.ApiModel is not { } apiModel) return engine.Id;

        // The API catalog can qualify a model with its provider (for example openai/gpt-image-2).
        var model = apiModel[(apiModel.LastIndexOf('/') + 1)..].ToLowerInvariant();
        return model switch
        {
            "gpt-image-2" => "direct-gpt-image-2",
            "gpt-image-2.5-sunburst" => "chatgpt-image-2.5",
            "gpt-image-2.6-sunburst" => "chatgpt-image-2.6",
            _ when model.StartsWith("gpt-image-2.5-sunburst-", StringComparison.Ordinal) => "chatgpt-image-2.5",
            _ when model.StartsWith("gpt-image-2.6-sunburst-", StringComparison.Ordinal) => "chatgpt-image-2.6",
            "gemini-3.1-flash-image" or "gemini-3.1-flash-image-preview" => "nano-banana-2",
            "doubao-seedream-5-5-261028" or "seedream-5.5" => "seedream-5.5",
            _ => engine.Id
        };
    }

    private string DefaultInstructions(EngineProfile? engine, AiTaskKind task)
    {
        if (engine != null)
        {
            var filename = task == AiTaskKind.GenerateImage ? "enhancer-generate.txt" : "enhancer.txt";
            var file = Path.Combine(Engines.Root, GuidanceId(engine), filename);
            if (File.Exists(file)) return File.ReadAllText(file).Trim();
        }

        var name = engine?.ModelName ?? engine?.DisplayName ?? "the selected model";
        if (task == AiTaskKind.GenerateImage)
            return $"Write a concise prompt to create a new image with {name}. Describe the requested subject, composition, lighting and style. Preserve quoted text and reference roles. Return only the prompt.";
        return $"Rewrite the user's request as a concise image-editing prompt for {name}. " +
            "Preserve explicit requirements, quoted text and reference order. Describe the intended change, placement and appearance. Return only the final prompt.";
    }

    internal static string InstructionKey(string pack, AiTaskKind task) => pack + (task switch
    {
        AiTaskKind.GenerateImage => "/generate",
        _ => "/" + task
    });

    internal static string InstructionScope(EngineProfile? pack)
    {
        var id = pack?.Id ?? "";
        if (id != "direct-api-image" || string.IsNullOrWhiteSpace(pack?.ApiModel)) return id;
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(pack.ApiModel));
        return id + "-" + Convert.ToHexString(hash);
    }

    internal string InstructionsFor(IReadOnlyDictionary<string, string> overrides, EngineProfile? pack, AiTaskKind task)
    {
        var id = pack?.Id ?? "";
        var scope = InstructionScope(pack);
        if (overrides.TryGetValue(InstructionKey(scope, task), out var saved)) return saved;
        if (scope != id && overrides.TryGetValue(InstructionKey(id, task), out saved)) return saved;
        if (task == AiTaskKind.GenerateImage) return DefaultInstructions(pack, task);
        return overrides.GetValueOrDefault(InstructionKey(scope, AiTaskKind.ImageEdit))
            ?? overrides.GetValueOrDefault(id + "/edit") ?? overrides.GetValueOrDefault(id) ?? DefaultInstructions(pack, task);
    }

    internal static string? EnhancedText(JsonElement root, string id)
    {
        if (root.TryGetProperty(id, out var job)) root = job;
        if (root.TryGetProperty("outputs", out var outputs) && outputs.TryGetProperty("enhanced_prompt", out var preview)
            && preview.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.Array
            && text.GetArrayLength() > 0 && text[0].ValueKind == JsonValueKind.String)
            return text[0].GetString()?.Trim();
        return null;
    }

    private static async Task<string> EnhanceCompatibleAsync(Settings settings, string instructions, string message, CancellationToken token, string? draftKey)
    {
        var endpoint = Assistant.ChatCompletionAssistantProvider.Endpoint(settings.EnhancerApiUrl);
        if (string.IsNullOrWhiteSpace(settings.EnhancerApiModel)) throw new InvalidOperationException("Enter the API model ID in AI Settings → Prompt enhancer.");
        using var client = new HttpClient(new HttpClientHandler { UseProxy = !endpoint.IsLoopback, AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromMinutes(5) };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if ((draftKey ?? (Settings.Persist ? UserApiKeys.Read(UserApiKeys.Target("Direct", endpoint)) : null) ?? Environment.GetEnvironmentVariable("AVS_COMPATIBLE_API_KEY")) is { Length: > 0 } key)
            request.Headers.Authorization = new("Bearer", key);
        var payload = new Dictionary<string, object> { ["model"] = settings.EnhancerApiModel.Trim(),
            ["max_tokens"] = Math.Clamp(settings.EnhancerMaxTokens, 16, 16384),
            ["messages"] = new[] { new { role = "system", content = instructions }, new { role = "user", content = message } } };
        if (settings.EnhancerEffort != "default") payload["reasoning_effort"] = settings.EnhancerEffort;
        request.Content = JsonContent.Create(payload);
        try
        {
            using var response = await client.SendAsync(request, token);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                throw new InvalidOperationException("The API refused authorization. Check the key.");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Text API request failed (HTTP {(int)response.StatusCode}).");
            var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: token);
            if (json?["choices"] is JsonArray { Count: > 0 } choices && choices[0]?["message"]?["content"]?.GetValue<string>()?.Trim() is { Length: > 0 } text)
                return text;
        }
        catch (HttpRequestException) { throw new InvalidOperationException("Could not reach the text API."); }
        catch (JsonException) { throw new InvalidDataException("The text API returned an invalid response."); }
        throw new InvalidDataException("The chat API returned an empty prompt.");
    }

    internal async Task<string> TestEnhancerAsync(Settings settings, CancellationToken token, string? directKey,
        EngineProfile? engine = null, AiTaskKind task = AiTaskKind.GenerateImage, string? input = null,
        Action? serverResponded = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var provider = settings.EffectiveEnhancerProvider();
        try
        {
            if (provider is not ("direct" or "local-codex"))
            {
                try { await TestConnectionAsync(deadline.Token); }
                catch (Exception error) when (IsAuthorizationError(error))
                {
                    throw new InvalidOperationException("ComfyUI refused authorization.", error);
                }
                catch (Exception error) when (!token.IsCancellationRequested &&
                    error is InvalidOperationException or HttpRequestException or JsonException or OperationCanceledException)
                {
                    throw new InvalidOperationException("The ComfyUI server is unavailable.");
                }
                token.ThrowIfCancellationRequested();
                serverResponded?.Invoke();

                if (provider != "comfy")
                {
                    if (ServerCapabilities?.NodeTypes.Contains("AVSPromptHelper") != true)
                        throw new PromptHelperMissingException();
                }
            }

            var prompt = string.IsNullOrWhiteSpace(input) ? "a cobalt-blue ceramic cup on a pale gray background" : input.Trim();
            var instructions = engine == null ? "Improve the image prompt in one short sentence. Return only the improved prompt."
                : InstructionsFor(settings.EnhancerInstructions, engine, task);
            var message = engine == null ? prompt : EnhancerMessage(engine, task, prompt, referenceCount: null);
            return await SendEnhancerAsync(settings, instructions, message, deadline.Token, directKey, promptForComfyKey: false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException("The text model did not respond before the test timed out.");
        }
    }

    internal static bool IsAuthorizationError(Exception error) =>
        error is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden } ||
        (error.InnerException != null && IsAuthorizationError(error.InnerException));

    internal sealed class PromptHelperMissingException() : InvalidOperationException("AVS Prompt Helper is unavailable on this ComfyUI server.");

    internal Task<string> EnhanceAsync(Settings settings, EngineProfile engine, AiTaskKind task, string draft,
        CancellationToken token = default, string? directKey = null, int? referenceCount = null)
    {
        var instructions = InstructionsFor(settings.EnhancerInstructions, engine, task);
        var message = EnhancerMessage(engine, task, draft, referenceCount);
        return SendEnhancerAsync(settings, instructions, message, token, directKey, promptForComfyKey: true);
    }

    private static string EnhancerMessage(EngineProfile engine, AiTaskKind task, string draft, int? referenceCount)
    {
        var references = referenceCount is int count ? $"\nAdded reference images: {count}" : "";
        return $"Target model: {engine.DisplayName}\nOperation: {task.DisplayName()}{references}\n" +
            $"Only the request text is available to the prompt enhancer. Use image roles stated in it or specified by the workflow and avoid inventing details about input images.\nUser request: {draft}";
    }

    private async Task<string> SendEnhancerAsync(Settings settings, string instructions, string message, CancellationToken token,
        string? directKey, bool promptForComfyKey)
    {
        var provider = settings.EffectiveEnhancerProvider();
        if (provider == "direct")
            return await EnhanceCompatibleAsync(settings, instructions, message, token, directKey);
        if (provider == "local-codex")
            return await LocalCodex.EnhanceAsync(settings.EnhancerCodexPath, settings.EnhancerCodexModel, instructions, message, token, settings.EnhancerEffort);
        using var client = Client();
        if (ConnectedServerUrl != client.Address.ToString() || ServerCapabilities == null)
            await TestConnectionAsync(token, overrideUrl: client.Address.ToString());
        if (provider != "comfy")
        {
            if (ServerCapabilities?.NodeTypes.Contains("AVSPromptHelper") != true) throw new PromptHelperMissingException();
            if (!Providers.ContainsKey(provider)) throw new InvalidOperationException("Choose a prompt provider in ComfyUI Settings.");
            var model = settings.EnhancerServerModels.GetValueOrDefault(provider);
            if (string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException("Choose the server model in AI Settings → Prompt enhancer.");
            var state = new { version = 1, prompt = message, system_prompt = instructions,
                settings = new { adapter = provider, model, base_url = provider == "compatible" ? settings.EnhancerServerApiUrl : "",
                    reasoning = settings.EnhancerEffort == "default" ? "auto" : settings.EnhancerEffort,
                    api_mode = settings.EnhancerServerApiMode, max_output_tokens = Math.Clamp(settings.EnhancerMaxTokens, 16, 16384), timeout = 300 } };
            var helper = new JsonObject { ["class_type"] = "AVSPromptHelper",
                ["inputs"] = new JsonObject { ["state"] = JsonSerializer.Serialize(state) } };
            try { return await ExecuteEnhancerGraph(client, new JsonObject { ["chat"] = helper }, token); }
            catch (Exception error) when (IsAuthorizationError(error))
            {
                throw new InvalidOperationException("The server refused authorization for the text request.", error);
            }
            catch (Exception error) when (error is HttpRequestException or InvalidDataException or InvalidOperationException or JsonException)
            {
                if (ServerCapabilities?.NodeTypes.Contains("AVSPromptHelper") != true)
                    throw new InvalidOperationException("AVS Prompt Helper is unavailable on this ComfyUI server.");
                throw new InvalidOperationException("The text model did not respond on the ComfyUI server. Check the model and server-side authorization.");
            }
        }
        if (!ChatModels.Contains(settings.EnhancerModel))
            throw new InvalidOperationException($"Select a chat model offered by this server in AI Settings → Prompt enhancer. Current choice: {settings.EnhancerModel}.");
        if (string.IsNullOrWhiteSpace(Credential) && promptForComfyKey && RequestApiKey != null)
        {
            SessionApiKey = (await RequestApiKey(token))?.Trim();
            if (string.IsNullOrWhiteSpace(SessionApiKey)) throw new OperationCanceledException();
        }
        if (string.IsNullOrWhiteSpace(Credential)) throw new InvalidOperationException("Add your Comfy.org key in AI Settings → Connection → Comfy credits.");
        var config = new JsonObject
        {
            ["instructions"] = instructions,
            ["max_output_tokens"] = Math.Clamp(settings.EnhancerMaxTokens, 16, 16384),
            ["truncation"] = "auto"
        };
        if (settings.EnhancerEffort != "default") config["reasoning_effort"] = settings.EnhancerEffort;
        var graph = new JsonObject
        {
            ["config"] = new JsonObject { ["class_type"] = "OpenAIChatConfig", ["inputs"] = config },
            ["chat"] = new JsonObject { ["class_type"] = "OpenAIChatNode", ["inputs"] = new JsonObject
                { ["model"] = settings.EnhancerModel, ["persist_context"] = false, ["advanced_options"] = new JsonArray("config", 0),
                  ["prompt"] = message } },
        };
        try { return await ExecuteEnhancerGraph(client, graph, token); }
        catch (Exception error) when (IsAuthorizationError(error))
        {
            throw new InvalidOperationException("Comfy credits refused authorization. Check the Comfy.org key.", error);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or InvalidOperationException or JsonException)
        {
            throw new InvalidOperationException("The model did not respond through Comfy credits. Check the server model, key and credit balance.");
        }
    }

    private async Task<string> ExecuteEnhancerGraph(IComfyConnection client, JsonObject graph, CancellationToken token)
    {
        graph["enhanced_prompt"] = new JsonObject { ["class_type"] = "PreviewAny", ["inputs"] = new JsonObject { ["source"] = new JsonArray("chat", 0) } };
        var compatibility = EngineCompatibility.CheckWorkflow(graph, ServerCapabilities!);
        if (!compatibility.IsCompatible) throw new InvalidOperationException(string.Join(", ", compatibility.Missing));
        var result = client is ComfyClient transport
            ? await transport.ExecuteAsync(graph, null, token, interruptOnCancel: false)
            : await client.ExecuteAsync(graph, cancellationToken: token);
        using (result.History)
            return EnhancedText(result.History.RootElement, result.PromptId) is { Length: > 0 } text ? text
                : throw new InvalidDataException("ComfyUI returned an empty prompt.");
    }
}
