using Composa.App.Dialogs;

namespace Composa.App;

public sealed partial class MainWindow
{
    private async Task FindCommand()
    {
        RefreshMenuState();
        var choices = AllShortcuts.Where(c => c.Id != FindCommandId).Concat(new[]
        {
            Setting("Editor preferences", "Editor", 3, terms: "panels tabs updates linked files"),
            Setting("ComfyUI server", "Connection", 0, "server"),
            Setting("Comfy credits API key", "Connection", 0, "key"),
            Setting("Image models", "AI", 1, terms: string.Join(" ", aiTasks.Engines.Profiles.Select(p => p.ModelName ?? p.DisplayName))),
            Setting("Workflow and model files", "Image models", 1, "files"),
            Setting("Prompt enhancer", "AI", 2, terms: "Text model CLI API")
        }).ToArray();
        Shortcut Setting(string title, string location, int page, string? field = null, string terms = "") =>
            new(title, title, "Settings › " + location, null, () => _ = ShowAiSettings(page, field), viewOnly: true) { SearchTerms = terms };
        bool CanRun(Shortcut command) => !IsDragging && (command.Group != "Tools and Canvas" || HasDocument) && (command.Enabled?.Invoke() ?? true);
        var selected = await new CommandSearchWindow(choices, CanRun).ShowDialog<Shortcut?>(this);
        if (selected == null || !CanRun(selected)) return;
        canvas.Focus(); Execute(selected);
    }
}
