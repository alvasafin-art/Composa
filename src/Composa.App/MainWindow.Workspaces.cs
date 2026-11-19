namespace Composa.App;

public sealed partial class MainWindow
{
    internal WorkspaceLayout CaptureWorkspace() => new(dock.CaptureLayout(), settings.CanvasOnly, dock.Tabbed);
    internal void ApplyWorkspace(WorkspaceLayout layout)
    {
        dock.SetTabbed(layout.TabbedPanels);
        settings.TabbedPanels = layout.TabbedPanels;
        var panels = new Dictionary<string, DockPanelState>(layout.Panels ?? []);
        panels.TryAdd("AI Generation", new(false, false, 164));
        dock.RestoreLayout(panels);
        SetCanvasOnly(layout.CanvasOnly);
        RefreshMenuState();
    }
}
