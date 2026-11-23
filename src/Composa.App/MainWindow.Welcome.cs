using Composa.App.Dialogs;

namespace Composa.App;

public sealed partial class MainWindow
{
    internal async Task ShowEditorWelcome(string version)
    {
        if (settings.SeenWelcomeVersions.Contains(version)) return;

        var current = ReadEditorSetup();
        bool firstArrangement = settings.Dock.Count == 0;
        var initial = firstArrangement ? current with { AiPanel = AiPanelPlacement.Sidebar } : current;
        var chosen = await new EditorWelcomeWindow(this, initial).ShowDialog<EditorSetup?>(this);
        settings.SeenWelcomeVersions.Add(version);
        if (chosen is { } setup)
        {
            settings.Theme = setup.Theme;
            (Avalonia.Application.Current as App)?.ApplyTheme(setup.Theme);
            ApplyEditorArrangement(firstArrangement || setup.Layout != current.Layout ? setup.Layout : null,
                setup.AiPanel, firstArrangement);
        }
        settings.Save();
    }
}
