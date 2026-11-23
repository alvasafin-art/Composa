namespace Composa.App;

public sealed partial class MainWindow
{
    private static readonly DockPanelState DefaultResourcePlacement = new(Visible: false, Height: 285, Column: 1, Standalone: true);

    internal WorkspaceLayout CaptureWorkspace() => new(dock.CaptureLayout(), settings.CanvasOnly, dock.Tabbed, settings.ToolsPosition);
    internal void ApplyWorkspace(WorkspaceLayout layout)
    {
        dock.SetTabbed(layout.TabbedPanels);
        settings.TabbedPanels = layout.TabbedPanels;
        var panels = new Dictionary<string, DockPanelState>(layout.Panels ?? []);
        panels.TryAdd("AI Generation", new(false, false, 164));
        panels.TryAdd("Properties", new DockPanelState(Height: 232));
        panels.TryAdd("Layers", new DockPanelState(Height: 285));
        panels.TryAdd("Resources", DefaultResourcePlacement);
        foreach (var title in new[] { "History", "Channels", "Navigator" })
            if (!panels.ContainsKey(title)) panels[title] = new DockPanelState(Visible: title == "History");
        dock.RestoreLayout(panels);
        PlaceTools(layout.ToolsPosition);
        settings.Save();
        SetCanvasOnly(layout.CanvasOnly);
        RebuildOptions();
        RefreshMenuState();
    }
}
