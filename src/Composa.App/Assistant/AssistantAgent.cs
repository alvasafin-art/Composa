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
        AssistantEditorTools tools, Action<string> progress, CancellationToken token, int documentSwitches = 0, RunBudget? budget = null)
    {
        if (request.ScriptOnly || AssistantIntent.RequestsScript(request.UserText))
            throw new InvalidOperationException("A script-only request must return code for review, not execute editor operations.");
        AssistantAgentResult? result = null;
        var guard = new AssistantCommandGuard();
        var messages = new List<AssistantToolMessage>(request.ToolMessages);
        var log = new System.Text.StringBuilder(); string? verified = null;
        var before = AssistantEditorTools.Fingerprint(session); var calls = 0; var failures = 0; var nudged = false;
        var verificationReminders = 0;
        budget ??= new RunBudget();
        try
        {
            await session.RunTransactionAsync("Assistant edit", async _ =>
            {
                while (budget.Steps < 32)
                {
                    var step = budget.Steps++;
                    token.ThrowIfCancellationRequested(); progress($"Assistant · step {step + 1}: thinking…");
                    var reply = await provider.PlanAsync(request with { Tools = tools.Definitions,
                        DocumentContext = "HOST TASK STATE (authoritative; do not restart completed actions):\n" + tools.WorkflowContext + "\nDOCUMENT:\n" + tools.Context, ToolMessages = messages,
                        PreviewDataUrl = request.PreviewDataUrl == null ? null : AssistantFile.Preview(session.Composite()) }, token);
                    token.ThrowIfCancellationRequested();
                    var actions = reply.Calls;
                    if (actions is [{ Name: "finish_task", Arguments: var finish }] && finish.ValueKind == System.Text.Json.JsonValueKind.Object &&
                        finish.TryGetProperty("summary", out var finishSummary) && finishSummary.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        // A tool-tuned model can explicitly finish instead of looping over reads.
                        // The same independent completion checks below still gate the commit.
                        reply = reply with { Summary = finishSummary.GetString()!, Script = "", Calls = [] };
                        actions = [];
                    }
                    if (actions.Count == 0 && !string.IsNullOrWhiteSpace(reply.Script))
                    {
                        using var script = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new { code = reply.Script }));
                        actions = [new AssistantToolCall("script-" + step, "execute_script", script.RootElement.Clone())];
                    }
                    if (actions.Count == 0)
                    {
                        if (before != AssistantEditorTools.Fingerprint(session) && tools.NeedsFinalCheck)
                        {
                            if (++verificationReminders > 2) throw new InvalidOperationException("The model did not verify the requested result after two reminders. Pending edits were rolled back.");
                            messages.Add(new("user", "Call verify_document with the user's requested final properties and preserved invariants on actual output ids before finishing. Do not repeat edits.")); continue;
                        }
                        var completionReport = tools.CheckCompletion();
                        if (completionReport != null) log.AppendLine("HOST FINAL VERIFICATION " + completionReport);
                        if (tools.VerificationFailed)
                            throw new InvalidOperationException("The actual document failed the requested postconditions. All pending edits were rolled back; a completion claim cannot override failed verification.");
                        var changed = before != AssistantEditorTools.Fingerprint(session);
                        if (changed && verified != AssistantEditorTools.Fingerprint(session))
                        { messages.Add(new("user", "Inspect get_document now to verify the actual resulting document before finalizing.")); continue; }
                        if (!changed && !nudged && tools.TaskIntent == "edit" && !tools.HasFinalChecks)
                        {
                            nudged = true; messages.Add(new("user", "No editing operation has been executed and the document is unchanged. This is an editing request. Call the actual editing tools now; do not merely claim completion.")); continue;
                        }
                        if (!changed && tools.TaskIntent == "edit" && !tools.HasFinalChecks) throw new InvalidOperationException("The agent did not execute the requested edit. The document is unchanged. Inspect Operations or try a more capable model.");
                        if (failures > 0 && !changed && tools.TaskIntent != "inspect" && !tools.HasFinalChecks) throw new InvalidOperationException("The agent could not apply the requested edit. No changes were committed.");
                        var summary = failures == 0 ? reply.Summary : reply.Summary + $"\nDuring this run {failures} command(s) failed and were rolled back. See Operations for details.";
                        result = new(summary, changed, calls, log.ToString());
                        if (!changed) throw new ReadOnlyCompletion();
                        return;
                    }
                    if (actions.Count > 16 || budget.Calls + actions.Count > 96) throw new InvalidOperationException("The Assistant reached its command limit; all changes were rolled back.");
                    messages.Add(new("assistant", reply.Summary, Calls: actions));
                    foreach (var call in actions)
                    {
                        token.ThrowIfCancellationRequested(); calls++; budget.Calls++; progress("Assistant · " + call.Name);
                        string outcome;
                        var state = AssistantEditorTools.Fingerprint(session);
                        try { guard.CheckFailures(call, state); if (tools.ReadOnly(call) && call.Name != "begin_task") guard.Observe(call, state); }
                        catch (Exception error) { log.AppendLine("STOP: " + error.Message); throw; }
                        bool replay;
                        try { replay = guard.AlreadyApplied(call, state); }
                        catch (Exception error) { log.AppendLine("STOP: " + error.Message); throw; }
                        try
                        {
                            outcome = replay ? "ALREADY APPLIED: this identical operation succeeded earlier in this request. It was NOT executed again. Do not repeat it for verification. For intentionally distinct objects use distinct names/arguments or a single explicit loop in a script. Continue with the next requested action or finish.\n" + JavaScriptRuntimeContext(session)
                                : await session.RunSavepointAsync(() => tools.ExecuteAsync(call, token));
                            if (state != AssistantEditorTools.Fingerprint(session))
                            {
                                guard.AppliedCommand(call, AssistantEditorTools.Fingerprint(session));
                                // Send the real resulting structure with the command result. Asking a
                                // small local model to re-plan an already completed edit for verification
                                // can make it recreate the same objects a second time.
                                verified = AssistantEditorTools.Fingerprint(session);
                                tools.DocumentEdited = true;
                            }
                            tools.Record(call, true, state != AssistantEditorTools.Fingerprint(session));
                            outcome = AssistantEditorTools.Receipt(call, tools.ReadOnly(call), outcome, state, session);
                        }
                        catch (AssistantDocumentSwitch switched)
                        {
                            messages.Add(new("tool", System.Text.Json.JsonSerializer.Serialize(new { ok = true, opened = true, document = switched.Session.Title }), call.Id));
                            // Complete the model's remaining call ids; no commands from a stale context run.
                            foreach (var skipped in actions.SkipWhile(action => action != call).Skip(1))
                                messages.Add(new("tool", "{\"ok\":false,\"error\":\"Document changed. Inspect the new document and reissue this call.\"}", skipped.Id));
                            throw;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception error)
                        { failures++; guard.Failed(call, state); tools.Record(call, false, false); outcome = System.Text.Json.JsonSerializer.Serialize(new { ok = false, operation = call.Name,
                            error = "ERROR: " + error.Message, rolledBack = true, recovery = "Inspect the exact schema and actual state; correct arguments before retrying. Never resend an unchanged failing call." }); }
                        if (call.Name is "get_document" or "get_document_state" or "verify_document") verified = AssistantEditorTools.Fingerprint(session);
                        log.AppendLine(call.Name + " " + call.Arguments.GetRawText()).AppendLine(ChatCompletionAssistantProvider.Bounded(outcome, 1200));
                        messages.Add(new("tool", outcome, call.Id) { ImageDataUrl = tools.TakeRenderedImage() });
                    }
                    // Retain complete assistant/tool groups, never orphan a tool result.
                    while (messages.Sum(message => message.Text.Length + (message.Calls?.Sum(call => call.Arguments.GetRawText().Length) ?? 0)) > 24000 && messages.Count > actions.Count + 1)
                    { messages.RemoveAt(0); while (messages.Count > 0 && messages[0].Role == "tool") messages.RemoveAt(0); }
                }
                throw new InvalidOperationException("The Assistant did not finish within 32 steps. All changes were rolled back; split the request into smaller tasks.");
            });
        }
        catch (ReadOnlyCompletion) { }
        catch (AssistantDocumentSwitch switched)
        {
            if (documentSwitches >= 4) throw new AssistantAgentException("Too many document switches. Open the target document and retry.", log.ToString(), switched);
            try
            {
                var continued = await RunAsync(provider, request with { ToolMessages = messages }, switched.Session, tools.ForDocument(switched.Session), progress, token, documentSwitches + 1, budget);
                return continued with { ToolCount = calls + continued.ToolCount, Log = log + "Opened: " + switched.Session.Title + "\n" + continued.Log };
            }
            catch (AssistantAgentException error) { throw new AssistantAgentException(error.Message, log + "Opened: " + switched.Session.Title + "\n" + error.ActionLog, error); }
        }
        catch (OperationCanceledException error) { error.Data["AssistantActionLog"] = log.ToString(); throw; }
        catch (Exception error) { throw new AssistantAgentException(error.Message, log.ToString(), error); }
        return result!;
    }
    private static string JavaScriptRuntimeContext(EditorSession session) => Composa.App.Mcp.EditorDocumentInspection.Read(session, 0, 6);
    private sealed class ReadOnlyCompletion : Exception;
    internal sealed class RunBudget { internal int Steps; internal int Calls; }
}
