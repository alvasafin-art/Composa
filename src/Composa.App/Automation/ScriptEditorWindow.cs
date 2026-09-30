using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Composa.App.Automation;

public sealed class ScriptEditorWindow : Window
{
    private readonly TextBox code;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox name;
    private CancellationTokenSource? running;
    private string? libraryPath;
    internal TextBox Code => code;

    public ScriptEditorWindow(AutomationCatalog catalog, string script, string title, string? path,
        Func<string, CancellationToken, Task<ScriptResult>> execute, Action saved)
    {
        libraryPath = path;
        Title = "Script Editor"; Width = 760; Height = 620; MinWidth = 420; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        code = new TextBox { Text = script, AcceptsReturn = true, AcceptsTab = true, FontFamily = FontFamily.Parse("monospace"), TextWrapping = TextWrapping.NoWrap };
        name = new TextBox { Text = title, Watermark = "Script name", IsReadOnly = path != null };
        Button run = null!, stop = null!, save = null!, export = null!;
        run = Ui.TextButton("Run", () => _ = Run(), accent: true);
        stop = Ui.TextButton("Stop", () => running?.Cancel()); stop.IsVisible = false;
        save = Ui.TextButton("Save to Library", () =>
        {
            try { libraryPath = catalog.SaveScript(name.Text ?? "Script", code.Text ?? "", libraryPath); saved(); status.Text = "Saved in the Scripts menu. Assign a key in Keyboard Shortcuts."; }
            catch (Exception error) { status.Text = error.Message; }
        });
        export = Ui.TextButton("Save .js…", () => _ = SaveFile());
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal }; foreach (var button in new[] { save, export, stop, run }) { button.Margin = new Thickness(0, 0, 8, 0); buttons.Children.Add(button); }
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), RowSpacing = 8, Margin = new Thickness(12) };
        root.Children.Add(name); Grid.SetRow(code, 1); root.Children.Add(code); Grid.SetRow(status, 2); root.Children.Add(status); Grid.SetRow(buttons, 3); root.Children.Add(buttons); Content = root;
        Closed += (_, _) => running?.Cancel();
        async Task Run()
        {
            if (running != null) return;
            running = new CancellationTokenSource(); run.IsEnabled = save.IsEnabled = export.IsEnabled = code.IsEnabled = false; stop.IsVisible = true;
            status.Text = "Running…";
            try { await execute(code.Text ?? "", running.Token); status.Text = "Completed. Ctrl+Z undoes the whole script."; }
            catch (OperationCanceledException) { status.Text = "Stopped. Document changes were rolled back."; }
            catch (Exception error) { status.Text = "Not applied: " + error.Message; }
            finally { running.Dispose(); running = null; run.IsEnabled = save.IsEnabled = export.IsEnabled = code.IsEnabled = true; stop.IsVisible = false; }
        }
        async Task SaveFile()
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            { Title = "Save Script", SuggestedFileName = name.Text + ".js", DefaultExtension = "js", FileTypeChoices = [new FilePickerFileType("JavaScript") { Patterns = ["*.js"] }] });
            if (file?.TryGetLocalPath() is not { } target) return;
            try { await File.WriteAllTextAsync(target, code.Text ?? ""); status.Text = "Saved " + Path.GetFileName(target); }
            catch (Exception error) { status.Text = error.Message; }
        }
    }
}
