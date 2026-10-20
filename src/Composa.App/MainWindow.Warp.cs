using Composa.Editing;

namespace Composa.App;

public sealed partial class MainWindow
{
    public void StartWarp()
    {
        if (session?.CanWarp != true || IsDragging) return;
        SelectTool(Tool.Move); canvas.StartWarp(); RebuildOptions(); UpdateStatus();
    }

    private bool CommitWarp(EditorSession target)
    {
        try { target.CommitWarp(); return true; }
        catch (Exception error) { ShowProblem(error.Message); return false; }
    }
}
