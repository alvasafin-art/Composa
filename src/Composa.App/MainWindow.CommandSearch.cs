using Composa.App.Dialogs;
using Composa.AI;

namespace Composa.App;

public sealed partial class MainWindow
{
    private async Task FindCommand()
    {
        RefreshMenuState();
        var choices = AllShortcuts.Where(c => c.Id != FindCommandId).Concat(new[]
        {
            Setting("Editor preferences", "Editor", 3, terms: "panels tabs updates linked files interface theme light dark appearance language русский язык оформление тема"),
            Setting("ComfyUI server", "Connection", 0, "server", "сервер подключение адрес"),
            Setting("Comfy credits API key", "Connection", 0, "key", "ключ токен"),
            Setting("Image models", "AI", 1, terms: "модели изображения"),
            Setting("Model components", "Image models", 1, "files"),
            Setting("Prompt enhancer", "AI", 2, terms: "Text model CLI API")
        }).Concat(aiTasks.Engines.Profiles.Select(p => Setting(p.ModelName ?? p.DisplayName, "Image models", 1, "model:" + p.Id, p.Id))).ToArray();
        Shortcut Setting(string title, string location, int page, string? field = null, string terms = "") =>
            new(title, title, "Settings › " + location, null, () => _ = ShowAiSettings(page, field), viewOnly: true) { SearchTerms = terms };
        bool CanRun(Shortcut command) => !IsDragging && (command.Group != "Tools and Canvas" || HasDocument) && (command.Enabled?.Invoke() ?? true);
        string Reason(Shortcut command)
        {
            if (IsDragging) return "Finish the current drag first.";
            if (!HasDocument) return "Open or create a document first.";
            if (command.UnavailableReason is { } reason) return reason;
            foreach (var operation in Enum.GetValues<AiTaskKind>())
                if (operation.DisplayName() == command.Title)
                    return AiAvailability(operation).Reason ?? "Wait for the current AI operation to finish.";
            return command.Title switch
            {
                "Undo" => "There are no earlier edits.", "Redo" => "There are no edits to redo.",
                "Invert Layer Mask" or "Link Layer Mask" or "Unlink Layer Mask" or "Apply Layer Mask" when session?.ActiveLayer?.Mask == null => "Select a layer with a mask.",
                "Link Layer Mask" => "The layer mask is already linked.", "Unlink Layer Mask" => "The layer mask is already unlinked.",
                "Apply Layer Mask" => "Select a pixel layer with a mask.", "Add Layer Mask" => "Select a layer without a mask.",
                "Merge Down" => "Select layers to merge, or a layer with another below it.", "Ungroup" => "Select a layer group.",
                "Deselect" or "Feather" or "Expand" or "Contract" or "Paste Into" => "Make a selection first.",
                _ when session?.ActiveLayer == null => "Select a layer first.",
                _ => "Unavailable for the current editing target."
            };
        }
        var selected = await new CommandSearchWindow(choices, CanRun, Reason).ShowDialog<Shortcut?>(this);
        if (selected == null || !CanRun(selected)) return;
        canvas.Focus(); Execute(selected);
    }
}
