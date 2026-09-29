using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Composa.AI;

namespace Composa.App.Assistant;

public sealed class LlamaServerHost(Settings settings) : IDisposable
{
    private Process? ownedProcess;
    public string Status { get; private set; } = "Stopped";
    public event Action? StatusChanged;

    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        if (await Healthy(cancellationToken)) { SetStatus("Ready"); return; }
        if (!settings.AssistantAutoStart) throw new InvalidOperationException("The local Assistant server is not running.");
        if (!Uri.TryCreate(settings.AssistantServerUrl, UriKind.Absolute, out var uri) || !uri.IsLoopback)
            throw new InvalidOperationException("Automatic llama.cpp startup is only allowed for a localhost server URL.");
        if (!File.Exists(settings.AssistantServerExecutable)) throw new FileNotFoundException("Choose llama-server.exe in Assistant Settings.", settings.AssistantServerExecutable);
        if (!File.Exists(settings.AssistantModelPath)) throw new FileNotFoundException("Choose a GGUF model in Assistant Settings.", settings.AssistantModelPath);
        if (ownedProcess is not { HasExited: false })
        {
            var start = new ProcessStartInfo(settings.AssistantServerExecutable)
            {
                WorkingDirectory = Path.GetDirectoryName(settings.AssistantServerExecutable)!,
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[]
            {
                "--model", settings.AssistantModelPath, "--host", uri.Host, "--port", uri.Port.ToString(),
                "--ctx-size", Math.Clamp(settings.AssistantContextSize, 2048, 131072).ToString(), "--parallel", "1",
                "--jinja", "--reasoning", "off", "--no-webui"
            }) start.ArgumentList.Add(argument);
            ownedProcess = Process.Start(start) ?? throw new InvalidOperationException("Could not start llama-server.");
            ownedProcess.OutputDataReceived += (_, _) => { };
            ownedProcess.ErrorDataReceived += (_, _) => { };
            ownedProcess.BeginOutputReadLine();
            ownedProcess.BeginErrorReadLine();
        }
        SetStatus("Loading local model…");
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ownedProcess.HasExited) throw new InvalidOperationException($"llama-server exited with code {ownedProcess.ExitCode} while loading the model.");
            if (await Healthy(cancellationToken)) { SetStatus("Ready"); return; }
            await Task.Delay(500, cancellationToken);
        }
        throw new TimeoutException("llama.cpp did not become ready within three minutes.");
    }

    private async Task<bool> Healthy(CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await client.GetAsync(new Uri(new Uri(settings.AssistantServerUrl.TrimEnd('/') + "/"), "health"), cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    private void SetStatus(string status) { Status = status; StatusChanged?.Invoke(); }

    public void Dispose()
    {
        if (ownedProcess is { HasExited: false })
        {
            try { ownedProcess.Kill(entireProcessTree: true); }
            catch { }
        }
        ownedProcess?.Dispose();
    }
}

public sealed class LlamaAssistantProvider(Settings settings, LlamaServerHost host) : IAssistantProvider
{
    public string Id => "local-llama-cpp";

    public async Task<AssistantPlan> PlanAsync(AssistantRequest request, CancellationToken cancellationToken = default)
    {
        await host.EnsureReadyAsync(cancellationToken);
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        var system = """
        You are the built-in Composa image-editor assistant. Return a concise explanation and a JavaScript script that accomplishes the request.
        Use only the supplied Composa scripting API. Never invent methods, access the filesystem except through doc.export when explicitly requested,
        or wrap the script in Markdown fences. Prefer tags, then layer names/types. If the request needs no document edit, return an empty script.
        """ + "\n\nSCRIPTING API:\n" + request.ScriptingReference + "\n\nCURRENT DOCUMENT:\n" + request.DocumentContext;
        var payload = new JsonObject
        {
            ["model"] = "local",
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = request.UserText }
            },
            ["temperature"] = 0.15,
            ["max_tokens"] = Math.Clamp(settings.AssistantMaxTokens, 256, 8192),
            ["stream"] = false,
            ["reasoning_effort"] = "none",
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["schema"] = new JsonObject
                {
                    ["type"] = "object", ["additionalProperties"] = false,
                    ["properties"] = new JsonObject
                    {
                        ["summary"] = new JsonObject { ["type"] = "string" },
                        ["script"] = new JsonObject { ["type"] = "string" }
                    },
                    ["required"] = new JsonArray("summary", "script")
                }
            }
        };
        using var response = await client.PostAsJsonAsync(settings.AssistantServerUrl.TrimEnd('/') + "/v1/chat/completions", payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"llama.cpp returned {(int)response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        var message = document.RootElement.GetProperty("choices")[0].GetProperty("message");
        var content = message.GetProperty("content").GetString() ?? throw new InvalidDataException("The Assistant returned an empty response.");
        return ParsePlan(content);
    }

    internal static AssistantPlan ParsePlan(string content)
    {
        try
        {
            var plan = JsonSerializer.Deserialize<AssistantPlan>(ExtractJsonObject(content), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (plan != null) return plan;
        }
        catch (InvalidDataException) { }
        catch (JsonException) { }

        // Older llama.cpp/Qwen template combinations can ignore response_format yet still follow the semantic
        // instruction and return one fenced JavaScript block. Accept that narrow, reviewable form as a fallback.
        var fence = content.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            var codeStart = content.IndexOf('\n', fence + 3);
            if (codeStart > fence)
            {
                var language = content[(fence + 3)..codeStart].Trim();
                var codeEnd = content.IndexOf("```", codeStart + 1, StringComparison.Ordinal);
                if (codeEnd > codeStart && (language.Length == 0 || language.Equals("js", StringComparison.OrdinalIgnoreCase) || language.Equals("javascript", StringComparison.OrdinalIgnoreCase)))
                {
                    var summary = content[..fence].Trim();
                    return new AssistantPlan(string.IsNullOrWhiteSpace(summary) ? "Prepared a script for review." : summary, content[(codeStart + 1)..codeEnd].Trim());
                }
            }
        }
        throw new InvalidDataException("The Assistant did not return a structured plan or a JavaScript block.");
    }

    // Some chat templates can retain a short natural-language prefix despite a response schema. Parse the first
    // complete JSON object instead of making that harmless template behavior break the Assistant.
    internal static string ExtractJsonObject(string content)
    {
        var start = content.IndexOf('{');
        if (start < 0)
        {
            var excerpt = content.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (excerpt.Length > 360) excerpt = excerpt[..360] + "…";
            throw new InvalidDataException("The Assistant response did not contain a JSON plan. Response: " + excerpt);
        }
        var depth = 0;
        var quoted = false;
        var escaped = false;
        for (var index = start; index < content.Length; index++)
        {
            var character = content[index];
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') quoted = false;
                continue;
            }
            if (character == '"') quoted = true;
            else if (character == '{') depth++;
            else if (character == '}' && --depth == 0) return content[start..(index + 1)];
        }
        throw new InvalidDataException("The Assistant returned an incomplete JSON plan.");
    }
}
