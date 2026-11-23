namespace Composa.App;

public sealed partial class MainWindow
{
    private static readonly DockPanelState DefaultResourcePlacement = new(Visible: false, Height: 285, Column: 1, Standalone: true);

    internal EditorSetup ReadEditorSetup() => new(settings.Theme,
        settings.ToolsPosition == "Bottom" && dock.Section("Layers").State.Left ? EditorLayoutPreset.Modern : EditorLayoutPreset.Classic,
        generationSection.State.Visible ? AiPanelPlacement.Sidebar : AiPanelPlacement.UnderSelection);

    internal void ApplyEditorArrangement(EditorLayoutPreset? layout, AiPanelPlacement? aiPanel, bool firstArrangement = false)
    {
        if (layout is null)
        {
            if (aiPanel is { } placement)
            {
                dock.SetVisible(generationSection, placement == AiPanelPlacement.Sidebar);
                if (placement == AiPanelPlacement.Sidebar) dock.Activate(generationSection);
            }
            return;
        }

        bool modern = layout == EditorLayoutPreset.Modern;
        bool sideAi = (aiPanel ?? ReadEditorSetup().AiPanel) == AiPanelPlacement.Sidebar;
        var tabs = modern ? Guid.NewGuid() : (Guid?)null;
        var panels = dock.CaptureLayout();
        panels["Layers"] = new(Height: 420, Left: modern, Order: -4, TabGroup: tabs, ActiveTab: modern);
        panels["Resources"] = modern
            ? new(Height: 420, Left: true, Order: -3, TabGroup: tabs)
            : DefaultResourcePlacement;
        panels["Properties"] = new(Height: 235, Order: -2, Standalone: true);
        panels["AI Generation"] = new(Visible: sideAi, Height: 194, Order: -1, Standalone: true);
        if (firstArrangement) panels["History"] = panels["History"] with { Visible = false };
        ApplyWorkspace(new(panels, settings.CanvasOnly, settings.TabbedPanels, modern ? "Bottom" : "Left"));
    }

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
        dock.RestoreLayout(panels, persist: false);
        settings.Dock = dock.CaptureLayout();
        PlaceTools(layout.ToolsPosition);
        settings.Save();
        SetCanvasOnly(layout.CanvasOnly);
        RebuildOptions();
        RefreshMenuState();
    }
}
