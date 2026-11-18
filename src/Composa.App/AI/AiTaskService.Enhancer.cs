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

    internal static async Task<string[]> DiscoverServerModels(string url, string adapter, string baseUrl)
    {
        var endpoint = ComfyServerAddress.Parse(url).Api("avs-prompt/test");
        using var client = new HttpClient(new HttpClientHandler { UseProxy = !endpoint.IsLoopback, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
        using var result = await client.PostAsJsonAsync(endpoint, new { adapter, base_url = baseUrl });
        if (result.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("AVS Prompt Helper is unavailable on this ComfyUI server.");
        var data = await result.Content.ReadFromJsonAsync<JsonObject>();
        if (!result.IsSuccessStatusCode) throw new InvalidOperationException((string?)data?["error"] ?? $"Server discovery failed ({(int)result.StatusCode}).");
        return (data?["models"] as JsonArray ?? []).Select(value => (string?)value?["id"]).OfType<string>().Where(id => id.Length > 0).Distinct().ToArray();
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

    internal static string? EnhancedText(JsonElement root, string id)
    {
        if (root.TryGetProperty(id, out var job)) root = job;
        if (root.TryGetProperty("outputs", out var outputs) && outputs.TryGetProperty("enhanced_prompt", out var preview)
            && preview.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.Array
            && text.GetArrayLength() > 0 && text[0].ValueKind == JsonValueKind.String)
            return text[0].GetString()?.Trim();
        return null;
    }

    private static async Task<string> EnhanceCompatibleAsync(Settings settings, string instructions, string message)
    {
        var endpoint = Assistant.ChatCompletionAssistantProvider.Endpoint(settings.EnhancerApiUrl);
        if (string.IsNullOrWhiteSpace(settings.EnhancerApiModel)) throw new InvalidOperationException("Enter the API model ID in Prompts.");
        using var client = new HttpClient(new HttpClientHandler { UseProxy = !endpoint.IsLoopback, AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromMinutes(5) };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (((Settings.Persist ? UserApiKeys.Read(UserApiKeys.Target("Direct", endpoint)) : null) ?? Environment.GetEnvironmentVariable("AVS_COMPATIBLE_API_KEY")) is { Length: > 0 } key)
            request.Headers.Authorization = new("Bearer", key);
        request.Content = JsonContent.Create(new { model = settings.EnhancerApiModel.Trim(), max_tokens = Math.Clamp(settings.EnhancerMaxTokens, 16, 16384),
            messages = new[] { new { role = "system", content = instructions }, new { role = "user", content = message } } });
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonObject>();
        if (json?["choices"] is JsonArray { Count: > 0 } choices && choices[0]?["message"]?["content"]?.GetValue<string>()?.Trim() is { Length: > 0 } text)
            return text;
        throw new InvalidDataException("The chat API returned an empty prompt.");
    }

    internal async Task<string> EnhanceAsync(Settings settings, EngineProfile engine, AiTaskKind task, string draft)
    {
        var instructions = settings.EnhancerInstructions.GetValueOrDefault(engine.Id) ?? DefaultInstructions(engine);
        var message = $"Target model: {engine.DisplayName}\nOperation: {task.DisplayName()}\nUser request: {draft}";
        var provider = settings.EffectiveEnhancerProvider();
        if (provider == "direct")
            return await EnhanceCompatibleAsync(settings, instructions, message);
        using var client = Client();
        if (ConnectedServerUrl != client.Address.ToString() || ServerCapabilities == null)
            await TestConnectionAsync(overrideUrl: client.Address.ToString());
        if (provider != "comfy")
        {
            if (!Providers.ContainsKey(provider)) throw new InvalidOperationException("Choose a prompt provider in ComfyUI Settings.");
            var model = settings.EnhancerServerModels.GetValueOrDefault(provider);
            if (string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException("Choose the server model in ComfyUI Settings → Prompts.");
            var state = new { version = 1, prompt = message, system_prompt = instructions,
                settings = new { adapter = provider, model, base_url = provider == "compatible" ? settings.EnhancerServerApiUrl : "",
                    api_mode = settings.EnhancerServerApiMode, max_output_tokens = Math.Clamp(settings.EnhancerMaxTokens, 16, 16384), timeout = 300 } };
            var helper = new JsonObject { ["class_type"] = "AVSPromptHelper",
                ["inputs"] = new JsonObject { ["state"] = JsonSerializer.Serialize(state) } };
            return await ExecuteEnhancerGraph(client, new JsonObject { ["chat"] = helper });
        }
        if (!ChatModels.Contains(settings.EnhancerModel))
            throw new InvalidOperationException($"Select a chat model offered by this server in ComfyUI Settings → Prompts. Current choice: {settings.EnhancerModel}.");
        if (string.IsNullOrWhiteSpace(Credential) && RequestApiKey != null)
        {
            SessionApiKey = (await RequestApiKey(CancellationToken.None))?.Trim();
            if (string.IsNullOrWhiteSpace(SessionApiKey)) throw new OperationCanceledException();
        }
        if (string.IsNullOrWhiteSpace(Credential)) throw new InvalidOperationException("Add your Comfy.org key in API access.");
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
        return await ExecuteEnhancerGraph(client, graph);
    }

    private async Task<string> ExecuteEnhancerGraph(IComfyConnection client, JsonObject graph)
    {
        graph["enhanced_prompt"] = new JsonObject { ["class_type"] = "PreviewAny", ["inputs"] = new JsonObject { ["source"] = new JsonArray("chat", 0) } };
        var compatibility = EngineCompatibility.CheckWorkflow(graph, ServerCapabilities!);
        if (!compatibility.IsCompatible) throw new InvalidOperationException(string.Join(", ", compatibility.Missing));
        var result = await client.ExecuteAsync(graph);
        using (result.History)
            return EnhancedText(result.History.RootElement, result.PromptId) is { Length: > 0 } text ? text
                : throw new InvalidDataException("ComfyUI returned an empty prompt.");
    }
}
