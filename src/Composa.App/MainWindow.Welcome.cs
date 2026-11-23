using Composa.App.Dialogs;

namespace Composa.App;

public sealed partial class MainWindow
{
    internal async Task ShowEditorWelcome()
    {
        if (settings.EditorWelcomeSeen) return;

        var current = ReadEditorSetup();
        var originalLanguage = Locale.Language;
        bool firstArrangement = settings.Dock.Count == 0;
        var initial = firstArrangement ? current with { AiPanel = AiPanelPlacement.Sidebar } : current;
        var choice = new EditorWelcomeWindow.Choice(initial, originalLanguage);
        while (true)
        {
            var chosen = await new EditorWelcomeWindow(this, choice).ShowDialog<EditorWelcomeWindow.Choice?>(this);
            if (chosen == null)
            {
                settings.Language = originalLanguage;
                settings.Theme = current.Theme;
                Locale.Apply(originalLanguage, this, null);
                (Avalonia.Application.Current as App)?.ApplyTheme(current.Theme);
                break;
            }
            choice = chosen;
            settings.Language = choice.Language;
            settings.Theme = choice.Setup.Theme;
            var back = false;
            await ShowAiSettings(firstRun: true, firstRunBack: () => back = true);
            if (back) continue;
            ApplyEditorArrangement(firstArrangement || choice.Setup.Layout != current.Layout ? choice.Setup.Layout : null,
                choice.Setup.AiPanel, firstArrangement);
            break;
        }
        settings.EditorWelcomeSeen = true;
        settings.Save();
    }
}
