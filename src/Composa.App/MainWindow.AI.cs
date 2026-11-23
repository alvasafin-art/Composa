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
    internal Task RunAiForTests(AiTaskKind task, string? prompt = null) => RunAi(task, prompt ?? "", useInlinePrompt: prompt != null);
    private SKBitmap? floatingBoundsMask;
    private SKRectI floatingBounds;
    private Point? aiFloatingPinnedPosition;
    private Point? aiFloatingDragStart;
    private Point aiFloatingDragPosition;
    private readonly Dictionary<AiTaskKind, string> modelDrafts = [];
    private Button aiEnhance = null!, aiAdvanced = null!;
    private DirectImageGenerator? DirectImageFor(AiTaskKind task) =>
        settings.ImageGenerationRoute != ImageGenerationRoute.ComfyWorkflow && DirectImageGenerator.Controls(task)
            ? DirectImageGenerator.From(settings) : null;
    private EngineProfile? PanelEngine(AiTaskKind task) => DirectImageFor(task)?.PromptProfile()
        ?? aiTasks.EngineFor(task, modelDrafts.GetValueOrDefault(task));
    private AiOperationSettings AiOptions(AiTaskKind task, EngineProfile? engine = null)
    {
        engine ??= PanelEngine(task);
        return settings.OperationFor(engine?.Id, task, engine?.PaidApi == true);
    }
    private Button aiTaskMenuButton = null!;
    private ComfyConnectionState? shownConnection;
    private AiOperationState? shownProgress;
    private readonly StackPanel aiActionHost = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly StackPanel aiVariantStrip = new() { Orientation = Orientation.Horizontal, Spacing = 4, IsVisible = false };
    private readonly ContentControl aiFloatingVariants = new() { IsVisible = false };
    private readonly StackPanel aiProgressHost = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly Canvas aiFloatingLayer = new();
    private readonly Border aiFloatingHost = new()
    {
        IsVisible = false, Width = 520, Padding = new Thickness(AiGenerationControls.EditorSpacing), CornerRadius = new CornerRadius(5),
        Background = Palette.Panel, BorderBrush = Palette.Edge,
        BoxShadow = BoxShadows.Parse("0 3 12 0 #44000000"),
        BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
    };
    private readonly TextBox aiFloatingPrompt = AiGenerationControls.PromptField("Describe the edit…");
    private readonly StackPanel aiReferenceHost = new() { Spacing = 5 };
    private readonly List<AiReferenceItem> aiReferences = [];
    private AiReferenceEditor? aiDialogReferences;
    private bool refreshingAiEngine;
    private WrapPanel? aiFloatingTools;
    private ToggleButton? aiFloatingAlignment;
    private Button? aiFloatingCollapse, aiHeaderReference, aiInlineAddReference;
    private string? aiLastError;
    private Button? aiFloatingGenerate, aiFloatingMore;
    private StackPanel? aiVariantButtons;
    private ComboBox? aiEngineCombo;
    private StackPanel? aiFloatingActions;
    private static readonly IBrush CreditColor = new SolidColorBrush(Color.Parse("#D6C08C"));
    private readonly TextBlock aiApiCost = Ui.Label("", CreditColor, 11);
    private StackPanel aiPriceBadge = null!;

    private sealed class AiReferenceItem(SKBitmap pixels, Bitmap preview, string name) : IDisposable
    {
        public SKBitmap Pixels { get; } = pixels;
        public Bitmap Preview { get; private set; } = preview;
        public string Name { get; } = name;
        public void UpdateDisplay(Composa.ColorManagement.RgbProfile profile)
        { var next = Ui.ToAvaloniaBitmap(Pixels, 512, profile); var old = Preview; Preview = next; old.Dispose(); }
        public void Dispose() { Pixels.Dispose(); Preview.Dispose(); }
    }

    private Control BuildAiContextBar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        aiTaskMenuButton = BuildAiTaskMenu();
        aiActionHost.Children.Add(aiTaskMenuButton);
        aiActionHost.Children.Add(aiVariantStrip);
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
        if (task == AiTaskKind.ObjectSelection && settings.ObjectSelectionModel != ObjectSelectionSource.ComfyUI)
            return new(task, session != null && !localSelectionBusy, session == null ? "Open a document first." : null);
        if (DirectImageFor(task) is { } direct)
        {
            var reason = direct.Availability(task, aiReferences.Count);
            return reason == null ? AiTaskAvailability.Resolve(session, direct.PromptProfile(), task, CropExpands)
                : new(task, false, reason);
        }
        var engine = PanelEngine(task);
        return AiTaskAvailability.Resolve(session, engine, task, CropExpands);
    }

    private void ChooseAiOperation(AiTaskKind task)
    {
        if (!CanRunAi(task)) return;
        bool immediate = task is AiTaskKind.RemoveObject or AiTaskKind.GenerativeExpand;
        if (DockEditorVisible && (immediate || task is AiTaskKind.GenerateImage or AiTaskKind.ImageEdit or AiTaskKind.GenerativeFill))
        {
            manualDockTask = task;
            RefreshAiUi();
            if (!immediate) { aiFloatingPrompt.Focus(); return; }
        }
        if (task == AiTaskKind.ObjectSelection) { SelectSubject(); return; }
        if (task == AiTaskKind.RemoveObject && session?.Selection == null) { SelectTool(Tool.RemoveObject); return; }
        _ = RunAi(task);
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SKBitmap, Bitmap> variantPreviews = new();
    private Composa.ColorManagement.RgbProfile? variantDisplay;

    private Button CompareVariants(EditorSession target, Guid group)
    {
        var button = Ui.IconButton(Icons.Eye, "Hold to compare with the original", () => { });
        button.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;
            e.Pointer.Capture(button); target.CompareWithoutVariants(group); e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        button.PointerReleased += (_, e) => { target.CompareWithoutVariants(null); e.Pointer.Capture(null); };
        button.PointerCaptureLost += (_, _) => target.CompareWithoutVariants(null);
        button.AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Space) { target.CompareWithoutVariants(group); e.Handled = true; } }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        button.AddHandler(KeyUpEvent, (_, e) => { if (e.Key == Key.Space) { target.CompareWithoutVariants(null); e.Handled = true; } }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        button.LostFocus += (_, _) => target.CompareWithoutVariants(null);
        button.DetachedFromVisualTree += (_, _) => target.CompareWithoutVariants(null);
        return button;
    }

    private void RefreshAiUi()
    {
        if (generativeExpandButton != null) generativeExpandButton.IsEnabled = CanRunAi(AiTaskKind.GenerativeExpand);
        if (aiContextHost.Child != null) RefreshGenerationDock();
        RefreshObjectModelChoices();
        RefreshAiContextBounds();
        if (toolButtons.TryGetValue(Tool.RemoveObject, out var removeTool)) removeTool.IsEnabled = canvas.AiToolsAvailable?.Invoke() == true;
        if (aiContextHost.Child == null) return;
        aiTaskMenuButton.IsVisible = session != null;
        if (shownConnection != aiTasks.ConnectionState)
        {
            shownConnection = aiTasks.ConnectionState;
            Locale.Tip(aiTaskMenuButton, "AI tasks and settings · ComfyUI " + aiTasks.ConnectionState.ToString().ToLowerInvariant());
        }
        var variants = session?.AiVariantGroup;
        if (aiVariantStrip.Children.Count != 0) aiVariantStrip.Children.Clear();
        aiVariantStrip.IsVisible = variants != null;
        aiFloatingVariants.IsVisible = variants != null && (DockEditorVisible || ShowSelectionPanel && !settings.AiFloatingCollapsed);
        if (aiFloatingVariants.IsVisible && aiFloatingVariants.Content == null)
        {
            aiActionHost.Children.Remove(aiVariantStrip);
            aiFloatingVariants.Content = aiVariantStrip;
        }
        else if (!aiFloatingVariants.IsVisible && aiFloatingVariants.Content != null)
        {
            aiFloatingVariants.Content = null;
            aiActionHost.Children.Add(aiVariantStrip);
        }
        if (variants != null && session != null)
        {
            if (!Equals(variantDisplay, DisplayProfile)) { variantPreviews.Clear(); variantDisplay = DisplayProfile; }
            for (var i = 0; i < variants.Children.Count; i++)
            {
                var index = i;
                var button = Ui.TextButton((i + 1).ToString(), () => session?.SelectAiVariant(variants, index), accent: variants.Children[i].Visible);
                if (variants.Children[i].Pixels is { } pixels)
                    button.Content = new Image { Source = variantPreviews.GetValue(pixels, p => Ui.ToAvaloniaBitmap(p, 54, DisplayProfile)),
                        Width = 30, Height = 22, Stretch = Stretch.Uniform };
                Locale.Tip(button, $"Variant {i + 1}");
                Avalonia.Automation.AutomationProperties.SetName(button, $"Variant {i + 1}");
                button.Padding = new Thickness(2);
                button.MinWidth = 28; button.IsEnabled = aiTasks.Operation?.Status is not (AiOperationStatus.Queued or AiOperationStatus.Running);
                aiVariantStrip.Children.Add(button);
            }
            aiVariantStrip.Children.Add(CompareVariants(session, variants.Id));
        }

        RefreshAiProgress();
        aiContextHost.IsVisible = session != null;
        RefreshAiFloatingUi();
    }

    private void RefreshAiState()
    {
        if (shownProgress?.Status is AiOperationStatus.Queued or AiOperationStatus.Running &&
            aiTasks.Operation?.Status is AiOperationStatus.Queued or AiOperationStatus.Running &&
            shownConnection == aiTasks.ConnectionState)
            RefreshAiProgress();
        else RefreshAiUi();
    }

    private void RefreshAiProgress()
    {
        var operation = aiTasks.Operation;
        var active = operation?.Status is AiOperationStatus.Queued or AiOperationStatus.Running;
        if (shownProgress != operation)
        {
            shownProgress = operation;
            aiProgressHost.Children.Clear();
            if (operation != null && operation.Status != AiOperationStatus.Completed) AddProgress(operation, active);
        }
        aiTaskText.IsVisible = active;
        aiTaskText.Text = active ? "AI: " + (operation?.Stage ?? "Working") +
            (operation is { Value: {} step, Maximum: > 0 } ? $" · {step}/{operation.Maximum}"
             : operation?.ElapsedSeconds is {} seconds ? $" · {TimeSpan.FromSeconds(seconds):m\\:ss}" : "") : "";
    }

    private void AddProgress(AiOperationState operation, bool active)
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

    private SKRectI? previewContext;
    private int previewRevision;

    internal async Task PreviewAiContext(SKRectI bounds)
    {
        var revision = ++previewRevision;
        previewContext = bounds;
        RefreshAiContextBounds();
        aiFloatingHost.IsVisible = false;
        try { await Task.Delay(TimeSpan.FromSeconds(2)); }
        finally
        {
            if (revision == previewRevision)
            {
                previewContext = null;
                RefreshAiContextBounds();
                RefreshAiFloatingUi();
            }
        }
    }

    private void RefreshAiContextBounds()
    {
        if (previewContext is { } preview) { canvas.AiContextBounds = preview; return; }
        SKRectI? context = null;
        if ((!DockEditorVisible || EditorTask is AiTaskKind.GenerativeFill or AiTaskKind.RemoveObject or AiTaskKind.GenerativeExpand) && settings.AiShowContextBounds && aiTasks.ConnectionState == ComfyConnectionState.Connected
            && session is { Selection: { } selection, IsPaintingSelection: false } && !canvas.IsDragging)
        {
            var bounds = AutomaticAiMask.ContourBounds(selection);
            if (!bounds.IsEmpty)
                context = AutomaticAiMask.Geometry(bounds, session.Document.Bounds).Bounds;
        }
        canvas.AiContextBounds = context;
    }

    private Button BuildAiTaskMenu()
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var task in Enum.GetValues<AiTaskKind>().Where(t => t != AiTaskKind.SelectSubject))
        {
            var item = new MenuItem { Header = task.DisplayName() };
            flyout.Opening += (_, _) =>
            {
                item.IsEnabled = CanRunAi(task);
                Locale.Tip(item, AiAvailability(task).Reason);
            };
            item.Click += (_, _) => ChooseAiOperation(task);
            flyout.Items.Add(item);
        }
        flyout.Items.Add(new Separator());
        var settingsItem = new MenuItem { Header = Locale.T("AI Settings…") };
        flyout.Opening += (_, _) => settingsItem.IsEnabled = aiTasks.Operation?.Status is not (AiOperationStatus.Queued or AiOperationStatus.Running);
        settingsItem.Click += (_, _) => _ = ShowAiSettings();
        flyout.Items.Add(settingsItem);
        return new Button { Name = "AiTaskMenu", Content = Locale.T("AI ▾"), Flyout = flyout, MinWidth = 0 };
    }

    private Control BuildAiFloatingMenu()
    {
        var grip = AiGenerationControls.DragHandle();
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(aiFloatingLayer).Properties.IsLeftButtonPressed) return;
            aiFloatingDragStart = e.GetPosition(aiFloatingLayer);
            aiFloatingDragPosition = new Point(Avalonia.Controls.Canvas.GetLeft(aiFloatingHost), Avalonia.Controls.Canvas.GetTop(aiFloatingHost));
            e.Pointer.Capture(grip); e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (aiFloatingDragStart is not { } start) return;
            aiFloatingPinnedPosition = aiFloatingDragPosition + (e.GetPosition(aiFloatingLayer) - start);
            RefreshAiFloatingPosition(); e.Handled = true;
        };
        grip.PointerReleased += (_, e) =>
        { if (aiFloatingDragStart == null) return; aiFloatingDragStart = null; e.Pointer.Capture(null); e.Handled = true; };
        grip.PointerCaptureLost += (_, _) => aiFloatingDragStart = null;
        aiHeaderReference = AiGenerationControls.IconAction(Icons.Image, "AiAddReference", "Add reference image · paste with Ctrl+V or drag files here", () => _ = PickAiReferences());
        aiEnhance = AiDialogs.EnhanceButton(aiFloatingPrompt, settings, aiTasks, () => EditorTask, () => PanelEngine(EditorTask));
        aiAdvanced = AiGenerationControls.IconAction(Icons.Gear, "AiAdvanced", "Generation settings", () => _ = ShowAiAdvanced());
        aiAdvanced.MinWidth = 0;
        void ToggleCollapsed()
        {
            settings.AiFloatingCollapsed = !settings.AiFloatingCollapsed;
            settings.Save();
            RefreshAiUi();
        }
        var close = aiFloatingCollapse = Ui.TextButton("Edit selection", ToggleCollapsed);
        close.Classes.Add("flat");
        close.Name = "AiFloatingExpand";
        Locale.Tip(close, "Expand panel");
        aiFloatingHost.DoubleTapped += (_, e) =>
        {
            if (e.Source is not Visual visual || visual.GetSelfAndVisualAncestors().TakeWhile(v => v != aiFloatingHost)
                .Any(v => v is Button or TextBox or ComboBox)) return;
            ToggleCollapsed();
            e.Handled = true;
        };
        grip.DoubleTapped += (_, e) => { ToggleCollapsed(); e.Handled = true; };

        aiFloatingGenerate = Ui.TextButton("Run", () => _ = RunAi(EditorTask, aiFloatingPrompt.Text ?? "", useInlinePrompt: true), accent: true);
        ToolTip.SetShowOnDisabled(aiFloatingGenerate, true);
        aiFloatingMore = AiGenerationControls.IconAction(Icons.MoreActions, "AiMore", "More AI operations", OpenAiFloatingTaskMenu);
        aiVariantButtons = AiGenerationControls.QuickRuns(count =>
            _ = RunAi(EditorTask, aiFloatingPrompt.Text ?? "", useInlinePrompt: true, runCount: count));
        var run = AiGenerationControls.RunGroup(aiFloatingGenerate, aiVariantButtons);
        aiEngineCombo = new ComboBox { Width = 141 };
        RefreshEditorModels();
        aiEngineCombo.SelectionChanged += (_, _) =>
        {
            if (refreshingAiEngine || aiEngineCombo.SelectedItem is not ImageModelChoice model) return;
            modelDrafts[EditorTask] = model.Engine.Id;
            RefreshAiUi();
        };
        Locale.Tip(aiEngineCombo, "Image model for this operation");
        RefreshAiReferenceUi();
        aiFloatingAlignment = AiDialogs.AlignmentToggle(settings);
        aiFloatingAlignment.IsCheckedChanged += (_, _) =>
        {
            var enabled = aiFloatingAlignment.IsChecked == true;
            if (settings.AiAlignMaskedEdits == enabled) return;
            settings.AiAlignMaskedEdits = enabled;
            settings.Save();
        };
        aiFloatingTools = new WrapPanel { Orientation = Orientation.Horizontal };
        aiPriceBadge = Ui.Row(2, Icons.Create(Icons.Coin, 14, CreditColor), aiApiCost);
        aiPriceBadge.Name = "AiCreditEstimate";
        aiPriceBadge.Margin = new Thickness(3, 0);
        aiPriceBadge.VerticalAlignment = VerticalAlignment.Center;
        aiPriceBadge.IsVisible = false;
        aiFloatingTools.Children.AddRange([aiEngineCombo, aiPriceBadge, aiHeaderReference, aiEnhance, aiFloatingAlignment, aiAdvanced]);
        var leading = new DockPanel();
        DockPanel.SetDock(grip, Dock.Left);
        leading.Children.Add(grip);
        leading.Children.Add(aiFloatingTools);
        aiFloatingActions = run;
        var bar = new AiPromptToolbar(leading, Ui.Row(3, aiFloatingMore, close, run));
        aiFloatingPrompt.HorizontalAlignment = HorizontalAlignment.Stretch;
        return Ui.Column(AiGenerationControls.EditorSpacing, aiFloatingPrompt, bar, aiReferenceHost, aiFloatingVariants);
    }

    private void RefreshEditorModels()
    {
        if (aiEngineCombo == null) return;
        var task = EditorTask;
        var engine = PanelEngine(task);
        ImageModelChoice[] choices = DirectImageFor(task) is { } direct
            ? [new(direct.Label, direct.Id, engine!)]
            : aiTasks.Engines.ImageModels(task, engine, settings.ComfyModelsFor(settings.ComfyServerUrl), settings.AiModelRoutes);
        refreshingAiEngine = true;
        try
        {
            AiGenerationControls.PresentImageModels(aiEngineCombo, choices);
            aiEngineCombo.SelectedItem = choices.FirstOrDefault(m => m.Engine.Id == engine?.Id);
        }
        finally { refreshingAiEngine = false; }
    }

    private void OpenAiFloatingTaskMenu()
    {
        if (aiFloatingMore == null) return;
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var task in Enum.GetValues<AiTaskKind>().Where(task => task is not (AiTaskKind.GenerativeFill or AiTaskKind.SelectSubject)))
        {
            var availability = AiAvailability(task);
            var item = new MenuItem
            {
                Header = task.DisplayName(),
                IsEnabled = availability.Available && aiTasks.Operation?.Status is not (AiOperationStatus.Queued or AiOperationStatus.Running)
            };
            if (availability.Reason != null) Locale.Tip(item, availability.Reason);
            item.Click += (_, _) => ChooseAiOperation(task);
            flyout.Items.Add(item);
        }
        AddGenerationConnectionMenu(flyout);
        if (modelDrafts.ContainsKey(AiTaskKind.GenerativeFill))
        {
            var save = new MenuItem { Header = Locale.T("Use this model by default") };
            save.Click += (_, _) =>
            {
                if (aiEngineCombo?.SelectedItem is not ImageModelChoice choice) return;
                settings.AiTaskModelKeys[nameof(AiTaskKind.GenerativeFill)] = choice.ModelKey;
                settings.AiTaskEngineIds[nameof(AiTaskKind.GenerativeFill)] = choice.Engine.Id;
                settings.Save();
                modelDrafts.Remove(AiTaskKind.GenerativeFill);
            };
            flyout.Items.Add(new Separator()); flyout.Items.Add(save);
        }
        flyout.ShowAt(aiFloatingMore);
    }

    private bool ShowSelectionPanel => !DockEditorVisible && previewContext == null && session?.Selection != null && !session.IsPaintingSelection
        && session.Tool != Tool.RemoveObject && !(session.Tool == Tool.Wand && session.WandMode == WandMode.Object && canvas.IsDragging)
        && (DirectImageFor(AiTaskKind.GenerativeFill) != null || aiTasks.ConnectionState == ComfyConnectionState.Connected);

    private void RefreshAiFloatingUi()
    {
        var visible = ShowSelectionPanel;
        aiFloatingHost.IsVisible = visible;
        var expanded = DockEditorVisible || !settings.AiFloatingCollapsed;
        if (aiInlineAddReference != null) aiInlineAddReference.IsVisible = !DockEditorVisible;
        aiReferenceHost.IsVisible = expanded && aiReferences.Count > 0;
        aiFloatingPrompt.IsVisible = expanded && EditorTask != AiTaskKind.GenerativeExpand;
        if (aiEnhance != null) aiEnhance.IsVisible = EditorTask != AiTaskKind.GenerativeExpand;
        if (aiFloatingActions != null) aiFloatingActions.IsVisible = expanded;
        if (aiHeaderReference != null) aiHeaderReference.IsVisible = expanded && (DockEditorVisible || aiReferences.Count == 0);
        if (aiFloatingTools != null) aiFloatingTools.IsVisible = !settings.AiFloatingCollapsed;
        if (aiFloatingAlignment != null) aiFloatingAlignment.IsChecked = settings.AiAlignMaskedEdits;
        if (aiFloatingMore != null) aiFloatingMore.IsVisible = !settings.AiFloatingCollapsed;
        if (aiFloatingCollapse != null) aiFloatingCollapse.IsVisible = settings.AiFloatingCollapsed && !DockEditorVisible;
        if (!visible && !DockEditorVisible) return;
        var busy = aiTasks.Operation?.Status is AiOperationStatus.Queued or AiOperationStatus.Running;
        var task = EditorTask;
        var direct = DirectImageFor(task);
        var fillEngine = PanelEngine(task);
        var fillProfile = AiOptions(task, fillEngine);
        if (aiFloatingAlignment != null) aiFloatingAlignment.IsEnabled = task != AiTaskKind.GenerateImage;
        if (aiHeaderReference != null) aiHeaderReference.IsEnabled = aiReferences.Count < 6;
        if (aiEngineCombo != null)
        {
            if (direct != null && (aiEngineCombo.SelectedItem is not ImageModelChoice choice || choice.Name != direct.Label))
                RefreshEditorModels();
            var index = aiEngineCombo.Items.OfType<ImageModelChoice>().ToList().FindIndex(model => model.Engine.Id == fillEngine?.Id);
            refreshingAiEngine = true;
            try { if (index >= 0 && aiEngineCombo.SelectedIndex != index) aiEngineCombo.SelectedIndex = index; }
            finally { refreshingAiEngine = false; }
            aiEngineCombo.IsEnabled = !busy && direct == null;
        }
        if (aiFloatingGenerate != null) aiFloatingGenerate.IsEnabled = !busy && AiAvailability(task).Available && (!DockEditorVisible || task != AiTaskKind.GenerateImage || !string.IsNullOrWhiteSpace(aiFloatingPrompt.Text));
        if (aiVariantButtons != null)
        {
            aiVariantButtons.IsVisible = AiGenerationControls.SupportsVariants(task);
            aiVariantButtons.IsEnabled = aiFloatingGenerate?.IsEnabled == true;
        }
        if (aiFloatingGenerate != null) AiGenerationControls.ShowBusy(aiFloatingGenerate, busy);
        if (aiFloatingMore != null) aiFloatingMore.IsEnabled = !busy;
        var operation = aiTasks.Operation;
        var status = aiLastError ?? (busy ? operation?.Stage : AiAvailability(task).Reason)
            ?? (direct == null && DockEditorVisible && aiTasks.ConnectionState != ComfyConnectionState.Connected ? "ComfyUI · " + aiTasks.ConnectionState.ToString().ToLowerInvariant() : null) ?? (operation?.Status switch
        {
            AiOperationStatus.Queued or AiOperationStatus.Running => operation.Stage ?? "Working…",
            AiOperationStatus.Cancelled => "Cancelled",
            AiOperationStatus.Failed => operation.Error ?? "AI operation failed",
            _ => ""
        });
        if (aiFloatingGenerate != null) Locale.Tip(aiFloatingGenerate, string.IsNullOrEmpty(status) ? "Run one variant" : status);
        aiPriceBadge.IsVisible = expanded && fillEngine?.PaidApi == true;
        PartnerPriceEstimate? estimate = null;
        if (aiPriceBadge.IsVisible)
        {
            var count = (task == AiTaskKind.GenerateImage ? 0 : 1) + aiReferences.Count; // cropped source and genuine user references only
            estimate = PartnerPricing.Estimate(aiTasks.ServerCapabilities, fillEngine!.ApiModel!, fillProfile.ApiQuality, "Custom", count, 1);
            aiApiCost.Text = estimate?.RoundedCredits(1) ?? "—";
            var tip = estimate == null ? "Paid API · estimate unavailable" : $"One variant · {estimate.Label} (ComfyUI credits)";
            Locale.Tip(aiPriceBadge, tip);
            Avalonia.Automation.AutomationProperties.SetName(aiPriceBadge, tip);
        }
        if (aiVariantButtons != null)
            foreach (var button in aiVariantButtons.Children.OfType<Button>())
            {
                var variants = button.Name == "AiVariants2" ? 2 : 3;
                var label = $"Run {variants} variants";
                Locale.Tip(button, estimate == null ? label : $"{label} · {estimate.RoundedCredits(variants)} ComfyUI credits total");
            }
        if (DockEditorVisible)
        {
            // Four reference cells fit in the standard 296 px dock. Keep the user's base height for the editor itself.
            var rows = (aiReferences.Count + 3) / 4;
            dock.SetExtraHeight(generationSection, rows * (56 + AiGenerationControls.EditorSpacing) + (aiFloatingVariants.IsVisible ? 34 : 0));
        }
        if (visible) Avalonia.Threading.Dispatcher.UIThread.Post(RefreshAiFloatingPosition);
    }

    private void RefreshAiFloatingPosition()
    {
        if (!aiFloatingHost.IsVisible || session?.Selection is not { } selection) return;
        if (!ReferenceEquals(selection, floatingBoundsMask))
        {
            floatingBounds = SelectionMask.Bounds(selection);
            floatingBoundsMask = selection;
        }
        var bounds = floatingBounds;
        if (bounds.IsEmpty) return;
        var topLeft = canvas.ToScreen(new SKPoint(bounds.Left, bounds.Top));
        var bottomRight = canvas.ToScreen(new SKPoint(bounds.Right, bounds.Bottom));
        var width = Math.Min(settings.AiFloatingCollapsed ? 210 : 520, Math.Max(1, canvas.Bounds.Width - 16));
        aiFloatingHost.Width = width;
        var height = (aiFloatingHost.Bounds.Height > 0 ? aiFloatingHost.Bounds.Height : settings.AiFloatingCollapsed ? 44 : 82);
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
            Title = Locale.T("Choose Reference Images"), AllowMultiple = true, FileTypeFilter = [ImageType]
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
        Avalonia.Automation.AutomationProperties.SetName(host, $"Reference images ({aiReferences.Count}/6)");
        var items = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            ItemSpacing = AiGenerationControls.EditorSpacing, LineSpacing = AiGenerationControls.EditorSpacing };
        foreach (var item in aiReferences.ToArray())
        {
            const double side = 56, radius = 6;
            var preview = new Image { Source = item.Preview, Width = side, Height = side, Stretch = Stretch.UniformToFill,
                Clip = new RectangleGeometry(new Rect(0, 0, side, side), radius, radius) };
            var remove = new Button
            {
                Content = Icons.Create(Icons.Close, 16, Brushes.White), MinWidth = 0, MinHeight = 0,
                Width = 18, Height = 18, Padding = new Thickness(0), Margin = new Thickness(0, 3, 3, 0),
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Color.Parse("#D9222326")), Foreground = Brushes.White
            };
            Avalonia.Automation.AutomationProperties.SetName(remove, "Remove reference: " + item.Name);
            remove.Click += (_, e) =>
            {
                e.Handled = true;
                aiReferences.Remove(item);
                item.Dispose();
                RefreshAiReferenceUi();
            };
            remove.SetValue(Panel.ZIndexProperty, 1);
            Locale.Tip(remove, "Remove reference");
            var cell = new Grid { Width = side, Height = side };
            var show = new Button { Content = preview, Padding = new Thickness(0), CornerRadius = new CornerRadius(radius),
                BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            show.Click += (_, _) => _ = ShowAiReferencePreview(item, TopLevel.GetTopLevel(host) as Window);
            Locale.Tip(show, "Preview reference: " + item.Name);
            cell.Children.Add(show);
            cell.Children.Add(remove);
            Locale.Tip(cell, item.Name);
            items.Children.Add(cell);
        }
        if (host == aiReferenceHost) aiInlineAddReference = null;
        if (aiReferences.Count is > 0 and < 6)
        {
            var add = Ui.IconButton(Icons.Image, "Add reference image", () => _ = PickAiReferences(), 23);
            add.Width = aiReferences.Count == 0 ? 36 : 56;
            add.Height = aiReferences.Count == 0 ? 30 : 56;
            add.CornerRadius = new CornerRadius(6);
            add.BorderBrush = Palette.Secondary; add.BorderThickness = new Thickness(1);
            Avalonia.Automation.AutomationProperties.SetName(add, "Add reference image");
            add.MinWidth = 0;
            add.Background = Palette.Panel;
            add.Padding = new Thickness(0);
            add.VerticalContentAlignment = VerticalAlignment.Center;
            add.HorizontalContentAlignment = HorizontalAlignment.Center;
            add.FontSize = 22;
            Locale.Tip(add, aiReferences.Count == 0 ? "Add reference image · paste with Ctrl+V or drag files here" : "Add another reference image");
            if (host == aiReferenceHost) aiInlineAddReference = add;
            items.Children.Add(add);
        }
        host.Children.Add(items);
    }

    private async Task ShowAiReferencePreview(AiReferenceItem item, Window? owner = null)
    {
        // Hold a separate image while the preview is open: removing a reference must not
        // dispose pixels still being rendered by another window.
        using var preview = Ui.ToAvaloniaBitmap(item.Pixels, 1040, DisplayProfile);
        var target = owner ?? this;
        var canvasCenter = canvas.PointToScreen(new Point(canvas.Bounds.Width / 2, canvas.Bounds.Height / 2));
        var previewScale = Math.Max(1, 220d / Math.Max(item.Pixels.Width, item.Pixels.Height));
        var scale = Math.Min(previewScale, Math.Min(Math.Max(64, canvas.Bounds.Width - 48) / item.Pixels.Width,
            Math.Max(64, canvas.Bounds.Height - 48) / item.Pixels.Height));
        var width = Math.Max(32, item.Pixels.Width * scale);
        var height = Math.Max(32, item.Pixels.Height * scale);
        var dialog = new Window { Title = item.Name, Width = width, Height = height,
            WindowDecorations = WindowDecorations.None, CanResize = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent] };
        var dpi = target.RenderScaling;
        dialog.Position = new PixelPoint(canvasCenter.X - (int)(width * dpi / 2), canvasCenter.Y - (int)(height * dpi / 2));
        var close = Ui.IconButton(Icons.Close, "Close preview", dialog.Close, 16);
        close.Width = close.Height = 26; close.MinWidth = close.MinHeight = 0; close.Padding = new Thickness(4);
        close.HorizontalAlignment = HorizontalAlignment.Right; close.VerticalAlignment = VerticalAlignment.Top;
        close.Margin = new Thickness(4); close.CornerRadius = new CornerRadius(5);
        close.Background = Palette.Panel; close.Foreground = Palette.Foreground;
        dialog.Content = new Grid { Children = { new Image { Source = preview, Stretch = Stretch.Uniform }, close } };
        dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; dialog.Close(); } };
        await dialog.ShowDialog(target);
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
            aiReferences.Add(new AiReferenceItem(pixels, Ui.ToAvaloniaBitmap(pixels, 512, DisplayProfile), name));
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
        if (textBox == null || Clipboard == null) { await PasteAiReference(); return; }
        try
        {
            var text = await Clipboard.TryGetTextAsync();
            if (string.IsNullOrEmpty(text)) { await PasteAiReference(); return; }
            var current = textBox.Text ?? "";
            var start = Math.Min(textBox.SelectionStart, textBox.SelectionEnd);
            var end = Math.Max(textBox.SelectionStart, textBox.SelectionEnd);
            textBox.Text = current[..start] + text + current[end..];
            textBox.CaretIndex = start + text.Length;
        }
        catch { /* A clipboard that changed mid-paste is harmless. */ }
    }

    internal void EnableAiReferenceDrop(Control host)
    {
        DragDrop.SetAllowDrop(host, true);
        host.AddHandler(DragDrop.DropEvent, OnAiReferenceDrop);
        host.AddHandler(DragDrop.DragOverEvent, (_, e) => { e.DragEffects = DragDropEffects.Copy; e.Handled = true; });
    }

    private void OnAiReferenceDrop(object? sender, DragEventArgs e)
    {
        var paths = e.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).OfType<string>() ?? [];
        AddAiReferencePaths(paths);
        e.Handled = true;
    }

    private async Task ShowAiSettings(int page = 0, string? field = null)
    {
        try
        {
            var previousRoute = settings.ImageGenerationRoute;
            if (!await AiDialogs.SettingsDialog(this, settings, aiTasks, page, field)) return;
            if (previousRoute != settings.ImageGenerationRoute) manualDockTask = null;
            dock.SetTabbed(settings.TabbedPanels);
            aiTasks.ConnectionTimeoutSeconds = settings.ComfyConnectionTimeoutSeconds;
            aiTasks.SelectedEngine = aiTasks.Engines.Find(settings.AiEngineId);
            modelDrafts.Clear();
            RefreshEditorModels();
            RefreshAiUi();
        }
        catch (Exception error) { ShowProblem(error.Message); }
    }

    private async Task ShowAiAdvanced()
    {
        var previous = aiTasks.SelectedEngine;
        var task = DockEditorVisible || session?.Selection != null ? EditorTask : AiTaskKind.ImageEdit;
        if (DirectImageFor(task) != null) { await ShowAiSettings(4); return; }
        aiTasks.SelectedEngine = PanelEngine(task);
        try { await AiDialogs.Advanced(this, settings, aiTasks, task); }
        finally { aiTasks.SelectedEngine = previous; RefreshAiUi(); }
    }

    private async Task EditLayerTags(Composa.Model.Layer layer)
    {
        if (session == null) return;
        if (await AiDialogs.LayerTags(this, layer, settings) is { } tags) session.SetLayerTags(layer, tags);
    }

    private async Task RunAi(AiTaskKind task, string initialPrompt = "", bool useInlinePrompt = false, SKRectI? selectionRegion = null, SelectionMode selectionOperation = SelectionMode.Replace, int runCount = 1, SKPointI? selectionPoint = null)
    {
        if (task == AiTaskKind.GenerativeExpand) initialPrompt = "";
        aiLastError = null;
        var target = session;
        var direct = DirectImageFor(task);
        var availability = AiAvailability(task);
        if (!availability.Available) { AiFailed(availability.Reason ?? "This AI task is unavailable."); return; }
        if (aiTasks.Operation?.Status is AiOperationStatus.Queued or AiOperationStatus.Running) { AiFailed("Another AI operation is already running."); return; }
        if (task == AiTaskKind.Upscale && session is { } upscaleSession)
        {
            var area = upscaleSession.Selection == null ? upscaleSession.Document.Bounds : SelectionMask.Bounds(upscaleSession.Selection);
            if (!await AiDialogs.Upscale(this, settings, area.Width, area.Height, upscaleSession.Selection != null, PanelEngine(task), aiTasks)) return;
        }
        var aspect = AiAspect(task);
        var profile = AiOptions(task);
        var fallback = profile.OriginalSize
            ? task is AiTaskKind.Upscale or AiTaskKind.ObjectSelection or AiTaskKind.SelectSubject ? aspect : AiDimensions.OriginalGenerationSize(aspect.Width, aspect.Height)
            : AiDimensions.FromMegapixels(profile.Megapixels, aspect.Width, aspect.Height);
        AiPromptResult? options;
        if (useInlinePrompt || task == AiTaskKind.RemoveObject && session?.Selection != null || task is AiTaskKind.GenerativeExpand or AiTaskKind.SelectSubject or AiTaskKind.ObjectSelection or AiTaskKind.Upscale)
            options = new(initialPrompt, fallback.Width, fallback.Height, profile.Seed, Variants: runCount);
        else
        {
            var host = new StackPanel { Name = "AiDialogReferences" };
            aiDialogReferences = new AiReferenceEditor(host, () => aiReferences.Count, PasteAiReferenceOrText, PickAiReferences);
            BuildAiReferenceUi(host);
            try { options = await AiDialogs.Prompt(this, task, settings, aspect.Width, aspect.Height, initialPrompt, aiTasks, aiReferences.Count,
                session?.Selection != null && !(task == AiTaskKind.GenerativeExpand && session.Tool == Tool.Crop), aiDialogReferences,
                session?.Document.Bounds, session?.Selection is { } selected ? AutomaticAiMask.ContourBounds(selected) : null,
                modelDrafts.GetValueOrDefault(task), direct?.PromptProfile()); }
            finally { aiDialogReferences = null; }
        }
        RefreshAiUi();
        if (options == null) return;
        if (direct != null && target != null && !sessions.Contains(target)) return;
        if (task == AiTaskKind.GenerateImage) { settings.GenerateImagePrompt = options.Prompt; settings.Save(); }
        if (task == AiTaskKind.ImageEdit) { settings.ImageEditPrompt = options.Prompt; settings.Save(); }
        if ((direct != null ? target : session) == null)
        {
            if (task != AiTaskKind.GenerateImage) return;
            AddSession(EditorSession.NewCanvas(options.Width, options.Height));
            target = session;
        }
        var seed = options.Seed < 0 ? Random.Shared.NextInt64(long.MaxValue) : options.Seed;
        var engine = direct?.PromptProfile() ?? (options.EngineId == null ? PanelEngine(task) : aiTasks.EngineFor(task, options.EngineId));
        if (direct == null && options.EngineId != null && engine != null) modelDrafts[task] = engine.Id;
        profile = AiOptions(task, engine);
        try
        {
            var request = new AiTaskRequest
            {
                Task = task,
                Scene=profile.Scene.Normalized(),
                AlignMaskedEdits = settings.AiAlignMaskedEdits,
                EngineId = engine?.Id,
                SelectionRegion = selectionRegion, SelectionOperation = selectionOperation, SelectionPoint = selectionPoint,
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
                    Variants = Math.Clamp(options.Variants, 1, 3), VariantMode = profile.VariantMode,
                    Loras = profile.ActiveLoras(engine),
                    UpscaleFactor = profile.UpscaleFactor == 4 ? 4 : 2,
                    Values = new Dictionary<string, object?> { ["fluxMemory"] = profile.FluxMemory,
                        ["apiQuality"] = profile.ApiQuality, ["apiSize"] = "Custom", ["imageOriginalSize"] = profile.OriginalSize }
                }
            };
            if (direct != null)
            {
                var destination = target!;
                await aiTasks.RunDirectImageAsync(new EditorCommandService(destination), direct, request, () => sessions.Contains(destination));
                return;
            }
            await aiTasks.RunAsync(new EditorCommandService(session!), request);
            if (task == AiTaskKind.GenerateImage && engine?.Binding(AiTaskKind.ImageEdit) != null && modelDrafts.ContainsKey(task))
            { modelDrafts[AiTaskKind.ImageEdit] = engine.Id; RefreshEditorModels(); }
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
        if(task is AiTaskKind.MatchToScene or AiTaskKind.Harmonize or AiTaskKind.Relight or AiTaskKind.ChangeBackground && session!=null)
            return (session.Document.Width,session.Document.Height);
        if (task == AiTaskKind.GenerativeExpand && session?.Tool == Tool.Crop && canvas.CropRect is { Width: > 0, Height: > 0 } crop)
            return (Math.Max(1, (int)Math.Round(crop.Width)), Math.Max(1, (int)Math.Round(crop.Height)));
        if (task is AiTaskKind.GenerativeFill or AiTaskKind.RemoveObject or AiTaskKind.Harmonize or AiTaskKind.Relight or AiTaskKind.GenerativeExpand
            && session?.Selection is { } selection && AutomaticAiMask.ContourBounds(selection) is { IsEmpty: false } bounds)
        {
            var context = AutomaticAiMask.Geometry(bounds, session.Document.Bounds).Bounds;
            return (context.Width, context.Height);
        }
        return session == null ? AiDimensions.FromMegapixels(settings.AiMegapixels, 1, 1) : (session.Document.Width, session.Document.Height);
    }

    private static bool IsEmpty(SKBitmap bitmap) => bitmap.GetPixelSpan().IndexOfAnyExcept((byte)0) < 0;
}
