namespace Composa.App;

public sealed partial class MainWindow
{
    internal WorkspaceLayout CaptureWorkspace() => new(dock.CaptureLayout(), settings.CanvasOnly, dock.Tabbed);
    internal void ApplyWorkspace(WorkspaceLayout layout)
    {
        dock.SetTabbed(layout.TabbedPanels);
        settings.TabbedPanels = layout.TabbedPanels;
        dock.RestoreLayout(layout.Panels ?? []);
        SetCanvasOnly(layout.CanvasOnly);
        RefreshMenuState();
    }
}
