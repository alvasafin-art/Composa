using Composa.App.Dialogs;

namespace Composa.App;

public sealed partial class MainWindow
{
    private async Task FindCommand()
    {
        RefreshMenuState();
        var choices = AllShortcuts.Where(c => c.Id != FindCommandId).ToArray();
        bool CanRun(Shortcut command) => !IsDragging && (command.Group != "Tools and Canvas" || HasDocument) && (command.Enabled?.Invoke() ?? true);
        var selected = await new CommandSearchWindow(choices, CanRun).ShowDialog<Shortcut?>(this);
        if (selected == null || !CanRun(selected)) return;
        canvas.Focus(); Execute(selected);
    }
}
