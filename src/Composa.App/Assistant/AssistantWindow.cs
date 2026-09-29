using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Composa.AI;
using Composa.App.AI;
using Composa.App.Automation;
using Composa.App.Dialogs;
using Composa.Editing;

namespace Composa.App.Assistant;

public sealed class AssistantWindow : Window
{
    private readonly MainWindow owner;
    private readonly Func<EditorSession?> session;
    private readonly LlamaAssistantProvider provider;
    private readonly LlamaServerHost server;
    private readonly JavaScriptRuntime runtime;
    private readonly AiTaskService aiTasks;
    private readonly Settings settings;
    private readonly TextBox request = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 90, PlaceholderText = "Describe what you want to change" };
    private readonly TextBox summary = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 54 };
    private readonly TextBox script = new() { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = FontFamily.Parse("Consolas"), Height = 230 };
    private readonly TextBlock status = Ui.Label("Ready", Palette.Secondary);
    private readonly Button plan, apply, save, cancel;
    private readonly Action serverChanged;
    private CancellationTokenSource? running;

    public AssistantWindow(MainWindow owner, Func<EditorSession?> session, Settings settings, LlamaServerHost server, JavaScriptRuntime runtime, AiTaskService aiTasks)
    {
        this.owner = owner; this.session = session; this.settings = settings; this.server = server; this.runtime = runtime; this.aiTasks = aiTasks;
        provider = new LlamaAssistantProvider(settings, server);
        Title = "Assistant"; Width = 680; Height = 650; MinWidth = 520; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        plan = Ui.TextButton("Plan", () => _ = Plan(), accent: true);
        apply = Ui.TextButton("Apply", () => _ = Apply()); apply.IsEnabled = false;
        save = Ui.TextButton("Save as Script…", () => _ = SaveScript()); save.IsEnabled = false;
        var settingsButton = Ui.TextButton("Settings…", () => _ = SettingsDialog());
        cancel = Ui.TextButton("Cancel", Cancel); cancel.IsEnabled = false;
        serverChanged = () => Avalonia.Threading.Dispatcher.UIThread.Post(() => status.Text = server.Status);
        server.StatusChanged += serverChanged;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(settingsButton); buttons.Children.Add(cancel); buttons.Children.Add(save); buttons.Children.Add(apply); buttons.Children.Add(plan);
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto"), RowSpacing = 8, Margin = new Thickness(16) };
        body.Children.Add(Ui.Label("Task", weight: FontWeight.SemiBold));
        Add(body, request, 1);
        Add(body, Ui.Label("Assistant plan", weight: FontWeight.SemiBold), 2);
        var results = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), RowSpacing = 8 };
        results.Children.Add(summary); Add(results, Ui.Label("JavaScript — review before applying", Palette.Secondary), 1); Add(results, script, 2);
        Add(body, results, 3); Add(body, status, 4); Add(body, buttons, 5);
        Content = body;
        Closed += (_, _) => { running?.Cancel(); server.StatusChanged -= serverChanged; };
        Opened += (_, _) => request.Focus();
    }

    private static void Add(Grid grid, Control control, int row) { Grid.SetRow(control, row); grid.Children.Add(control); }

    private async Task Plan()
    {
        if (string.IsNullOrWhiteSpace(request.Text)) return;
        running?.Cancel(); running = new CancellationTokenSource();
        plan.IsEnabled = false; cancel.IsEnabled = true; apply.IsEnabled = save.IsEnabled = false;
        status.Text = "Preparing Assistant…"; status.Foreground = Palette.Secondary;
        try
        {
            var context = session() is { } current ? JavaScriptRuntime.Describe(current) : "{\"document\":null}";
            var result = await provider.PlanAsync(new AssistantRequest(request.Text!, context, JavaScriptRuntime.Reference), running.Token);
            summary.Text = result.Summary;
            script.Text = result.Script;
            apply.IsEnabled = session() != null && !string.IsNullOrWhiteSpace(result.Script);
            save.IsEnabled = !string.IsNullOrWhiteSpace(result.Script);
            status.Text = apply.IsEnabled ? "Review the script, then choose Apply." : "Completed without document changes.";
        }
        catch (OperationCanceledException) { status.Text = "Cancelled"; }
        catch (Exception error) { status.Text = "Error: " + error.Message; status.Foreground = Brushes.Orange; }
        finally { plan.IsEnabled = true; cancel.IsEnabled = false; }
    }

    private async Task Apply()
    {
        if (session() is not { } current || string.IsNullOrWhiteSpace(script.Text)) return;
        running?.Cancel(); running = new CancellationTokenSource();
        apply.IsEnabled = plan.IsEnabled = false; cancel.IsEnabled = true;
        var editorContent = owner.Content as Control;
        if (editorContent != null) editorContent.IsEnabled = false;
        try
        {
            var title = string.IsNullOrWhiteSpace(summary.Text) ? "Assistant edit" : "Assistant: " + summary.Text.Trim();
            var result = await runtime.ExecuteAsync(current, script.Text, aiTasks, settings, title.Length > 120 ? title[..120] : title, running.Token);
            status.Text = result.ExportedPath == null ? "Applied as one Undo step." : "Applied and exported to " + result.ExportedPath;
            status.Foreground = Palette.Accent;
            owner.Canvas.InvalidateVisual();
        }
        catch (OperationCanceledException) { status.Text = "Cancelled"; status.Foreground = Palette.Secondary; }
        catch (Exception error) { status.Text = "Script error: " + error.Message; status.Foreground = Brushes.Orange; }
        finally
        {
            if (editorContent != null) editorContent.IsEnabled = true;
            apply.IsEnabled = true; plan.IsEnabled = true; cancel.IsEnabled = false;
        }
    }

    private async Task SaveScript()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Composa Script", SuggestedFileName = "composa-script.js", DefaultExtension = "js",
            FileTypeChoices = [new FilePickerFileType("JavaScript") { Patterns = ["*.js"] }]
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        await File.WriteAllTextAsync(path, script.Text ?? "");
        status.Text = "Saved " + Path.GetFileName(path);
    }

    private async Task SettingsDialog()
    {
        try
        {
            if (await AssistantDialogs.Settings(owner, settings)) status.Text = "Settings saved.";
        }
        catch (Exception error) { status.Text = "Settings error: " + error.Message; status.Foreground = Brushes.Orange; }
    }

    private void Cancel() => running?.Cancel();
}

