namespace Composa.App;

public sealed partial class MainWindow
{
    internal WorkspaceLayout CaptureWorkspace() => new(dock.CaptureLayout(), settings.CanvasOnly);
    internal void ApplyWorkspace(WorkspaceLayout layout)
    {
        dock.RestoreLayout(layout.Panels ?? []);
        SetCanvasOnly(layout.CanvasOnly);
        RefreshMenuState();
    }
}
