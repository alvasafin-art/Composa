using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Composa.AI;
using Composa.App.AI;
using Composa.App.Dialogs;
using Composa.Editing;
using SkiaSharp;

namespace Composa.App;

public sealed partial class MainWindow
{
    private readonly StackPanel aiActionHost = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly StackPanel aiProgressHost = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private ComboBox? aiModel;
    private ComboBox? aiResolution;

    private Control BuildAiContextBar()
    {
        var profiles = aiTasks.Engines.Profiles.Count == 0 ? ["No Engine Pack"] : aiTasks.Engines.Profiles.Select(profile => profile.DisplayName).ToArray();
        aiModel = Ui.Combo(profiles, aiTasks.SelectedEngine?.DisplayName ?? profiles[0], name => name, name =>
        {
            aiTasks.SelectedEngine = aiTasks.Engines.Profiles.FirstOrDefault(profile => profile.DisplayName == name);
            settings.AiEngineId = aiTasks.SelectedEngine?.Id;
            settings.Save();
            RefreshAiUi();
        }, 210);
        aiModel.MaxWidth = 210;
        aiModel.IsEnabled = aiTasks.Engines.Profiles.Count > 0;
        aiResolution = Ui.Combo(new[] { "Document size", "512 × 512", "768 × 768", "1024 × 1024", "1024 × 1536", "1536 × 1024" }, settings.AiResolution,
            value => value, value => { settings.AiResolution = value; settings.Save(); }, 140);
        var settingsButton = Ui.TextButton("⚙", () => _ = ShowAiSettings());
        settingsButton.MinWidth = 32;
        ToolTip.SetTip(settingsButton, "AI and ComfyUI settings");
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,*,Auto"), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Ui.Label("AI", weight: FontWeight.SemiBold));
        AddAt(row, Ui.Row(5, Ui.Label("Model", Palette.Secondary), aiModel), 1).Margin = new Thickness(14, 0, 0, 0);
        AddAt(row, Ui.Row(5, Ui.Label("Resolution", Palette.Secondary), aiResolution), 2).Margin = new Thickness(12, 0, 0, 0);
        AddAt(row, settingsButton, 3).Margin = new Thickness(6, 0, 12, 0);
        AddAt(row, aiActionHost, 4);
        AddAt(row, aiProgressHost, 5);
        aiContextHost.Child = row;
        RefreshAiUi();
        return aiContextHost;
    }

    private bool CropExpands => session?.Tool == Tool.Crop && canvas.CropRect is { } crop &&
        (crop.Left < 0 || crop.Top < 0 || crop.Right > session.Document.Width || crop.Bottom > session.Document.Height);

    private bool CanRunAi(AiTaskKind task) =>
        AiTaskAvailability.Resolve(session, aiTasks.SelectedEngine, task, CropExpands).Available && aiTasks.Operation?.Status is not (AiOperationStatus.Queued or AiOperationStatus.Running);

    private void RefreshAiUi()
    {
        if (aiContextHost.Child == null) return;
        aiActionHost.Children.Clear();
        var contextual = new List<AiTaskKind>();
        if (session?.Selection != null) contextual.AddRange([AiTaskKind.GenerativeFill, AiTaskKind.RemoveObject]);
        else if (session?.ActiveLayer?.Pixels is { } pixels && IsEmpty(pixels)) contextual.Add(AiTaskKind.GenerateImage);
        if (CropExpands) contextual.Add(AiTaskKind.GenerativeExpand);
        foreach (var task in contextual.Distinct())
        {
            var availability = AiTaskAvailability.Resolve(session, aiTasks.SelectedEngine, task, CropExpands);
            var button = Ui.TextButton(task.DisplayName(), () => _ = RunAi(task), accent: task is AiTaskKind.GenerativeFill or AiTaskKind.GenerateImage);
            button.MinWidth = 0;
            button.IsEnabled = availability.Available && aiTasks.Operation?.Status is not (AiOperationStatus.Queued or AiOperationStatus.Running);
            if (availability.Reason != null) ToolTip.SetTip(button, availability.Reason);
            aiActionHost.Children.Add(button);
        }
        if (session != null) aiActionHost.Children.Add(BuildAiTaskMenu());
        if (contextual.Count == 0)
            aiActionHost.Children.Add(Ui.Label(aiTasks.SelectedEngine == null ? "Install an Engine Pack to enable generation" : "Select an area for fill or removal", Palette.Secondary));

        aiProgressHost.Children.Clear();
        var operation = aiTasks.Operation;
        var active = operation?.Status is AiOperationStatus.Queued or AiOperationStatus.Running;
        if (operation != null)
        {
            var label = Ui.Label(operation.Error ?? operation.Stage ?? operation.Status.ToString(), operation.Status == AiOperationStatus.Failed ? Brushes.Orange : Palette.Secondary);
            label.MaxWidth = 190;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            aiProgressHost.Children.Add(label);
            if (active)
            {
                aiProgressHost.Children.Add(new ProgressBar
                {
                    Width = 110, Height = 6, IsIndeterminate = operation.IsIndeterminate,
                    Minimum = 0, Maximum = operation.Maximum ?? 1, Value = operation.Value ?? 0
                });
                var cancel = Ui.TextButton("Cancel", aiTasks.Cancel);
                cancel.MinWidth = 0;
                aiProgressHost.Children.Add(cancel);
            }
        }
        aiTaskText.IsVisible = active;
        aiTaskText.Text = active ? "AI: " + (operation?.Stage ?? "Working") : "";
    }

