using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Composa.AI;
using Composa.App.AI;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.IO;
using Composa.Rendering;
using Composa.Selections;
using SelectionMode = Composa.Selections.SelectionMode;
using SkiaSharp;

namespace Composa.App;

public sealed partial class MainWindow
{
    internal AiTaskService AiTasks => aiTasks;
    internal bool AiFloatingVisible => aiFloatingHost.IsVisible;
    internal int AiReferenceCount => aiReferences.Count;
    internal Border AiFloatingPanel => aiFloatingHost;
    internal Task RunAiForTests(AiTaskKind task) => RunAi(task);
    private Point? aiFloatingPinnedPosition;
    private Point? aiFloatingDragStart;
    private Point aiFloatingDragPosition;
    private bool refreshingAiVariants;
    private AiOperationSettings AiOptions(AiTaskKind task, EngineProfile? engine = null)
    {
        engine ??= aiTasks.EngineFor(task);
        return settings.OperationFor(engine?.Id, task, engine?.PaidApi == true);
    }
    private readonly StackPanel aiActionHost = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly StackPanel aiProgressHost = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly Canvas aiFloatingLayer = new();
    private readonly Border aiFloatingHost = new()
    {
        IsVisible = false, Width = 520, Padding = new Thickness(12), CornerRadius = new CornerRadius(8),
        Background = new SolidColorBrush(Color.Parse("#292929")), BorderBrush = new SolidColorBrush(Color.Parse("#4A4A4A")),
        BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
    };
    private readonly TextBox aiFloatingPrompt = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 54, MaxHeight = 90, PlaceholderText = "Describe what you want to create or edit" };
    private readonly StackPanel aiReferenceHost = new() { Spacing = 5 };
    private readonly List<AiReferenceItem> aiReferences = [];
    private AiReferenceEditor? aiDialogReferences;
    private bool refreshingAiEngine;
    private bool aiFloatingDismissed;
    private string? aiLastError;
    private Button? aiFloatingGenerate, aiFloatingMore, aiFloatingAdvanced;
    private ComboBox? aiFloatingTaskCombo;
    private AiTaskKind aiFloatingTask = AiTaskKind.GenerativeFill;
    private readonly TextBlock aiSelectionSummary = new() { Foreground = Palette.Secondary, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private ComboBox? aiVariantsCombo;
    private ComboBox? aiEngineCombo;
    private Grid? aiFloatingFooter;
    private StackPanel? aiFloatingActions;
    private readonly TextBlock aiApiCost = new() { Foreground = Palette.Secondary, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 225 };
    private readonly TextBlock aiFloatingStatus = new() { Foreground = Palette.Secondary, TextWrapping = TextWrapping.Wrap, Name = "AiPanelStatus" };

    private sealed class AiReferenceItem(SKBitmap pixels, Bitmap preview, string name) : IDisposable
    {
        public SKBitmap Pixels { get; } = pixels;
        public Bitmap Preview { get; } = preview;
        public string Name { get; } = name;
        public void Dispose() { Pixels.Dispose(); Preview.Dispose(); }
    }

    private Control BuildAiContextBar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(aiActionHost);
        row.Children.Add(aiProgressHost);
        aiContextHost.Child = row;
        RefreshAiUi();
        return aiContextHost;
    }

    private bool CropExpands => session?.Tool == Tool.Crop && canvas.CropRect is { } crop &&
        (crop.Left < 0 || crop.Top < 0 || crop.Right > session.Document.Width || crop.Bottom > session.Document.Height);

    private bool CanRunAi(AiTaskKind task) =>
        AiAvailability(task).Available && aiTasks.Operation?.Status is not (AiOperationStatus.Queued or AiOperationStatus.Running);

    private AiTaskAvailability AiAvailability(AiTaskKind task)
    {
        var engine = aiTasks.EngineFor(task);
        return AiTaskAvailability.Resolve(session, engine, task, CropExpands);
    }

    private void RefreshAiUi()
    {
        RefreshObjectModelChoices();
        RefreshAiContextBounds();
        if (toolButtons.TryGetValue(Tool.RemoveObject, out var removeTool)) removeTool.IsEnabled = canvas.AiToolsAvailable?.Invoke() == true;
        if (aiContextHost.Child == null) return;
        aiActionHost.Children.Clear();
        if (session != null) aiActionHost.Children.Add(BuildAiTaskMenu());
        if (session?.AiVariantGroup is { } variants)
        {
            aiActionHost.Children.Add(Ui.Label("Variants", Palette.Secondary));
            for (var i = 0; i < variants.Children.Count; i++)
            {
                var index = i;
                var button = Ui.TextButton((i + 1).ToString(), () => session?.SelectAiVariant(variants, index), accent: variants.Children[i].Visible);
                button.MinWidth = 28; button.IsEnabled = aiTasks.Operation?.Status is not (AiOperationStatus.Queued or AiOperationStatus.Running);
                aiActionHost.Children.Add(button);
            }
        }

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
        aiContextHost.IsVisible = session != null;
        RefreshAiFloatingUi();
    }

    private void RefreshAiContextBounds()
    {
        SKRectI? context = null;
        if (settings.AiShowContextBounds && aiTasks.ConnectionState == ComfyConnectionState.Connected
            && session is { Selection: { } selection, IsPaintingSelection: false } && !canvas.IsDragging)
        {
            var bounds = SelectionMask.Bounds(selection, 1);
            var profile = AiOptions(AiTaskKind.GenerativeFill);
            if (!bounds.IsEmpty)
                context = aiTasks.EngineFor(AiTaskKind.GenerativeFill)?.PaidApi == true
                    ? AiContextGeometry.Padded(bounds, session.Document.Bounds, profile.GptContextPadding)
                    : AiContextGeometry.Flux(bounds, session.Document.Bounds, profile.MaskGrow, profile.MaskBlend, profile.MaskContext, profile.MaskBlur);
        }
        canvas.AiContextBounds = context;
    }

    private Button BuildAiTaskMenu()
    {
        Button? button = null;
        button = Ui.TextButton("AI ▾", () =>
        {
            var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            foreach (var task in Enum.GetValues<AiTaskKind>())
            {
                if (task == AiTaskKind.SelectSubject) continue;
                var availability = AiAvailability(task);
                var item = new MenuItem
                {
                    Header = task.DisplayName(),
                    IsEnabled = availability.Available && aiTasks.Operation?.Status is not (AiOperationStatus.Queued or AiOperationStatus.Running)
                };
                if (availability.Reason != null) ToolTip.SetTip(item, availability.Reason);
                item.Click += (_, _) => _ = RunAi(task);
                flyout.Items.Add(item);
            }
            flyout.Items.Add(new Separator());
            var settingsItem = new MenuItem { Header = "AI Settings…", IsEnabled = aiTasks.Operation?.Status is not (AiOperationStatus.Queued or AiOperationStatus.Running) };
            settingsItem.Click += (_, _) => _ = ShowAiSettings();
            flyout.Items.Add(settingsItem);
            flyout.ShowAt(button!);
        });
        button.MinWidth = 0;
        ToolTip.SetTip(button, "AI tasks and settings · ComfyUI " + aiTasks.ConnectionState.ToString().ToLowerInvariant());
        return button;
    }

    private Control BuildAiFloatingMenu()
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Background = Brushes.Transparent, MinHeight = 30 };
        header.Children.Add(Ui.Label("Generative AI", weight: FontWeight.SemiBold));
        ToolTip.SetTip(header, "Drag the header to move the panel");
        header.PointerPressed += (_, e) =>
        {
            if (e.Source is Avalonia.Visual visual && visual.GetVisualAncestors().OfType<Button>().Any()) return;
            if (e.Source is Button) return;
            if (!e.GetCurrentPoint(aiFloatingLayer).Properties.IsLeftButtonPressed) return;
            aiFloatingDragStart = e.GetPosition(aiFloatingLayer);
            aiFloatingDragPosition = new Point(Avalonia.Controls.Canvas.GetLeft(aiFloatingHost), Avalonia.Controls.Canvas.GetTop(aiFloatingHost));
            e.Pointer.Capture(header); e.Handled = true;
        };
        header.PointerMoved += (_, e) =>
        {
            if (aiFloatingDragStart is not { } start) return;
            aiFloatingPinnedPosition = aiFloatingDragPosition + (e.GetPosition(aiFloatingLayer) - start);
            RefreshAiFloatingPosition(); e.Handled = true;
        };
        header.PointerReleased += (_, e) =>
        { if (aiFloatingDragStart == null) return; aiFloatingDragStart = null; e.Pointer.Capture(null); e.Handled = true; };
        header.PointerCaptureLost += (_, _) => aiFloatingDragStart = null;
        var advanced = aiFloatingAdvanced = Ui.TextButton("Advanced…", () => _ = ShowAiAdvanced());
        advanced.MinWidth = 0;
        AddAt(header, advanced, 1).Margin = new Thickness(0, 0, 5, 0);
        var close = Ui.TextButton("×", () => { aiFloatingDismissed = true; aiFloatingPinnedPosition = null; RefreshAiFloatingUi(); });
        close.MinWidth = 32;
        ToolTip.SetTip(close, "Close until the selection changes");
        AddAt(header, close, 2);

        aiFloatingTaskCombo = Ui.Combo(new[] { AiTaskKind.GenerativeFill, AiTaskKind.RemoveObject, AiTaskKind.ChangeBackground, AiTaskKind.Harmonize, AiTaskKind.Relight },
            aiFloatingTask, task => task == AiTaskKind.GenerativeFill ? "Fill selection" : task.DisplayName(), task => { aiFloatingTask = task; aiLastError = null; RefreshAiUi(); }, 205);
        aiFloatingTaskCombo.Name = "AiPanelTask";
        aiFloatingGenerate = Ui.TextButton("Generate", () => _ = RunAi(aiFloatingTask, aiFloatingPrompt.Text ?? "", useInlinePrompt: true), accent: true);
        aiFloatingGenerate.Name = "AiPanelRun";
        aiFloatingMore = Ui.TextButton("••• ▾", OpenAiFloatingTaskMenu);
        aiFloatingMore.MinWidth = 38;
        aiFloatingMore.Padding = new Thickness(8, 5);
        ToolTip.SetTip(aiFloatingMore, "More AI operations");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(aiFloatingMore);
        aiVariantsCombo = Ui.Combo(new[] { 1, 2, 3 }, Math.Clamp(AiOptions(AiTaskKind.GenerativeFill).Variants, 1, 3),
            value => value.ToString(), value =>
            {
                if (refreshingAiVariants) return;
                var engine = aiTasks.EngineFor(aiFloatingTask);
                settings.SetOperation(engine?.Id, aiFloatingTask, AiOptions(aiFloatingTask, engine) with { Variants = value });
                settings.Save(); RefreshAiFloatingUi();
            }, 60);
        aiVariantsCombo.MinWidth = 0; aiVariantsCombo.Padding = new Thickness(9, 5);
        ToolTip.SetTip(aiVariantsCombo, "Number of variants · 1, 2 or 3");
        // A split action: one silhouette, a straight seam, independent keyboard-accessible controls.
        aiFloatingMore.Height = 26;
        aiFloatingMore.MinHeight = 0;
        aiFloatingMore.Padding = new Thickness(8, 0);
        aiFloatingMore.VerticalContentAlignment = VerticalAlignment.Center;
        actions.Children.Add(AiGenerationControls.Split(aiFloatingGenerate, aiVariantsCombo));
        aiEngineCombo = new ComboBox { MaxWidth = 310, HorizontalAlignment = HorizontalAlignment.Stretch, Name = "AiPanelWorkflow" };
        aiEngineCombo.SelectionChanged += (_, _) =>
        {
            if (refreshingAiEngine || aiEngineCombo.SelectedIndex < 0) return;
            var available = aiTasks.Engines.Profiles.Where(profile => profile.Binding(aiFloatingTask) != null).ToArray();
            if (aiEngineCombo.SelectedIndex >= available.Length) return;
            settings.AiTaskEngineIds[aiFloatingTask.ToString()] = available[aiEngineCombo.SelectedIndex].Id; settings.Save(); RefreshAiUi();
        };
        aiEngineCombo.MinWidth = 0; aiEngineCombo.Padding = new Thickness(9, 5);
        ToolTip.SetTip(aiEngineCombo, "Workflow pack · GPT uses paid Comfy.org credits; FLUX uses your server's models");
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };
        aiFloatingFooter = footer; aiFloatingActions = actions;
        aiApiCost.Margin = new Thickness(0, 0, 0, 5); footer.Children.Add(aiApiCost);
        Grid.SetRow(actions, 1); Grid.SetColumnSpan(actions, 2); footer.Children.Add(actions);
        RefreshAiReferenceUi();
        var summary = new Border { Background = Palette.Panel, CornerRadius = new CornerRadius(4), Padding = new Thickness(8), Child = aiSelectionSummary };
        var taskLabel = Ui.Label("Task", Palette.Secondary); taskLabel.Width = 65;
        var workflowLabel = Ui.Label("Workflow", Palette.Secondary); workflowLabel.Width = 65;
        var workflowRow = new Grid { ColumnDefinitions = new ColumnDefinitions("73,*") };
        workflowRow.Children.Add(workflowLabel); AddAt(workflowRow, aiEngineCombo, 1);
        return Ui.Column(9, header, Ui.Row(8, taskLabel, aiFloatingTaskCombo), summary,
            aiFloatingPrompt, aiReferenceHost, workflowRow, aiFloatingStatus, footer);
    }

    private void OpenAiFloatingTaskMenu()
    {
        if (aiFloatingMore == null) return;
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var task in Enum.GetValues<AiTaskKind>().Where(task => task is not (AiTaskKind.GenerativeFill or AiTaskKind.RemoveObject or AiTaskKind.SelectSubject)))
        {
            if (task == AiTaskKind.ImageEdit && session?.Selection != null) continue;
            var availability = AiAvailability(task);
            var item = new MenuItem
            {
                Header = task.DisplayName(),
                IsEnabled = availability.Available && aiTasks.Operation?.Status is not (AiOperationStatus.Queued or AiOperationStatus.Running)
            };
            if (availability.Reason != null) ToolTip.SetTip(item, availability.Reason);
            item.Click += (_, _) => _ = RunAi(task, aiFloatingPrompt.Text ?? "", useInlinePrompt: true);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(aiFloatingMore);
    }

    private void RefreshAiFloatingUi()
    {
        var visible = session?.Selection != null && session.IsPaintingSelection == false
            && session.Tool != Tool.RemoveObject && !(session.Tool == Tool.ObjectSelectionAi && canvas.IsDragging)
            && aiTasks.ConnectionState == ComfyConnectionState.Connected && !aiFloatingDismissed;
        aiFloatingHost.IsVisible = visible;
        if (!visible) return;
        var busy = aiTasks.Operation?.Status is AiOperationStatus.Queued or AiOperationStatus.Running;
        var fillEngine = aiTasks.EngineFor(aiFloatingTask);
        var fillProfile = AiOptions(aiFloatingTask, fillEngine);
        if (aiFloatingTaskCombo != null) aiFloatingTaskCombo.IsEnabled = !busy;
        if (aiFloatingAdvanced != null) aiFloatingAdvanced.IsEnabled = !busy;
        aiReferenceHost.IsEnabled = !busy;
        aiFloatingPrompt.IsVisible = aiFloatingTask != AiTaskKind.RemoveObject;
        aiFloatingPrompt.IsEnabled = !busy;
        aiFloatingPrompt.PlaceholderText = aiFloatingTask == AiTaskKind.ChangeBackground ? "Describe the new background" : "Describe what you want to create or edit in this area";
        var selectedBounds = SelectionMask.Bounds(session!.Selection!);
        aiSelectionSummary.Text = aiFloatingTask == AiTaskKind.RemoveObject
            ? $"Remove the selected object · {selectedBounds.Width} × {selectedBounds.Height} px\nSurrounding image supplies context. The result is editable and undoable."
            : $"Selected area · {selectedBounds.Width} × {selectedBounds.Height} px\nThe surrounding image supplies context. The result is editable and undoable.";
        if (aiVariantsCombo != null)
        {
            refreshingAiVariants = true;
            try { aiVariantsCombo.SelectedIndex = Math.Clamp(fillProfile.Variants, 1, 3) - 1; }
            finally { refreshingAiVariants = false; }
            aiVariantsCombo.IsEnabled = !busy;
        }
        if (aiEngineCombo != null)
        {
            var available = aiTasks.Engines.Profiles.Where(profile => profile.Binding(aiFloatingTask) != null).ToArray();
            var index = Array.FindIndex(available, profile => profile.Id == fillEngine?.Id);
            refreshingAiEngine = true;
            try { aiEngineCombo.ItemsSource = available.Select(profile => profile.DisplayName).ToArray(); aiEngineCombo.SelectedIndex = index; }
            finally { refreshingAiEngine = false; }
            aiEngineCombo.IsEnabled = !busy;
        }
        if (aiFloatingGenerate != null)
        {
            aiFloatingGenerate.Content = aiFloatingTask switch { AiTaskKind.RemoveObject => "Remove", AiTaskKind.GenerativeFill => "Generate", _ => aiFloatingTask.DisplayName() };
            aiFloatingGenerate.IsEnabled = !busy && AiAvailability(aiFloatingTask).Available;
            ToolTip.SetTip(aiFloatingGenerate, AiAvailability(aiFloatingTask).Reason ?? "Run " + aiFloatingTask.DisplayName() + " using " + fillEngine?.DisplayName);
        }
        if (aiFloatingMore != null) aiFloatingMore.IsEnabled = !busy && Enum.GetValues<AiTaskKind>().Any(task =>
            task is not (AiTaskKind.GenerativeFill or AiTaskKind.RemoveObject) && AiAvailability(task).Available);
        var operation = aiTasks.Operation;
        aiFloatingStatus.Text = aiLastError ?? (operation?.Status switch
        {
            AiOperationStatus.Queued or AiOperationStatus.Running => operation.Stage ?? "Working…",
            AiOperationStatus.Completed => "Completed",
            AiOperationStatus.Cancelled => "Cancelled",
            AiOperationStatus.Failed => operation.Error ?? "AI operation failed",
            _ => AiAvailability(aiFloatingTask).Reason ?? "Ready · result added as an editable layer"
        });
        aiFloatingStatus.Foreground = aiLastError != null || operation?.Status == AiOperationStatus.Failed ? Brushes.Orange : Palette.Secondary;
        aiApiCost.IsVisible = fillEngine?.PaidApi == true;
        if (aiApiCost.IsVisible)
        {
            var count = 1 + aiReferences.Count; // cropped source and genuine user references only
            var estimate = PartnerPricing.Estimate(aiTasks.ServerCapabilities, fillEngine!.ApiModel!, fillProfile.ApiQuality, "Custom", count, Math.Clamp(fillProfile.Variants, 1, 3));
            aiApiCost.Text = operation?.CreditsUsed is { } credits ? PartnerPricing.Reported(credits) : estimate?.Label ?? "Paid API · estimate unavailable";
            ToolTip.SetTip(aiApiCost, "ComfyUI bills credits, not a balance of OpenAI tokens. Estimate comes from this server's price badge and includes the cropped source, user references and all variants. Actual charges may differ. Local ComfyUI does not expose the account balance. The edit mask stays in Composa and is not a billed input.");
        }
        ToolTip.SetTip(aiFloatingStatus, aiFloatingStatus.Text);
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
        if (aiFloatingFooter != null && aiFloatingActions != null)
        {
            var narrow = width < 490;
            Grid.SetRow(aiFloatingActions, 1); Grid.SetColumn(aiFloatingActions, 0); Grid.SetColumnSpan(aiFloatingActions, 2);
            aiFloatingActions.Margin = new Thickness(0, narrow ? 7 : 0, 0, 0);
        }
        var height = Math.Max(190, aiFloatingHost.Bounds.Height);
        var maxLeft = Math.Max(8, canvas.Bounds.Width - width - 8);
        var left = Math.Clamp((topLeft.X + bottomRight.X - width) / 2, 8, maxLeft);
        var below = bottomRight.Y + 10;
        // Always anchor below, never jump to an unrelated side of the selection. Only
        // constrain to the viewport at its edges, and retain the user's drag offset.
        left = Math.Clamp(aiFloatingPinnedPosition?.X ?? left, 8, maxLeft);
        var top = aiFloatingPinnedPosition?.Y ?? below;
        top = Math.Clamp(top, 8, Math.Max(8, canvas.Bounds.Height - height - 8));
        Avalonia.Controls.Canvas.SetLeft(aiFloatingHost, left);
        Avalonia.Controls.Canvas.SetTop(aiFloatingHost, top);
    }

    private async Task PickAiReferences()
    {
        var files = await (aiDialogReferences?.Owner ?? this).StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose Reference Images", AllowMultiple = true, FileTypeFilter = [ImageType]
        });
        AddAiReferencePaths(files.Select(file => file.TryGetLocalPath()).OfType<string>());
    }

    private void RefreshAiReferenceUi()
    {
        BuildAiReferenceUi(aiReferenceHost);
        if (aiDialogReferences?.View is StackPanel dialogHost) BuildAiReferenceUi(dialogHost);
        aiDialogReferences?.NotifyChanged();
        RefreshAiFloatingUi();
    }

    private void BuildAiReferenceUi(StackPanel host)
    {
        host.Children.Clear();
        var label = Ui.Label($"References ({aiReferences.Count}/6)", Palette.Secondary);
        host.Children.Add(label);
        var items = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var item in aiReferences.ToArray())
        {
            var preview = new Image { Source = item.Preview, Width = 52, Height = 52, Stretch = Stretch.UniformToFill };
            var remove = new Button
            {
                Content = "×", MinWidth = 22, Width = 22, Height = 22, Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.Parse("#CC292929")), Foreground = Brushes.White
            };
            remove.Click += (_, e) =>
            {
                e.Handled = true;
                aiReferences.Remove(item);
                item.Dispose();
                RefreshAiReferenceUi();
            };
            remove.SetValue(Panel.ZIndexProperty, 1);
            ToolTip.SetTip(remove, "Remove reference");
            var cell = new Grid { Width = 56, Height = 56, Margin = new Thickness(0, 0, 7, 5) };
            var show = new Button { Content = preview, Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            show.Click += (_, _) => _ = ShowAiReferencePreview(item, TopLevel.GetTopLevel(host) as Window);
            ToolTip.SetTip(show, "Preview reference: " + item.Name);
            cell.Children.Add(show);
            cell.Children.Add(remove);
            ToolTip.SetTip(cell, item.Name);
            items.Children.Add(cell);
        }
        if (aiReferences.Count < 6)
        {
            var add = Ui.IconButton(Icons.Image, "Add reference image", () => _ = PickAiReferences(), 23);
            add.Width = aiReferences.Count == 0 && host == aiReferenceHost ? 28 : 56;
            add.Height = aiReferences.Count == 0 && host == aiReferenceHost ? 28 : 56;
            add.MinWidth = 0;
            add.Classes.Remove("flat");
            add.Padding = new Thickness(0);
            add.VerticalContentAlignment = VerticalAlignment.Center;
            add.HorizontalContentAlignment = HorizontalAlignment.Center;
            add.Margin = new Thickness(0, 0, 7, 5);
            add.FontSize = 22;
            ToolTip.SetTip(add, aiReferences.Count == 0 ? "Add reference image · paste with Ctrl+V or drag files here" : "Add another reference image");
            items.Children.Add(add);
        }
        if (aiReferences.Count == 0 && host == aiReferenceHost)
        { host.Children.Clear(); host.Children.Add(Ui.Row(8, label, items)); }
        else host.Children.Add(items);
    }

    private async Task ShowAiReferencePreview(AiReferenceItem item, Window? owner = null)
    {
        // Hold a separate image while the preview is open: removing a reference must not
        // dispose pixels still being rendered by another window.
        using var stream = new MemoryStream(); item.Preview.Save(stream); stream.Position = 0;
        using var preview = new Bitmap(stream);
        var dialog = new Window { Title = item.Name, Width = 760, Height = 600, MinWidth = 300, MinHeight = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var root = new DockPanel { Margin = new Thickness(12) };
        var close = Ui.TextButton("Close", dialog.Close); DockPanel.SetDock(close, Dock.Bottom); root.Children.Add(close);
        var label = Ui.Label($"{item.Name} · {item.Pixels.Width} × {item.Pixels.Height}"); DockPanel.SetDock(label, Dock.Top); root.Children.Add(label);
        root.Children.Add(new Image { Source = preview, Stretch = Stretch.Uniform }); dialog.Content = root;
        await dialog.ShowDialog(owner ?? this);
    }

    private void AddAiReferencePaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (aiReferences.Count >= 6) break;
            try { AddAiReference(ImageFiles.Load(path), Path.GetFileName(path)); }
            catch (Exception error) { ShowProblem($"Couldn't add {Path.GetFileName(path)}: {error.Message}"); }
        }
        RefreshAiReferenceUi();
    }

    private void AddAiReference(SKBitmap pixels, string name)
    {
        if (aiReferences.Count >= 6) { pixels.Dispose(); return; }
        try
        {
            using var image = SKImage.FromBitmap(pixels);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = new MemoryStream(encoded.ToArray());
            aiReferences.Add(new AiReferenceItem(pixels, new Bitmap(stream), name));
        }
        catch { pixels.Dispose(); throw; }
    }

    internal void AddAiReferenceForTests(SKBitmap pixels)
    {
        AddAiReference(pixels, "Test reference");
        RefreshAiReferenceUi();
    }

    private async Task<bool> PasteAiReference()
    {
        if (aiReferences.Count >= 6) return false;
        try
        {
            if (await ExternalClipboardBitmap() is { } bitmap)
            {
                using (bitmap)
                {
                    using var stream = new MemoryStream();
                    bitmap.Save(stream, PngBitmapEncoderOptions.Default);
                    stream.Position = 0;
                    AddAiReference(ImageFiles.Load(stream, "clipboard"), "Clipboard image");
                }
            }
            else if (EditorSession.Clipboard is { } internalImage) AddAiReference(Pixels.Clone(internalImage.Pixels), "Clipboard image");
            else return false;
            RefreshAiReferenceUi();
            return true;
        }
        catch (Exception error) { ShowProblem("Couldn't paste the reference: " + error.Message); return false; }
    }

    private async Task PasteAiReferenceOrText(TextBox? textBox)
    {
        if (await PasteAiReference() || textBox == null || Clipboard == null) return;
        try
        {
            var text = await Clipboard.TryGetTextAsync();
            if (text == null) return;
            var current = textBox.Text ?? "";
            var start = Math.Min(textBox.SelectionStart, textBox.SelectionEnd);
            var end = Math.Max(textBox.SelectionStart, textBox.SelectionEnd);
            textBox.Text = current[..start] + text + current[end..];
            textBox.CaretIndex = start + text.Length;
        }
        catch { /* A clipboard that changed mid-paste is harmless. */ }
    }

    private void OnAiReferenceDrop(object? sender, DragEventArgs e)
    {
        var paths = e.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).OfType<string>() ?? [];
        AddAiReferencePaths(paths);
        e.Handled = true;
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

    private async Task ShowAiAdvanced()
    {
        var previous = aiTasks.SelectedEngine;
        var task = session?.Selection != null ? aiFloatingTask : AiTaskKind.ImageEdit;
        aiTasks.SelectedEngine = aiTasks.EngineFor(task);
        try { await AiDialogs.Advanced(this, settings, aiTasks, task); }
        finally { aiTasks.SelectedEngine = previous; RefreshAiUi(); }
    }

    private async Task EditLayerTags(Composa.Model.Layer layer)
    {
        if (session == null) return;
        if (await AiDialogs.LayerTags(this, layer, settings) is { } tags) session.SetLayerTags(layer, tags);
    }

    private async Task RunAi(AiTaskKind task, string initialPrompt = "", bool useInlinePrompt = false, SKRectI? selectionRegion = null, SelectionMode selectionOperation = SelectionMode.Replace)
    {
        if (task == AiTaskKind.GenerativeExpand) initialPrompt = "";
        aiLastError = null;
        aiFloatingStatus.Text = "Starting…";
        var availability = AiAvailability(task);
        if (!availability.Available) { AiFailed(availability.Reason ?? "This AI task is unavailable."); return; }
        if (aiTasks.Operation?.Status is AiOperationStatus.Queued or AiOperationStatus.Running) { AiFailed("Another AI operation is already running."); return; }
        if (task == AiTaskKind.Upscale && session is { } upscaleSession)
        {
            var area = upscaleSession.Selection == null ? upscaleSession.Document.Bounds : SelectionMask.Bounds(upscaleSession.Selection);
            if (!await AiDialogs.Upscale(this, settings, area.Width, area.Height, upscaleSession.Selection != null, aiTasks.EngineFor(task))) return;
        }
        var aspect = AiAspect(task);
        var profile = AiOptions(task);
        var fallback = profile.OriginalSize ? aspect : AiDimensions.FromMegapixels(profile.Megapixels, aspect.Width, aspect.Height);
        AiPromptResult? options;
        if (task != AiTaskKind.GenerativeExpand && (useInlinePrompt || task == AiTaskKind.RemoveObject && session?.Selection != null || task is AiTaskKind.SelectSubject or AiTaskKind.ObjectSelection or AiTaskKind.Upscale or AiTaskKind.MatchToScene))
            options = new(initialPrompt, fallback.Width, fallback.Height, profile.Seed);
        else
        {
            var host = new StackPanel { Name = "AiDialogReferences", Spacing = 5, MaxWidth = 430 };
            DragDrop.SetAllowDrop(host, true);
            host.AddHandler(DragDrop.DropEvent, OnAiReferenceDrop);
            host.AddHandler(DragDrop.DragOverEvent, (_, e) => { e.DragEffects = DragDropEffects.Copy; e.Handled = true; });
            aiDialogReferences = new AiReferenceEditor(host, () => aiReferences.Count, PasteAiReferenceOrText);
            BuildAiReferenceUi(host);
            try { options = await AiDialogs.Prompt(this, task, settings, aspect.Width, aspect.Height, initialPrompt, aiTasks, aiReferences.Count,
                session?.Selection != null && !(task == AiTaskKind.GenerativeExpand && session.Tool == Tool.Crop), aiDialogReferences,
                session?.Document.Bounds, session?.Selection is { } selected ? SelectionMask.Bounds(selected,1) : null); }
            finally { aiDialogReferences = null; }
        }
        RefreshAiUi();
        if (options == null) return;
        if (session == null)
        {
            if (task != AiTaskKind.GenerateImage) return;
            AddSession(EditorSession.NewCanvas(options.Width, options.Height));
        }
        var seed = options.Seed < 0 ? Random.Shared.NextInt64(long.MaxValue) : options.Seed;
        var engine = aiTasks.EngineFor(task, options.EngineId);
        profile = AiOptions(task, engine);
        try
        {
            var request = new AiTaskRequest
            {
                Task = task,
                EngineId = engine?.Id,
                SelectionRegion = selectionRegion, SelectionOperation = selectionOperation,
                Prompt = task == AiTaskKind.GenerativeExpand ? AiPromptDefaults.Expand : options.Prompt,
                ExpansionMode = profile.ExpansionMode,
                ExpansionMinimumSide = profile.ExpansionMode == AiExpansionMode.WholeImage ? profile.WholeExpansionMinimumSide : profile.ExpansionMinimumSide,
                ReferenceImages = aiReferences.Select(item => item.Pixels).ToList(),
                ReferenceMegapixels = profile.ReferenceMegapixels,
                ExpansionBounds = task == AiTaskKind.GenerativeExpand && session?.Tool == Tool.Crop && canvas.CropRect is { } crop
                    ? new SKRectI((int)Math.Floor(crop.Left), (int)Math.Floor(crop.Top), (int)Math.Ceiling(crop.Right), (int)Math.Ceiling(crop.Bottom)) : null,
                Settings = new AiGenerationSettings
                {
                    Width = options.Width, Height = options.Height, Seed = seed,
                    Variants = Math.Clamp(profile.Variants, 1, 3), VariantMode = profile.VariantMode,
                    Loras = engine?.Lora.Supported == true && profile.LorasEnabled
                        ? profile.Loras.Take(3).Select(lora => new AiLora(lora.Name, lora.Strength, lora.Enabled)).ToArray() : [],
                    UpscaleFactor = profile.UpscaleFactor == 4 ? 4 : 2,
                    Values = new Dictionary<string, object?> { ["maskGrow"] = profile.MaskGrow, ["maskBlend"] = profile.MaskBlend,
                        ["maskContext"] = profile.MaskContext, ["maskBlur"] = profile.MaskBlur, ["colorMatch"] = profile.ColorMatch,
                        ["fluxMemory"] = profile.FluxMemory,
                        ["gptContextPadding"] = profile.GptContextPadding,
                        ["apiQuality"] = profile.ApiQuality, ["apiSize"] = "Custom", ["imageOriginalSize"] = profile.OriginalSize }
                }
            };
            await aiTasks.RunAsync(new EditorCommandService(session!), request);
            if (task == AiTaskKind.GenerativeExpand && session?.Tool == Tool.Crop) canvas.ResetCropToCanvas();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { AiFailed(error.Message); }
    }

    private void AiFailed(string message)
    {
        aiLastError = message;
        ShowProblem(message);
        RefreshAiUi();
    }

    private (int Width, int Height) AiAspect(AiTaskKind task)
    {
        if (task == AiTaskKind.GenerativeExpand && session?.Tool == Tool.Crop && canvas.CropRect is { Width: > 0, Height: > 0 } crop)
            return (Math.Max(1, (int)Math.Round(crop.Width)), Math.Max(1, (int)Math.Round(crop.Height)));
        if (task is AiTaskKind.GenerativeFill or AiTaskKind.RemoveObject or AiTaskKind.Harmonize or AiTaskKind.Relight or AiTaskKind.GenerativeExpand
            && session?.Selection is { } selection && SelectionMask.Bounds(selection) is { IsEmpty: false } bounds)
            return (bounds.Width, bounds.Height);
        return session == null ? AiDimensions.FromMegapixels(settings.AiMegapixels, 1, 1) : (session.Document.Width, session.Document.Height);
    }

    private static bool IsEmpty(SKBitmap bitmap) => bitmap.GetPixelSpan().IndexOfAnyExcept((byte)0) < 0;
}
