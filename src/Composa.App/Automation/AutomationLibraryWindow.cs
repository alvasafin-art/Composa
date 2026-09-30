using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Composa.App.Dialogs;

namespace Composa.App.Automation;

public sealed class AutomationLibraryWindow : Window
{
    public AutomationLibraryWindow(AutomationCatalog catalog, Settings settings, Action changed,
        Action<AutomationCommand> run, Action<AutomationCommand> edit, Action create)
    {
        Title = "Scripts & Plugins"; Width = 680; Height = 620; MinWidth = 440; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var body = new StackPanel { Spacing = 10 };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Palette.Secondary };
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[] { Ui.TextButton("New Script", create), Ui.TextButton("Import Script…", () => _ = Import()),
            Ui.TextButton("Install Plugin…", () => _ = Install()), Ui.TextButton("Refresh", Refresh) })
        { button.Margin = new Thickness(0, 0, 8, 6); actions.Children.Add(button); }
        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(actions, Dock.Top); root.Children.Add(actions); DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        root.Children.Add(new ScrollViewer { Content = body }); Content = root; Refresh();
        void Refresh()
        {
            changed(); body.Children.Clear();
            body.Children.Add(Ui.Label("Commands · shortcuts can be assigned in Help > Keyboard Shortcuts", Palette.Secondary));
            if (catalog.Commands.Count == 0) body.Children.Add(Ui.Label("No scripts or enabled plugin commands yet."));
            foreach (var command in catalog.Commands)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 8 };
                row.Children.Add(new TextBlock { Text = command.Title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                var editButton = Ui.TextButton(command.PluginId == null ? "Edit" : "View / Copy", () =>
                { try { edit(command); } catch (Exception error) { status.Text = error.Message; } });
                Grid.SetColumn(editButton, 1); row.Children.Add(editButton);
                var runButton = Ui.TextButton("Run", () => run(command)); Grid.SetColumn(runButton, 2); row.Children.Add(runButton);
                if (command.PluginId == null)
                {
                    var removeButton = Ui.TextButton("Remove", () => _ = RemoveScript(command)); Grid.SetColumn(removeButton, 3); row.Children.Add(removeButton);
                }
                body.Children.Add(row);
            }
            body.Children.Add(Ui.Label("Installed Plugins", weight: FontWeight.SemiBold));
            foreach (var plugin in catalog.Plugins)
            {
                var checkbox = Ui.Check(plugin.Manifest.Name + " · " + plugin.Manifest.Version, !settings.DisabledScriptPlugins.Contains(plugin.Manifest.Id), enabled =>
                {
                    settings.DisabledScriptPlugins.Remove(plugin.Manifest.Id); if (!enabled) settings.DisabledScriptPlugins.Add(plugin.Manifest.Id);
                    settings.Save(); Refresh();
                });
                var remove = Ui.TextButton("Remove", () => _ = Remove(plugin));
                body.Children.Add(Ui.Row(8, checkbox, remove));
            }
            status.Text = catalog.Errors.Count > 0 ? string.Join("\n", catalog.Errors) :
                "Plugins run only when you choose a command. JavaScript only; no native modules or arbitrary file reads. Removed scripts/plugins are archived for recovery.";
        }
        async Task Import()
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            { Title = "Import Script", FileTypeFilter = [new FilePickerFileType("JavaScript") { Patterns = ["*.js"] }] });
            if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
            try { catalog.ImportScript(path); Refresh(); } catch (Exception error) { status.Text = error.Message; }
        }
        async Task Install()
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            { Title = "Choose plugin.json", FileTypeFilter = [new FilePickerFileType("Plugin manifest") { Patterns = ["plugin.json"] }] });
            if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
            try
            {
                var manifest = AutomationCatalog.Validate(path);
                if (!await Prompts.Confirm(this, "Install Plugin", $"Install {manifest.Name} ({manifest.Version}), {manifest.Commands.Count} commands?\nOnly install scripts from an author you trust.\n" +
                    (manifest.Permissions.Contains("export") ? "This plugin requests permission to export files." : "No filesystem export permission requested."))) return;
                catalog.Install(path); settings.DisabledScriptPlugins.Remove(manifest.Id); settings.Save(); Refresh();
            }
            catch (Exception error) { status.Text = error.Message; }
        }
        async Task Remove(InstalledScriptPlugin plugin)
        {
            if (!await Prompts.Confirm(this, "Remove Plugin", $"Remove {plugin.Manifest.Name}? Its package will be moved to the automation archive, not erased.")) return;
            try { catalog.RemovePlugin(plugin.Manifest.Id); settings.DisabledScriptPlugins.Remove(plugin.Manifest.Id); settings.Save(); Refresh(); }
            catch (Exception error) { status.Text = error.Message; }
        }
        async Task RemoveScript(AutomationCommand command)
        {
            if (!await Prompts.Confirm(this, "Remove Script", $"Move {command.Title} to the automation archive? It can be imported again for recovery.")) return;
            try { catalog.RemoveScript(command.Id); Refresh(); }
            catch (Exception error) { status.Text = error.Message; }
        }
    }
}