    private Button BuildAiTaskMenu()
    {
        Button? button = null;
        button = Ui.TextButton("AI ▾", () =>
        {
            var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            foreach (var task in Enum.GetValues<AiTaskKind>())
            {
                var availability = AiTaskAvailability.Resolve(session, aiTasks.SelectedEngine, task, CropExpands);
                var item = new MenuItem
                {
                    Header = task.DisplayName(),
                    IsEnabled = availability.Available && aiTasks.Operation?.Status is not (AiOperationStatus.Queued or AiOperationStatus.Running)
                };
                if (availability.Reason != null) ToolTip.SetTip(item, availability.Reason);
                item.Click += (_, _) => _ = RunAi(task);
                flyout.Items.Add(item);
            }
            flyout.ShowAt(button!);
        });
        button.MinWidth = 0;
        ToolTip.SetTip(button, "More AI tasks");
        return button;
    }

    private async Task ShowAiSettings()
    {
        try
        {
            if (!await AiDialogs.SettingsDialog(this, settings, aiTasks)) return;
            aiTasks.ConnectionTimeoutSeconds = settings.ComfyConnectionTimeoutSeconds;
            aiTasks.SelectedEngine = aiTasks.Engines.Find(settings.AiEngineId);
            if (aiModel != null)
            {
                aiModel.SelectedItem = aiTasks.SelectedEngine?.DisplayName;
                aiModel.PlaceholderText = aiTasks.SelectedEngine?.DisplayName ?? "No Engine Pack";
            }
            RefreshAiUi();
        }
        catch (Exception error) { ShowProblem(error.Message); }
    }

    private async Task EditLayerTags(Composa.Model.Layer layer)
    {
        if (session == null) return;
        if (await AiDialogs.LayerTags(this, layer, settings) is { } tags) session.SetLayerTags(layer, tags);
    }

    private async Task RunAi(AiTaskKind task, string initialPrompt = "")
    {
        var availability = AiTaskAvailability.Resolve(session, aiTasks.SelectedEngine, task, CropExpands);
        if (!availability.Available) { ShowProblem(availability.Reason ?? "This AI task is unavailable."); return; }
        if (aiTasks.Operation?.Status is AiOperationStatus.Queued or AiOperationStatus.Running) { ShowProblem("Another AI operation is already running."); return; }
        var fallback = ParseResolution(settings.AiResolution, session?.Document.Width ?? 1024, session?.Document.Height ?? 1024);
        AiPromptResult? options;
        if (task is AiTaskKind.RemoveObject or AiTaskKind.SelectSubject or AiTaskKind.ObjectSelection or AiTaskKind.Upscale)
            options = new("", fallback.Width, fallback.Height, settings.AiSeed);
        else options = await AiDialogs.Prompt(this, task, settings, session?.Document.Width ?? fallback.Width, session?.Document.Height ?? fallback.Height, initialPrompt);
        if (options == null) return;
        if (session == null)
        {
            if (task != AiTaskKind.GenerateImage) return;
            AddSession(EditorSession.NewCanvas(options.Width, options.Height));
        }
        var seed = options.Seed < 0 ? Random.Shared.NextInt64(long.MaxValue) : options.Seed;
        var request = new AiTaskRequest
        {
            Task = task,
            Prompt = options.Prompt,
            ExpansionBounds = task == AiTaskKind.GenerativeExpand && canvas.CropRect is { } crop
                ? new SKRectI((int)Math.Floor(crop.Left), (int)Math.Floor(crop.Top), (int)Math.Ceiling(crop.Right), (int)Math.Ceiling(crop.Bottom)) : null,
            Settings = new AiGenerationSettings { Width = options.Width, Height = options.Height, Seed = seed }
        };
        try { await aiTasks.RunAsync(new EditorCommandService(session!), request); }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowProblem(error.Message); }
    }

    private static (int Width, int Height) ParseResolution(string value, int fallbackWidth, int fallbackHeight)
    {
        if (value == "Document size") return (fallbackWidth, fallbackHeight);
        var parts = value.Replace('×', 'x').Split('x', StringSplitOptions.TrimEntries);
        return parts.Length == 2 && int.TryParse(parts[0], out var width) && int.TryParse(parts[1], out var height)
            ? (width, height) : (fallbackWidth, fallbackHeight);
    }

    private static bool IsEmpty(SKBitmap bitmap) => bitmap.GetPixelSpan().IndexOfAnyExcept((byte)0) < 0;
}