public static class AssistantDialogs
{
    public static async Task<bool> Settings(Window owner, Settings settings)
    {
        var url = new TextBox { Text = settings.AssistantServerUrl, Width = 360 };
        var executable = new TextBox { Text = settings.AssistantServerExecutable, Width = 360 };
        var model = new TextBox { Text = settings.AssistantModelPath, Width = 360 };
        async Task Pick(TextBox target, string title, string pattern)
        {
            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title, AllowMultiple = false, FileTypeFilter = [new FilePickerFileType(title) { Patterns = [pattern] }]
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) target.Text = path;
        }
        var exeRow = Ui.Row(6, executable, Ui.TextButton("Browse…", () => _ = Pick(executable, "llama-server", "*.exe")));
        var modelRow = Ui.Row(6, model, Ui.TextButton("Browse…", () => _ = Pick(model, "GGUF model", "*.gguf")));
        var context = Ui.Number(settings.AssistantContextSize, 2048, 131072, _ => { }, 1024, "0", 110);
        var tokens = Ui.Number(settings.AssistantMaxTokens, 256, 8192, _ => { }, 128, "0", 110);
        var auto = Ui.Check("Start the local server when needed", settings.AssistantAutoStart, value => settings.AssistantAutoStart = value);
        var form = CanvasDialogs.Form(("Server URL", url), ("llama-server", exeRow), ("Model", modelRow), ("Context", context), ("Maximum reply", tokens), ("", auto));
        if (!await new DialogWindow("Assistant Settings", form).Ask(owner)) return false;
        if (!Uri.TryCreate(url.Text, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https")) throw new FormatException("Enter a valid HTTP server URL.");
        settings.AssistantServerUrl = address.ToString().TrimEnd('/');
        settings.AssistantServerExecutable = executable.Text?.Trim() ?? "";
        settings.AssistantModelPath = model.Text?.Trim() ?? "";
        settings.AssistantContextSize = (int)(context.Value ?? 16384);
        settings.AssistantMaxTokens = (int)(tokens.Value ?? 1536);
        settings.Save();
        return true;
    }
}
