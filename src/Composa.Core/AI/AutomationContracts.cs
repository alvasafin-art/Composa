using Composa.Editing;
using Composa.Model;

namespace Composa.AI;

/// <summary>Shared entry point for UI, MCP, future scripts, plugins and assistant providers.</summary>
public interface IEditorCommandService
{
    EditorSession Session { get; }
    void Transaction(string name, Action<EditorSession> commands);
    IReadOnlyList<Layer> FindLayersByTag(string tag);
}

public sealed class EditorCommandService(EditorSession session) : IEditorCommandService
{
    public EditorSession Session { get; } = session;

    public void Transaction(string name, Action<EditorSession> commands)
        => Session.RunTransaction(name, commands);

    public IReadOnlyList<Layer> FindLayersByTag(string tag) => Session.FindLayersByTag(tag).ToList();
}

public interface IAiTaskRunner
{
    Task RunAsync(IEditorCommandService editor, AiTaskRequest request, CancellationToken cancellationToken = default);
}

public sealed record AssistantMessage(string Role, string Text);
public sealed record AssistantAttachment(string Name, string? Text = null, string? ImageDataUrl = null);
public sealed record AssistantRequest(string UserText, string DocumentContext, string ScriptingReference)
{
    public IReadOnlyList<AssistantMessage> History { get; init; } = [];
    public IReadOnlyList<AssistantAttachment> Attachments { get; init; } = [];
    public string? PreviewDataUrl { get; init; }
}
public sealed record AssistantPlan(string Summary, string Script);

public interface IAssistantProvider
{
    string Id { get; }
    Task<AssistantPlan> PlanAsync(AssistantRequest request, CancellationToken cancellationToken = default);
}
public interface IScriptRuntime { string Language { get; } }
public interface IPluginCommandProvider { IEnumerable<string> CommandIds { get; } }
