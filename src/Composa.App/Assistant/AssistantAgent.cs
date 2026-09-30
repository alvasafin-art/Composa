using System.Text.RegularExpressions;
using Composa.AI;
using Composa.Editing;

namespace Composa.App.Assistant;

internal sealed record AssistantAgentResult(string Summary, bool Changed, int ToolCount, string Log);
internal sealed class AssistantAgentException(string message, string log, Exception inner) : Exception(message, inner)
{
    public string ActionLog { get; } = log;
}

/// <summary>Bounded observe/act/verify loop with per-command savepoints and one outer Undo transaction.</summary>
internal static class AssistantAgent
{
    public static async Task<AssistantAgentResult> RunAsync(IAssistantProvider provider, AssistantRequest request, EditorSession session,
        AssistantEditorTools tools, Action<string> progress, CancellationToken token)
    {
        AssistantAgentResult? result = null;
        var messages = new List<AssistantToolMessage>();
        var log = new System.Text.StringBuilder(); string? verified = null;
        var before = AssistantEditorTools.Fingerprint(session); var calls = 0; var failures = 0; var nudged = false;
        try
        {
            await session.RunTransactionAsync("Assistant edit", async _ =>
            {
                for (var step = 0; step < 18; step++)
                {
                    token.ThrowIfCancellationRequested(); progress($"Assistant · step {step + 1}: thinking…");
                    var reply = await provider.PlanAsync(request with { Tools = tools.Definitions,
                        DocumentContext = JavaScriptRuntimeContext(session), ToolMessages = messages,
                        PreviewDataUrl = request.PreviewDataUrl == null ? null : AssistantFile.Preview(session.Composite()) }, token);
                    token.ThrowIfCancellationRequested();
                    var actions = reply.Calls;
                    if (actions.Count == 0 && !string.IsNullOrWhiteSpace(reply.Script))
                    {
                        using var script = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new { code = reply.Script }));
                        actions = [new AssistantToolCall("script-" + step, "execute_script", script.RootElement.Clone())];
                    }
                    if (actions.Count == 0)
                    {
                        var changed = before != AssistantEditorTools.Fingerprint(session);
                        if (changed && verified != AssistantEditorTools.Fingerprint(session))
                        { messages.Add(new("user", "Inspect get_document now to verify the actual resulting document before finalizing.")); continue; }
                        if (!changed && !nudged && IsEditRequest(request.UserText))
                        {
                            nudged = true; messages.Add(new("user", "No editing operation has been executed and the document is unchanged. This is an editing request. Call the actual editing tools now; do not merely claim completion.")); continue;
                        }
                        if (failures > 0 && !changed) throw new InvalidOperationException("The agent could not apply the requested edit. No changes were committed. " + reply.Summary);
                        var summary = failures == 0 ? reply.Summary : reply.Summary + $"\nDuring this run {failures} command(s) failed and were rolled back. See Operations for details.";
                        result = new(summary, changed, calls, log.ToString());
                        if (!changed) throw new ReadOnlyCompletion();
                        return;
                    }
                    if (actions.Count > 16 || calls + actions.Count > 48) throw new InvalidOperationException("The Assistant reached its command limit; all changes were rolled back.");
                    messages.Add(new("assistant", reply.Summary, Calls: actions));
                    foreach (var call in actions)
                    {
                        token.ThrowIfCancellationRequested(); calls++; progress("Assistant · " + call.Name);
                        string outcome;
                        try
                        {
                            var state = AssistantEditorTools.Fingerprint(session);
                            outcome = await session.RunSavepointAsync(() => tools.ExecuteAsync(call, token));
                            if (state != AssistantEditorTools.Fingerprint(session))
                            {
                                // Send the real resulting structure with the command result. Asking a
                                // small local model to re-plan an already completed edit for verification
                                // can make it recreate the same objects a second time.
                                outcome += "\nACTUAL DOCUMENT AFTER THIS COMMAND:\n" + Composa.App.Automation.JavaScriptRuntime.Describe(session, 0, 6, 160);
                                verified = AssistantEditorTools.Fingerprint(session);
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception error)
                        { failures++; outcome = "ERROR: " + error.Message + ". This command was rolled back. Correct its arguments/schema before continuing."; }
                        if (call.Name == "get_document") verified = AssistantEditorTools.Fingerprint(session);
                        log.AppendLine(call.Name + " " + call.Arguments.GetRawText()).AppendLine(ChatCompletionAssistantProvider.Bounded(outcome, 1200));
                        messages.Add(new("tool", ChatCompletionAssistantProvider.Bounded(outcome, 4500), call.Id));
                    }
                    // Retain complete assistant/tool groups, never orphan a tool result.
                    while (messages.Sum(message => message.Text.Length + (message.Calls?.Sum(call => call.Arguments.GetRawText().Length) ?? 0)) > 7500 && messages.Count > actions.Count + 1)
                    { messages.RemoveAt(0); while (messages.Count > 0 && messages[0].Role == "tool") messages.RemoveAt(0); }
                }
                throw new InvalidOperationException("The Assistant did not finish within 18 steps. All changes were rolled back; split the request into smaller tasks.");
            });
        }
        catch (ReadOnlyCompletion) { }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { throw new AssistantAgentException(error.Message, log.ToString(), error); }
        return result!;
    }
    private static string JavaScriptRuntimeContext(EditorSession session) => Composa.App.Automation.JavaScriptRuntime.Describe(session);
    private static bool IsEditRequest(string text) => !Regex.IsMatch(text, @"^\s*(как|почему|что|расскажи|объясни|how|why|what|explain)\b", RegexOptions.IgnoreCase)
        && Regex.IsMatch(text, @"созда|нарис|замен|измен|удал|добав|сдела|переме|умень|увели|выдел|примен|выпол|редакт|create|draw|replace|edit|remove|add|move|apply|resize|generate", RegexOptions.IgnoreCase);
    private sealed class ReadOnlyCompletion : Exception;
}
