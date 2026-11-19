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
        ["direct"] = "Compatible API · this computer"
    };

    internal sealed record ServerTextModel(string Id, string[]? Efforts);

    internal static async Task<ServerTextModel[]> DiscoverServerModels(string url, string adapter, string baseUrl)
    {
        var endpoint = ComfyServerAddress.Parse(url).Api("avs-prompt/test");
        using var client = new HttpClient(new HttpClientHandler { UseProxy = ComfyServerAddress.UseProxy(endpoint), AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
        using var result = await client.PostAsJsonAsync(endpoint, new { adapter, base_url = baseUrl });
        if (result.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("AVS Prompt Helper is unavailable on this ComfyUI server.");
        var data = await result.Content.ReadFromJsonAsync<JsonObject>();
        if (!result.IsSuccessStatusCode) throw new InvalidOperationException((string?)data?["error"] ?? $"Server discovery failed ({(int)result.StatusCode}).");
        return (data?["models"] as JsonArray ?? []).OfType<JsonObject>().Where(row => (string?)row["id"] is { Length: > 0 })
            .Select(row => new ServerTextModel((string)row["id"]!, (row["reasoning"] as JsonArray)?.Select(e => (string)e!).ToArray()))
            .DistinctBy(row => row.Id).ToArray();
    }

    internal IReadOnlyList<string> ChatModels =>
        ServerCapabilities?.NodeDefinitions.GetValueOrDefault("OpenAIChatNode")?["input"]?["required"]?["model"]?[1]?["options"]
            is JsonArray choices ? choices.Select(value => value!.GetValue<string>()).ToArray() : [];

    internal string DefaultInstructions(EngineProfile? engine)
    {
        if (engine != null)
        {
            var file = Path.Combine(Engines.DirectoryOf(engine), "enhancer.txt");
            if (File.Exists(file)) return File.ReadAllText(file).Trim();
        }
        if (NanoBanana.IsModel(engine?.ApiModel))
            return "Write one natural-language prompt for Nano Banana. Identify the base image and role of each referenced image when specified. Describe the desired subject, placement, lighting and interactions. For edits, name the requested change and preserve unrelated details. Keep quoted text exact. Return only the prompt.";
        if (engine?.ApiModel?.StartsWith("gpt-image-", StringComparison.Ordinal) == true)
            return "Write one precise GPT Image prompt. State the intended result first. Describe composition and visual constraints in plain language. For edits, identify the target, requested change and details to preserve. Keep all required visible text verbatim. Return only the prompt.";
        return $"Rewrite the user's request as a concise image prompt for {engine?.DisplayName ?? "the selected model"}. " +
            "Preserve explicit requirements, quoted text and reference order. Describe the intended change, placement and appearance. Return only the final prompt.";
    }

    internal static string InstructionKey(string pack, AiTaskKind task) => pack + (task == AiTaskKind.GenerateImage ? "/generate" : "/edit");

    internal string InstructionsFor(IReadOnlyDictionary<string, string> overrides, EngineProfile? pack, AiTaskKind task)
    {
        var id = pack?.Id ?? "";
        if (overrides.TryGetValue(InstructionKey(id, task), out var saved)) return saved;
        if (task == AiTaskKind.GenerateImage)
            return $"Write a concise prompt to create a new image with {pack?.ModelName ?? pack?.DisplayName}. Describe the requested subject, composition, lighting and style. Preserve quoted text and reference roles. Return only the prompt.";
        return overrides.GetValueOrDefault(id) ?? DefaultInstructions(pack);
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
        using var response = await client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonObject>();
        if (json?["choices"] is JsonArray { Count: > 0 } choices && choices[0]?["message"]?["content"]?.GetValue<string>()?.Trim() is { Length: > 0 } text)
            return text;
        throw new InvalidDataException("The chat API returned an empty prompt.");
    }

    internal async Task<string> EnhanceAsync(Settings settings, EngineProfile engine, AiTaskKind task, string draft, CancellationToken token = default, string? directKey = null)
    {
        var instructions = InstructionsFor(settings.EnhancerInstructions, engine, task);
        var message = $"Target model: {engine.DisplayName}\nOperation: {task.DisplayName()}\nUser request: {draft}";
        var provider = settings.EffectiveEnhancerProvider();
        if (provider == "direct")
            return await EnhanceCompatibleAsync(settings, instructions, message, token, directKey);
        using var client = Client();
        if (ConnectedServerUrl != client.Address.ToString() || ServerCapabilities == null)
            await TestConnectionAsync(token, overrideUrl: client.Address.ToString());
        if (provider != "comfy")
        {
            if (!Providers.ContainsKey(provider)) throw new InvalidOperationException("Choose a prompt provider in ComfyUI Settings.");
            var model = settings.EnhancerServerModels.GetValueOrDefault(provider);
            if (string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException("Choose the server model in AI Settings → Prompt enhancer.");
            var state = new { version = 1, prompt = message, system_prompt = instructions,
                settings = new { adapter = provider, model, base_url = provider == "compatible" ? settings.EnhancerServerApiUrl : "",
                    reasoning = settings.EnhancerEffort == "default" ? "auto" : settings.EnhancerEffort,
                    api_mode = settings.EnhancerServerApiMode, max_output_tokens = Math.Clamp(settings.EnhancerMaxTokens, 16, 16384), timeout = 300 } };
            var helper = new JsonObject { ["class_type"] = "AVSPromptHelper",
                ["inputs"] = new JsonObject { ["state"] = JsonSerializer.Serialize(state) } };
            return await ExecuteEnhancerGraph(client, new JsonObject { ["chat"] = helper }, token);
        }
        if (!ChatModels.Contains(settings.EnhancerModel))
            throw new InvalidOperationException($"Select a chat model offered by this server in AI Settings → Prompt enhancer. Current choice: {settings.EnhancerModel}.");
        if (string.IsNullOrWhiteSpace(Credential) && RequestApiKey != null)
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
        return await ExecuteEnhancerGraph(client, graph, token);
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
