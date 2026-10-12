using System.Text.Json;
using Composa.AI;

namespace Composa.App.Assistant;

/// <summary>Code authoring can inspect supplied data but cannot reach an editing command.</summary>
internal static class AssistantScriptAuthor
{
    private static readonly string[] Allowed = ["list_read_folders", "list_files", "read_file", "read_attachment"];
    private static readonly AssistantToolDefinition ReturnScript = new("return_script", "Return the finished reusable script and a brief explanation. This delivers code for review; it does not execute anything. Call alone when the required source data has been read.",
        JsonSerializer.SerializeToElement(new { type = "object", properties = new { summary = new { type = "string" }, script = new { type = "string" } }, required = new[] { "summary", "script" }, additionalProperties = false }));

    internal static async Task<AssistantPlan> RunAsync(IAssistantProvider provider, AssistantRequest request, AssistantEditorTools tools,
        Action<string> progress, CancellationToken token)
    {
        var history = new List<AssistantToolMessage>();
        var guard = new AssistantCommandGuard();
        var repairs = 0;
        for (var step = 0; step < 12; step++)
        {
            token.ThrowIfCancellationRequested(); progress($"Script author · step {step + 1}");
            var reply = await provider.PlanAsync(request with { ScriptOnly = true, AllowAuthoringReads = true,
                Tools = tools.Definitions.Where(tool => Allowed.Contains(tool.Name)).Append(ReturnScript).ToArray(), ToolMessages = history }, token);
            token.ThrowIfCancellationRequested();
            AssistantPlan? draft = null;
            if (reply.Calls is [{ Name: "return_script", Arguments: var artifact }] && artifact.ValueKind == JsonValueKind.Object &&
                artifact.TryGetProperty("script", out var code) && code.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(code.GetString()))
                draft = new(artifact.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.String ? summary.GetString()! : "Script ready", code.GetString()!);
            else if (!string.IsNullOrWhiteSpace(reply.Script) || reply.Calls.Count == 0 || reply.Calls is [{ Name: "execute_script" }])
                draft = AssistantIntent.AsScriptDraft(reply); // Legacy execute_script output is code, never a command here.
            if (draft != null)
            {
                var error = AssistantScriptValidation.Error(draft.Script, request.ScriptTarget);
                if (error == null) return draft;
                if (++repairs > 2) throw new InvalidDataException("The model returned invalid code after two repair attempts. " + error);
                progress("Script author · correcting validation error");
                var feedback = JsonSerializer.Serialize(new { ok = false, error, instruction = "Return the complete corrected script. Static validation only; nothing was executed." });
                if (reply.Calls.Count == 0)
                {
                    history.Add(new("assistant", JsonSerializer.Serialize(new { summary = draft.Summary, script = draft.Script })));
                    history.Add(new("user", feedback));
                }
                else
                {
                    history.Add(new("assistant", reply.Summary, Calls: reply.Calls));
                    foreach (var call in reply.Calls) history.Add(new("tool", feedback, call.Id));
                }
                continue;
            }
            if (reply.Calls.Count > 8) throw new InvalidOperationException("Too many file calls while authoring a script.");
            history.Add(new("assistant", reply.Summary, Calls: reply.Calls));
            foreach (var call in reply.Calls)
            {
                token.ThrowIfCancellationRequested(); guard.Observe(call, "authoring");
                string result;
                try
                {
                    if (!Allowed.Contains(call.Name)) throw new InvalidOperationException("Script authoring permits file reads only; return code without executing it.");
                    progress("Script author · " + call.Name);
                    result = await tools.ExecuteAsync(call, token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { result = JsonSerializer.Serialize(new { ok = false, error = error.Message }); }
                history.Add(new("tool", result, call.Id));
            }
        }
        throw new InvalidOperationException("The model did not return a script within 12 steps. No document changes were made.");
    }
}
