using Composa.Editing;

namespace Composa.AI;

public sealed record AiTaskAvailability(AiTaskKind Task, bool Available, string? Reason)
{
    public static AiTaskAvailability Resolve(EditorSession? session, EngineProfile? engine, AiTaskKind task, bool cropExpands = false)
    {
        if (task == AiTaskKind.MatchToScene)
        {
            if (session == null) return new(task, false, "Open a document first.");
            if (session.ActiveLayer?.Pixels == null) return new(task, false, "Select an image layer first.");
            return new(task, true, null);
        }
        if (engine == null) return new(task, false, "No Engine Profile is installed or selected.");
        if (engine.Binding(task) == null) return new(task, false, $"{engine.DisplayName} does not provide {task.DisplayName()}.");
        if (session == null && task != AiTaskKind.GenerateImage) return new(task, false, "Open a document first.");
        if (task.RequiresSelection() && session?.Selection == null) return new(task, false, "Make a selection first.");
        return new(task, true, null);
    }

    public static IReadOnlyList<AiTaskKind> Contextual(EditorSession? session, EngineProfile? engine, bool cropExpands = false)
    {
        if (session == null) return engine?.Binding(AiTaskKind.GenerateImage) != null ? [AiTaskKind.GenerateImage] : [];
        var candidates = new List<AiTaskKind>();
        if (session.Selection != null) candidates.AddRange([AiTaskKind.GenerativeFill, AiTaskKind.RemoveObject]);
        else candidates.Add(AiTaskKind.ImageEdit);
        if (session.ActiveLayer is { Pixels: not null } layer && layer.Pixels.GetPixelSpan().IndexOfAnyExcept((byte)0) < 0) candidates.Add(AiTaskKind.GenerateImage);
        if (cropExpands) candidates.Add(AiTaskKind.GenerativeExpand);
        return candidates.Where(task => Resolve(session, engine, task, cropExpands).Available).Distinct().ToList();
    }
}
