using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Composa.App.Automation;
using Composa.App.Dialogs;

namespace Composa.App;

public sealed partial class MainWindow
{
    private readonly AutomationCatalog automation;
    private MenuItem? scriptsMenu, pluginsMenu;
    private bool automationRunning;
    private CancellationTokenSource? automationCancellation;
    internal AutomationCatalog Automation => automation;

    internal void ReloadAutomation()
    {
        automation.Reload(settings.DisabledScriptPlugins);
        commands.RemoveAll(command => command.Id.StartsWith("automation:", StringComparison.Ordinal));
        menuItems.RemoveAll(item => item.Command.Id.StartsWith("automation:", StringComparison.Ordinal));
        if (scriptsMenu == null || pluginsMenu == null) return;
        scriptsMenu.Items.Clear(); pluginsMenu.Items.Clear();
        void ActionItem(MenuItem menu, string title, Action action)
        { var item = new MenuItem { Header = title }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        ActionItem(scriptsMenu, "Script Editor…", () => ShowScriptEditor());
        ActionItem(scriptsMenu, "Run Script File…", () => _ = PickScriptToRun());
        ActionItem(scriptsMenu, "Scripts & Plugins…", ShowAutomationLibrary);
        ActionItem(pluginsMenu, "Manage / Install Plugins…", ShowAutomationLibrary);
        scriptsMenu.Items.Add(new Separator()); pluginsMenu.Items.Add(new Separator());
        foreach (var entry in automation.Commands)
        {
            var shortcut = new Shortcut("automation:" + entry.Id, entry.Title, entry.PluginId == null ? "Scripts" : "Plugins", null,
                () => _ = RunAutomationCommand(entry), () => session != null && !automationRunning && !session.IsInteracting && !canvas.IsDragging);
            var item = new MenuItem { Header = entry.Title };
            item.Click += (_, _) => Execute(shortcut); shortcut.Item = item;
            commands.Add(shortcut); menuItems.Add((item, shortcut));
            (entry.PluginId == null ? scriptsMenu : pluginsMenu).Items.Add(item);
        }
        ApplyShortcutOverrides();
    }

    private async Task PickScriptToRun()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = "Run JavaScript", FileTypeFilter = [new FilePickerFileType("JavaScript") { Patterns = ["*.js"] }] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        try { ShowScriptEditor(AutomationCatalog.ReadScript(path), Path.GetFileNameWithoutExtension(path)); }
        catch (Exception error) { ShowProblem(error.Message); }
    }

    private async Task RunAutomationCommand(AutomationCommand command)
    {
        Window? progress = null;
        try
        {
            var execution = RunAutomationScriptAsync(AutomationCatalog.ReadScript(command.File), command.Title, allowExport: command.AllowExport);
            if (!execution.IsCompleted && !OwnedWindows.OfType<DialogWindow>().Any(window => window.IsVisible))
            {
                progress = new Window { Title = command.Title, Width = 440, Height = 140, MinWidth = 300, MinHeight = 140,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
                progress.Content = new StackPanel { Margin = new Avalonia.Thickness(12), Spacing = 10, Children =
                { Ui.Label("Running script / AI tasks…"), new ProgressBar { IsIndeterminate = true, Height = 6 },
                    Ui.TextButton("Stop", () => automationCancellation?.Cancel()) } };
                progress.Closing += (_, _) => automationCancellation?.Cancel(); progress.Show(this);
            }
            await execution;
            note = command.Title + " · completed (Ctrl+Z to undo)";
        }
        catch (OperationCanceledException) { note = "Script cancelled; changes rolled back."; }
        catch (Exception error) { ShowProblem(command.Title + ": " + error.Message); }
        finally { progress?.Close(); UpdateStatus(); }
    }

    internal async Task<ScriptResult> RunAutomationScriptAsync(string script, string title, CancellationToken token = default, bool allowExport = true, Window? dialogOwner = null)
    {
        if (session is not { } target) throw new InvalidOperationException("Open a document before running a script.");
        if (automationRunning || target.IsInteracting || canvas.IsDragging) throw new InvalidOperationException("Finish the current edit before running a script.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        automationCancellation = cancellation; automationRunning = true;
        var content = Content as Control;
        if (content != null) content.IsEnabled = false;
        try
        {
            var result = await scriptRuntime.ExecuteAsync(target, script, aiTasks, settings, title, cancellation.Token, allowExport: allowExport, dialogs: new ScriptDialogs(dialogOwner ?? this));
            if (result.SendToPhotoshop) await SendDocumentToPhotoshop(target, cancellation.Token);
            if (result.SendLayersToPhotoshop) await SendDocumentToPhotoshop(target, cancellation.Token, layers: true);
            return result;
        }
        finally
        {
            automationRunning = false; automationCancellation = null;
            if (content != null) content.IsEnabled = true;
            canvas.InvalidateVisual(); RefreshAiUi(); UpdateStatus();
        }
    }

    internal void ShowScriptEditor(string? script = null, string name = "Script", string? libraryPath = null)
    {
        ScriptEditorWindow? editor = null;
        editor = new ScriptEditorWindow(automation, script ?? "app.activeDocument.addRectangle(40, 40, 240, 120, '#87CEEB', 'Blue Rectangle');", name, libraryPath,
            (text, token) => RunAutomationScriptAsync(text, "Script: " + name, token, dialogOwner: editor), ReloadAutomation);
        editor.Show(this);
    }

    private void ShowAutomationLibrary()
    {
        var library = new AutomationLibraryWindow(automation, settings, ReloadAutomation,
            command => _ = RunAutomationCommand(command), command => ShowScriptEditor(AutomationCatalog.ReadScript(command.File), command.Title,
                command.PluginId == null ? command.File : null), () => ShowScriptEditor());
        library.Show(this);
    }
}
