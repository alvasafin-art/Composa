using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Composa.AI;
using Composa.App.AI;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.IO;
using Composa.Selections;
using SkiaSharp;

namespace Composa.App;

public sealed partial class MainWindow
{
    internal AiTaskService AiTasks => aiTasks;
    private readonly StackPanel aiActionHost = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly StackPanel aiProgressHost = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private ComboBox? aiMegapixels;
    private readonly Canvas aiFloatingLayer = new();
    private readonly Border aiFloatingHost = new()
    {
        IsVisible = false, Width = 520, Padding = new Thickness(12), CornerRadius = new CornerRadius(8),
        Background = new SolidColorBrush(Color.Parse("#292929")), BorderBrush = new SolidColorBrush(Color.Parse("#4A4A4A")),
        BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
    };
    private readonly TextBox aiFloatingPrompt = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 54, MaxHeight = 90, PlaceholderText = "Describe what you want to create or edit" };
    private readonly StackPanel aiReferenceHost = new() { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
    private string? aiReferencePath;
    private Avalonia.Media.Imaging.Bitmap? aiReferencePreview;
    private bool aiFloatingDismissed;
    private ComboBox? aiFloatingMegapixels;
    private Button? aiFloatingGenerate, aiFloatingRemove, aiFloatingMore;

    private Control BuildAiContextBar()
    {
        var selectedMegapixels = AiDimensions.MegapixelOptions.MinBy(option => Math.Abs(option - settings.AiMegapixels));
        aiMegapixels = Ui.Combo(AiDimensions.MegapixelOptions, selectedMegapixels, AiDimensions.Label,
            value => { settings.AiMegapixels = value; settings.Save(); RefreshAiUi(); }, 92);
        var settingsButton = Ui.TextButton("⚙", () => _ = ShowAiSettings());
        settingsButton.MinWidth = 32;
        ToolTip.SetTip(settingsButton, "AI and ComfyUI settings");
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto"), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Ui.Label("AI", weight: FontWeight.SemiBold));
        AddAt(row, Ui.Row(5, Ui.Label("Size", Palette.Secondary), aiMegapixels), 1).Margin = new Thickness(14, 0, 0, 0);
        AddAt(row, settingsButton, 2).Margin = new Thickness(6, 0, 12, 0);
        AddAt(row, aiActionHost, 3);
        AddAt(row, aiProgressHost, 4);
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
        RefreshAiFloatingUi();
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

    private Control BuildAiFloatingMenu()
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        header.Children.Add(Ui.Label("Generative AI", weight: FontWeight.SemiBold));
        var advanced = Ui.TextButton("Advanced…", () => _ = ShowAiSettings());
        advanced.MinWidth = 0;
        AddAt(header, advanced, 1).Margin = new Thickness(0, 0, 5, 0);
        var close = Ui.TextButton("×", () => { aiFloatingDismissed = true; RefreshAiFloatingUi(); });
        close.MinWidth = 32;
        ToolTip.SetTip(close, "Close until the selection changes");
        AddAt(header, close, 2);

        var chosen = AiDimensions.MegapixelOptions.MinBy(option => Math.Abs(option - settings.AiMegapixels));
        aiFloatingMegapixels = Ui.Combo(AiDimensions.MegapixelOptions, chosen, AiDimensions.Label, value =>
        {
            settings.AiMegapixels = value;
            settings.Save();
            RefreshAiUi();
        }, 90);
        aiFloatingGenerate = Ui.TextButton("Generate", () => _ = RunAi(AiTaskKind.GenerativeFill, aiFloatingPrompt.Text ?? "", aiReferencePath, useInlinePrompt: true), accent: true);
        aiFloatingRemove = Ui.TextButton("Remove", () => _ = RunAi(AiTaskKind.RemoveObject, referencePath: aiReferencePath, useInlinePrompt: true));
        aiFloatingMore = Ui.TextButton("••• ▾", OpenAiFloatingTaskMenu);
        aiFloatingMore.MinWidth = 54;
        ToolTip.SetTip(aiFloatingMore, "More AI operations");
        var actions = Ui.Row(7, Ui.Label("Size", Palette.Secondary), aiFloatingMegapixels, aiFloatingGenerate, aiFloatingRemove, aiFloatingMore);
        RefreshAiReferenceUi();
        return Ui.Column(9, header, aiFloatingPrompt, aiReferenceHost, actions);
    }

    private void OpenAiFloatingTaskMenu()
    {
        if (aiFloatingMore == null) return;
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var task in Enum.GetValues<AiTaskKind>().Where(task => task is not (AiTaskKind.GenerativeFill or AiTaskKind.RemoveObject)))
        {
            var availability = AiTaskAvailability.Resolve(session, aiTasks.SelectedEngine, task, CropExpands);
            var item = new MenuItem
            {
                Header = task.DisplayName(),
                IsEnabled = availability.Available && aiTasks.Operation?.Status is not (AiOperationStatus.Queued or AiOperationStatus.Running)
            };
            if (availability.Reason != null) ToolTip.SetTip(item, availability.Reason);
            item.Click += (_, _) => _ = RunAi(task, aiFloatingPrompt.Text ?? "", aiReferencePath, useInlinePrompt: true);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(aiFloatingMore);
    }

    private void RefreshAiFloatingUi()
    {
        var visible = session?.Selection != null && aiTasks.SelectedEngine != null && !aiFloatingDismissed;
        aiFloatingHost.IsVisible = visible;
        if (!visible) return;
        var busy = aiTasks.Operation?.Status is AiOperationStatus.Queued or AiOperationStatus.Running;
        if (aiFloatingGenerate != null) aiFloatingGenerate.IsEnabled = !busy && AiTaskAvailability.Resolve(session, aiTasks.SelectedEngine, AiTaskKind.GenerativeFill, CropExpands).Available;
        if (aiFloatingRemove != null) aiFloatingRemove.IsEnabled = !busy && AiTaskAvailability.Resolve(session, aiTasks.SelectedEngine, AiTaskKind.RemoveObject, CropExpands).Available;
        if (aiFloatingMore != null) aiFloatingMore.IsEnabled = !busy && Enum.GetValues<AiTaskKind>().Any(task =>
            task is not (AiTaskKind.GenerativeFill or AiTaskKind.RemoveObject) && AiTaskAvailability.Resolve(session, aiTasks.SelectedEngine, task, CropExpands).Available);
        var index = Array.IndexOf(AiDimensions.MegapixelOptions, AiDimensions.MegapixelOptions.MinBy(option => Math.Abs(option - settings.AiMegapixels)));
        if (aiMegapixels?.SelectedIndex != index) aiMegapixels!.SelectedIndex = index;
        if (aiFloatingMegapixels?.SelectedIndex != index) aiFloatingMegapixels!.SelectedIndex = index;
        Avalonia.Threading.Dispatcher.UIThread.Post(RefreshAiFloatingPosition);
    }

    private void RefreshAiFloatingPosition()
    {
        if (!aiFloatingHost.IsVisible || session?.Selection is not { } selection) return;
        var bounds = SelectionMask.Bounds(selection);
        if (bounds.IsEmpty) return;
        var topLeft = canvas.ToScreen(new SKPoint(bounds.Left, bounds.Top));
        var bottomRight = canvas.ToScreen(new SKPoint(bounds.Right, bounds.Bottom));
        var width = Math.Max(340, Math.Min(520, canvas.Bounds.Width - 16));
        aiFloatingHost.Width = width;
        var height = Math.Max(190, aiFloatingHost.Bounds.Height);
        var maxLeft = Math.Max(8, canvas.Bounds.Width - width - 8);
        var left = Math.Clamp((topLeft.X + bottomRight.X - width) / 2, 8, maxLeft);
        var below = bottomRight.Y + 10;
        var top = below + height <= canvas.Bounds.Height - 8 ? below : topLeft.Y - height - 10;
        top = Math.Clamp(top, 8, Math.Max(8, canvas.Bounds.Height - height - 8));
        Avalonia.Controls.Canvas.SetLeft(aiFloatingHost, left);
        Avalonia.Controls.Canvas.SetTop(aiFloatingHost, top);
    }

    private async Task PickAiReference()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose Reference Image", AllowMultiple = false, FileTypeFilter = [ImageType]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        aiReferencePath = path;
        aiReferencePreview?.Dispose();
        try { aiReferencePreview = new Avalonia.Media.Imaging.Bitmap(path); }
        catch { aiReferencePreview = null; }
        RefreshAiReferenceUi();
    }

    private void RefreshAiReferenceUi()
    {
        aiReferenceHost.Children.Clear();
        aiReferenceHost.Children.Add(Ui.Label("Reference", Palette.Secondary));
        if (aiReferencePath == null)
        {
            var add = Ui.TextButton("+ Add image", () => _ = PickAiReference());
            add.MinWidth = 0;
            aiReferenceHost.Children.Add(add);
            return;
        }
        if (aiReferencePreview != null)
            aiReferenceHost.Children.Add(new Image { Source = aiReferencePreview, Width = 44, Height = 44, Stretch = Stretch.UniformToFill });
        var name = Ui.Label(Path.GetFileName(aiReferencePath));
        name.MaxWidth = 280;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        aiReferenceHost.Children.Add(name);
        var replace = Ui.TextButton("Replace…", () => _ = PickAiReference());
        replace.MinWidth = 0;
        aiReferenceHost.Children.Add(replace);
        var remove = Ui.TextButton("×", () =>
        {
            aiReferencePath = null;
            aiReferencePreview?.Dispose();
            aiReferencePreview = null;
            RefreshAiReferenceUi();
        });
        remove.MinWidth = 32;
        aiReferenceHost.Children.Add(remove);
    }

    private async Task ShowAiSettings()
    {
        try
        {
            if (!await AiDialogs.SettingsDialog(this, settings, aiTasks)) return;
            aiTasks.ConnectionTimeoutSeconds = settings.ComfyConnectionTimeoutSeconds;
            aiTasks.SelectedEngine = aiTasks.Engines.Find(settings.AiEngineId);
            RefreshAiUi();
        }
        catch (Exception error) { ShowProblem(error.Message); }
    }

    private async Task EditLayerTags(Composa.Model.Layer layer)
    {
        if (session == null) return;
        if (await AiDialogs.LayerTags(this, layer, settings) is { } tags) session.SetLayerTags(layer, tags);
    }

    private async Task RunAi(AiTaskKind task, string initialPrompt = "", string? referencePath = null, bool useInlinePrompt = false)
    {
        var availability = AiTaskAvailability.Resolve(session, aiTasks.SelectedEngine, task, CropExpands);
        if (!availability.Available) { ShowProblem(availability.Reason ?? "This AI task is unavailable."); return; }
        if (aiTasks.Operation?.Status is AiOperationStatus.Queued or AiOperationStatus.Running) { ShowProblem("Another AI operation is already running."); return; }
        var aspect = AiAspect(task);
        var fallback = AiDimensions.FromMegapixels(settings.AiMegapixels, aspect.Width, aspect.Height);
        AiPromptResult? options;
        if (useInlinePrompt || task is AiTaskKind.RemoveObject or AiTaskKind.SelectSubject or AiTaskKind.ObjectSelection or AiTaskKind.Upscale)
            options = new(initialPrompt, fallback.Width, fallback.Height, settings.AiSeed);
        else options = await AiDialogs.Prompt(this, task, settings, aspect.Width, aspect.Height, initialPrompt);
        if (options == null) return;
        if (session == null)
        {
            if (task != AiTaskKind.GenerateImage) return;
            AddSession(EditorSession.NewCanvas(options.Width, options.Height));
        }
        var seed = options.Seed < 0 ? Random.Shared.NextInt64(long.MaxValue) : options.Seed;
        SKBitmap? reference = null;
        try
        {
            if (referencePath != null) reference = ImageFiles.Load(referencePath);
            var request = new AiTaskRequest
            {
                Task = task,
                Prompt = options.Prompt,
                ReferenceImage = reference,
                ExpansionBounds = task == AiTaskKind.GenerativeExpand && canvas.CropRect is { } crop
                    ? new SKRectI((int)Math.Floor(crop.Left), (int)Math.Floor(crop.Top), (int)Math.Ceiling(crop.Right), (int)Math.Ceiling(crop.Bottom)) : null,
                Settings = new AiGenerationSettings { Width = options.Width, Height = options.Height, Seed = seed }
            };
            await aiTasks.RunAsync(new EditorCommandService(session!), request);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowProblem(error.Message); }
        finally { reference?.Dispose(); }
    }

    private (int Width, int Height) AiAspect(AiTaskKind task)
    {
        if (task == AiTaskKind.GenerativeExpand && canvas.CropRect is { Width: > 0, Height: > 0 } crop)
            return (Math.Max(1, (int)Math.Round(crop.Width)), Math.Max(1, (int)Math.Round(crop.Height)));
        if (task is AiTaskKind.GenerativeFill or AiTaskKind.RemoveObject or AiTaskKind.ChangeBackground or AiTaskKind.Harmonize
            && session?.Selection is { } selection && SelectionMask.Bounds(selection) is { IsEmpty: false } bounds)
            return (bounds.Width, bounds.Height);
        return (session?.Document.Width ?? 1, session?.Document.Height ?? 1);
    }

    private static bool IsEmpty(SKBitmap bitmap) => bitmap.GetPixelSpan().IndexOfAnyExcept((byte)0) < 0;
}
