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
using Composa.App.Controls;
using Composa.Model;
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
    private readonly Dictionary<AiTaskKind, ImageModelChoice> modelDrafts = [];
    private Button aiEnhance = null!, aiAdvanced = null!;
    private ToggleButton aiPixelAlignment = null!;
    private readonly TextBlock aiFloatingOperation = new() { Name = "AiFloatingOperation", FontWeight = FontWeight.SemiBold };
    private ImageGenerationRoute PanelRoute(AiTaskKind task) =>
        modelDrafts.GetValueOrDefault(ImageOperationModels.ModelTask(task))?.Route ?? settings.ImageRouteFor(task);
    private DirectImageGenerator? DirectImageFor(AiTaskKind task)
    {
        if (modelDrafts.TryGetValue(ImageOperationModels.ModelTask(task), out var selected))
            return selected.Direct;
        return DirectImageGenerator.IsDirect(settings.ImageRouteFor(task)) ? DirectImageGenerator.For(settings, ImageOperationModels.ModelTask(task)) : null;
    }
    private EngineProfile? PanelEngine(AiTaskKind task) =>
        modelDrafts.GetValueOrDefault(ImageOperationModels.ModelTask(task))?.Engine ?? DirectImageFor(task)?.PromptProfile() ?? aiTasks.EngineFor(task);
    private ImageModelChoice? PanelChoice(AiTaskKind task)
    {
        task = ImageOperationModels.ModelTask(task);
        if (modelDrafts.TryGetValue(task, out var chosen)) return chosen;
        if (PanelRoute(task) == ImageGenerationRoute.BuiltIn) return ImageOperationModels.LocalSelection(settings);
        if (DirectImageFor(task) is { } direct) return ImageOperationModels.DirectChoice(direct,
            ImageOperationModels.ApiCatalog(settings).GetValueOrDefault(direct.Model) ?? "", settings.ImageApiDisplayName);
        return ImageOperationModels.ComfyDefault(aiTasks.Engines, task, aiTasks.EngineFor(task),
            settings.ComfyModelsFor(settings.ComfyServerUrl), settings.Workflows,
            aiTasks.ConnectionState == ComfyConnectionState.Connected &&
                aiTasks.ConnectedServerUrl == settings.ComfyServerUrl ? aiTasks.ServerCapabilities : null);
    }
    private bool? PanelUsesAssignedWorkflow(AiTaskKind task) =>
        modelDrafts.GetValueOrDefault(ImageOperationModels.ModelTask(task))?.AssignedWorkflow;
    private AiOperationSettings AiOptions(AiTaskKind task, EngineProfile? engine = null)
    {
        engine ??= PanelEngine(task);
        return settings.OperationFor(engine?.Id, task, engine?.PaidApi == true);
    }
    private Button aiTaskMenuButton = null!;
    private ComfyConnectionState? shownConnection;
    private ComfyServerCapabilities? modelListCapabilities;
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
    private readonly AiPromptField aiFloatingPrompt = AiGenerationControls.PromptField("Describe the edit…");
    private readonly StackPanel aiReferenceHost = new() { Spacing = 5 };
    private Button aiWorkflowLink = null!;
    private readonly List<AiReferenceItem> aiReferences = [];
    private AiReferenceEditor? aiDialogReferences;
    private bool refreshingAiEngine;
    private WrapPanel? aiFloatingTools;
    private Button? aiFloatingCollapse, aiHeaderReference;
    private AiTaskKind aiDialogReferenceTask;
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
        if (!AiGenerationControls.IsOperationEnabled(task)) return new(task, false, "Unavailable");
        if (task is AiTaskKind.ObjectSelection or AiTaskKind.SelectSubject && PanelRoute(task) == ImageGenerationRoute.BuiltIn)
            return new(task, session != null && !localSelectionBusy, session == null ? "Open a document first." : null);
        if (DirectImageFor(task) is { } direct)
        {
            if (direct.Route == ImageGenerationRoute.DirectApi && task is not (AiTaskKind.GenerateImage or AiTaskKind.Upscale or AiTaskKind.ObjectSelection or AiTaskKind.SelectSubject)
                && ImageOperationModels.GenerationOnly(settings)?.Contains(direct.Model) == true)
                return new(task, false, "This API model generates images only. Choose an image editing model.");
            var reason = direct.Availability(task, aiReferences.Count);
            return reason == null ? AiTaskAvailability.Resolve(session, direct.PromptProfile(), task, CropExpands)
                : new(task, false, reason);
        }
        var engine = PanelEngine(task);
        if ((PanelUsesAssignedWorkflow(task) ?? !settings.Workflows.FactoryDefaults.Contains(task)) && settings.Workflows.Assignments.ContainsKey(task))
        {
            if (session == null && task != AiTaskKind.GenerateImage) return new(task, false, "Open a document first.");
            if (task.RequiresSelection() && session?.Selection == null) return new(task, false, "Make a selection first.");
            return new(task, true, null);
        }
        return AiTaskAvailability.Resolve(session, engine, task, CropExpands);
    }

    private void ChooseAiOperation(AiTaskKind task)
    {
        if (!CanRunAi(task)) return;
        bool immediate = task is AiTaskKind.RemoveObject or AiTaskKind.GenerativeExpand;
        bool sceneEdit = task is AiTaskKind.Harmonize or AiTaskKind.ChangeBackground;
        if (IsPanelEdit(task) && ShowSelectionPanel)
        {
            manualFloatingTask = task;
            SetFloatingCollapsed(false);
            aiFloatingPrompt.Focus();
            return;
        }
        if (sceneEdit && !DockEditorVisible)
        {
            SetCanvasOnly(false);
            dock.SetVisible(generationSection, true);
            dock.Activate(generationSection);
        }
        if (DockEditorVisible && (IsPanelEdit(task) || immediate || task == AiTaskKind.GenerateImage))
        {
            manualDockTask = task;
            RefreshAiUi();
            if (!immediate) { aiFloatingPrompt.Focus(); return; }
        }
        if (task == AiTaskKind.ObjectSelection) { SelectSubject(); return; }
        if (task == AiTaskKind.RemoveObject && session?.Selection == null) { SelectTool(Tool.RemoveObject); return; }
        _ = RunAi(task);
    }

    private static bool IsPanelEdit(AiTaskKind task) => task is AiTaskKind.GenerativeFill or AiTaskKind.ImageEdit
        or AiTaskKind.Harmonize or AiTaskKind.ChangeBackground;

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
        shownConnection = aiTasks.ConnectionState;
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
        if (shownConnection != aiTasks.ConnectionState || !ReferenceEquals(modelListCapabilities, aiTasks.ServerCapabilities))
            RefreshEditorModels();
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
        if ((EditorTask is AiTaskKind.GenerativeFill or AiTaskKind.RemoveObject || EditorTask == AiTaskKind.GenerativeExpand && session?.Tool != Tool.Crop) && settings.AiShowContextBounds
            && (DirectImageFor(EditorTask) != null || aiTasks.ConnectionState == ComfyConnectionState.Connected)
            && session is { Selection: { } selection, IsPaintingSelection: false } && !canvas.IsDragging)
        {
            var bounds = AutomaticAiMask.ContourBounds(selection);
            if (!bounds.IsEmpty)
                context = AutomaticAiMask.Geometry(bounds, session.Document.Bounds, settings.AiSelectionContextPercent).Bounds;
        }
        canvas.AiContextBounds = context;
    }

    private Button BuildAiTaskMenu()
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var task in AiGenerationControls.VisibleOperations)
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
        var button = new Button { Name = "AiTaskMenu", Content = Locale.T("AI ▾"), Flyout = flyout, MinWidth = 0 };
        Locale.Tip(button, "Choose AI operation");
        return button;
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
        aiHeaderReference = AiGenerationControls.IconAction(Icons.Image, "AiAddReference",
            "Add reference image · paste with Ctrl+V or drag files and layers here", () => _ = PickAiReferences());
        aiEnhance = AiDialogs.EnhanceButton(aiFloatingPrompt, settings, aiTasks, () => EditorTask, () => PanelEngine(EditorTask),
            () => aiReferences.Count);
        aiAdvanced = AiGenerationControls.IconAction(Icons.Gear, "AiAdvanced", "Generation settings", () => _ = ShowAiAdvanced());
        aiAdvanced.MinWidth = 0;
        aiPixelAlignment = AiGenerationControls.AlignmentToggle(settings.AiAlignMaskedEdits, align =>
        {
            settings.AiAlignMaskedEdits = align;
            settings.Save();
        });
        var workflowName = Ui.Label("", Palette.Secondary, 11);
        workflowName.TextTrimming = TextTrimming.CharacterEllipsis;
        aiWorkflowLink = Ui.TextButton("Workflows", () =>
        {
            var task = EditorTask;
            var id = settings.Workflows.ActiveId(task, PanelEngine(task)?.Id, PanelUsesAssignedWorkflow(task));
            _ = ShowAiSettings(4, id == null ? null : "workflow:" + id);
        });
        aiWorkflowLink.Content = workflowName;
        aiWorkflowLink.Name = "AiActiveWorkflow";
        aiWorkflowLink.Classes.Add("flat");
        aiWorkflowLink.HorizontalAlignment = HorizontalAlignment.Stretch;
        aiWorkflowLink.IsVisible = false;
        void ToggleCollapsed() => SetFloatingCollapsed(!settings.AiFloatingCollapsed);
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
        aiFloatingMore = AiGenerationControls.IconAction(Icons.MoreActions, "AiMore", "More AI operations", () => { });
        aiFloatingMore.Flyout = BuildAiFloatingTaskMenu();
        aiVariantButtons = AiGenerationControls.QuickRuns(count =>
            _ = RunAi(EditorTask, aiFloatingPrompt.Text ?? "", useInlinePrompt: true, runCount: count));
        var run = AiGenerationControls.RunGroup(aiFloatingGenerate, aiVariantButtons);
        aiEngineCombo = new ComboBox { Width = AiGenerationControls.ModelPickerWidth };
        RefreshEditorModels();
        aiEngineCombo.SelectionChanged += (_, _) =>
        {
            if (refreshingAiEngine || aiEngineCombo.SelectedItem is not ImageModelChoice model) return;
            if (!model.Available) { RefreshEditorModels(); return; }
            modelDrafts[ImageOperationModels.ModelTask(EditorTask)] = model;
            RefreshAiUi();
        };
        Locale.Tip(aiEngineCombo, "Image model for this operation");
        RefreshAiReferenceUi();
        aiFloatingTools = new WrapPanel { Orientation = Orientation.Horizontal };
        aiPriceBadge = Ui.Row(2, Icons.Create(Icons.Coin, 14, CreditColor), aiApiCost);
        aiPriceBadge.Name = "AiCreditEstimate";
        aiPriceBadge.Margin = new Thickness(3, 0);
        aiPriceBadge.VerticalAlignment = VerticalAlignment.Center;
        aiPriceBadge.IsVisible = false;
        aiFloatingTools.Children.AddRange([aiEngineCombo, aiPriceBadge, aiHeaderReference, aiEnhance, aiPixelAlignment, aiAdvanced]);
        var leading = new DockPanel();
        DockPanel.SetDock(grip, Dock.Left);
        leading.Children.Add(grip);
        leading.Children.Add(aiFloatingTools);
        aiFloatingActions = run;
        var bar = new AiPromptToolbar(leading, Ui.Row(3, aiFloatingMore, close, run));
        aiFloatingPrompt.HorizontalAlignment = HorizontalAlignment.Stretch;
        return Ui.Column(AiGenerationControls.EditorSpacing, aiFloatingOperation, aiFloatingPrompt, bar, aiWorkflowLink, aiReferenceHost, aiFloatingVariants);
    }

    private void RefreshEditorModels()
    {
        if (aiEngineCombo == null) return;
        modelListCapabilities = aiTasks.ServerCapabilities;
        var task = ImageOperationModels.ModelTask(EditorTask);
        var selected = PanelChoice(task);
        var direct = DirectImageGenerator.For(settings, task) with { Route = ImageGenerationRoute.DirectApi };
        bool comfyConfigured, credits, apiEnabled;
        try { _ = ComfyServerAddress.Parse(settings.ComfyServerUrl); comfyConfigured = true; }
        catch (FormatException) { comfyConfigured = false; }
        try { credits = !string.IsNullOrWhiteSpace(aiTasks.Credential); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { credits = false; }
        try
        {
            apiEnabled = !string.IsNullOrWhiteSpace(DirectImageGenerator.ReadKey(DirectImageGenerator.Endpoint(direct.ApiAddress)));
        }
        catch (Exception error) when (error is FormatException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { apiEnabled = false; }
        var choices = ImageOperationModels.Choices(aiTasks, task, aiTasks.EngineFor(task), settings.ComfyModelsFor(settings.ComfyServerUrl),
            settings.Workflows, settings, comfyConfigured, aiTasks.ConnectionState == ComfyConnectionState.Connected, credits,
            ImageOperationModels.DirectChoices(direct, ImageOperationModels.ApiCatalog(settings).Select(pair => (pair.Key, pair.Value)),
                settings.ImageApiDisplayName, apiEnabled, ImageOperationModels.GenerationOnly(settings), ImageOperationModels.ApiUpscalers(settings)), selected);
        refreshingAiEngine = true;
        try
        {
            AiGenerationControls.PresentImageModels(aiEngineCombo, choices);
            aiEngineCombo.SelectedItem = choices.FirstOrDefault(choice => choice.SelectionKey == selected?.SelectionKey);
        }
        finally { refreshingAiEngine = false; }
    }

    private MenuFlyout BuildAiFloatingTaskMenu()
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var task in AiGenerationControls.VisibleOperations)
        {
            var item = new MenuItem
            {
                Header = Locale.T(task.DisplayName()),
                ToggleType = IsPanelEdit(task) ? MenuItemToggleType.Radio : MenuItemToggleType.None
            };
            flyout.Opening += (_, _) =>
            {
                item.IsChecked = EditorTask == task;
                item.IsEnabled = CanRunAi(task);
                Locale.Tip(item, AiAvailability(task).Reason);
            };
            ToolTip.SetShowOnDisabled(item, true);
            item.Click += (_, _) => ChooseAiOperation(task);
            flyout.Items.Add(item);
        }
        return flyout;
    }

    private void SetFloatingCollapsed(bool collapsed)
    {
        if (settings.AiFloatingCollapsed != collapsed)
        {
            settings.AiFloatingCollapsed = collapsed;
            settings.Save();
        }
        RefreshAiUi();
    }

    private bool ShowSelectionPanel => !settings.CanvasOnly && !DockEditorVisible && previewContext == null && session?.Selection != null && !session.IsPaintingSelection
        && session.Tool != Tool.RemoveObject && !(session.Tool == Tool.Wand && session.WandMode == WandMode.Object && canvas.IsDragging)
        && (DirectImageFor(EditorTask) != null || DirectImageFor(AiTaskKind.GenerativeFill) != null || aiTasks.ConnectionState == ComfyConnectionState.Connected);

    private void RefreshAiFloatingUi()
    {
        var visible = ShowSelectionPanel;
        aiFloatingHost.IsVisible = visible;
        var expanded = DockEditorVisible || !settings.AiFloatingCollapsed;
        if (aiHeaderReference != null)
        {
            aiHeaderReference.IsVisible = expanded;
            aiHeaderReference.IsEnabled = aiReferences.Count < AiReferenceEditor.Capacity;
        }
        aiReferenceHost.IsVisible = expanded && aiReferences.Count > 0;
        aiFloatingOperation.IsVisible = visible && expanded;
        aiFloatingPrompt.IsVisible = expanded && EditorTask != AiTaskKind.GenerativeExpand;
        aiPixelAlignment.IsChecked = settings.AiAlignMaskedEdits;
        aiPixelAlignment.IsVisible = expanded && AiGenerationControls.SupportsAlignment(EditorTask);
        if (aiEnhance != null) aiEnhance.IsVisible = EditorTask != AiTaskKind.GenerativeExpand;
        if (aiFloatingActions != null) aiFloatingActions.IsVisible = expanded;

        if (aiFloatingTools != null) aiFloatingTools.IsVisible = !settings.AiFloatingCollapsed;
        if (aiFloatingMore != null) aiFloatingMore.IsVisible = !settings.AiFloatingCollapsed;
        if (aiFloatingCollapse != null) aiFloatingCollapse.IsVisible = settings.AiFloatingCollapsed && !DockEditorVisible;
        if (!visible && !DockEditorVisible) return;
        var busy = aiTasks.Operation?.Status is AiOperationStatus.Queued or AiOperationStatus.Running;
        var task = EditorTask;
        aiPixelAlignment.IsEnabled = !busy;
        var direct = DirectImageFor(task);
        var fillEngine = PanelEngine(task);
        var fillProfile = AiOptions(task, fillEngine);

        if (aiEngineCombo != null)
        {
            aiEngineCombo.IsEnabled = !busy && aiEngineCombo.ItemCount > 0;
        }
        if (aiFloatingGenerate != null) aiFloatingGenerate.IsEnabled = !busy && AiAvailability(task).Available && (!DockEditorVisible || task != AiTaskKind.GenerateImage || !string.IsNullOrWhiteSpace(aiFloatingPrompt.Text));
        if (aiVariantButtons != null)
        {
            aiVariantButtons.IsVisible = AiGenerationControls.SupportsVariants(task);
            aiVariantButtons.IsEnabled = aiFloatingGenerate?.IsEnabled == true;
        }
        if (aiFloatingGenerate != null) AiGenerationControls.ShowBusy(aiFloatingGenerate, busy);
        if (aiFloatingMore != null) aiFloatingMore.IsEnabled = !busy;
        var workflowId = settings.Workflows.ActiveId(task, fillEngine?.Id, PanelUsesAssignedWorkflow(task));
        var activeWorkflow = workflowId == null ? null : settings.Workflows.Workflows.GetValueOrDefault(workflowId);
        aiWorkflowLink.IsVisible = expanded && direct == null && activeWorkflow != null;
        if (aiWorkflowLink.Content is TextBlock workflowName) workflowName.Text = activeWorkflow?.Name;
        Locale.Tip(aiWorkflowLink, "Workflow bindings");
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
        var directApi = direct is { Route: ImageGenerationRoute.DirectApi };
        aiPriceBadge.IsVisible = expanded && (directApi || direct == null &&
            (activeWorkflow == null ? fillEngine?.PaidApi == true : ComfyClient.UsesApiCredits(activeWorkflow.Graph)));
        aiPriceBadge.Children[0].IsVisible = !directApi;
        PartnerPriceEstimate? estimate = null;
        if (aiPriceBadge.IsVisible && directApi)
        {
            var previous = aiTasks.LastDirectImageCharge;
            var charge = previous is { } last && last.Generator.Task == direct!.Task &&
                last.Generator.ApiAddress == direct.ApiAddress && last.Generator.Model == direct.Model ? last.Rate : null;
            var catalogRate = ImageOperationModels.ApiRate(settings, direct!);
            aiApiCost.Text = charge?.Label ?? (catalogRate == null ? "—" : "≈" + catalogRate.Label);
            var tip = charge != null ? "Last API request" : catalogRate != null ? "API catalog price per image or request" :
                "The image API has not provided a price.";
            Locale.Tip(aiPriceBadge, tip);
            Avalonia.Automation.AutomationProperties.SetName(aiPriceBadge, Locale.T(tip));
        }
        else if (aiPriceBadge.IsVisible)
        {
            var count = (task == AiTaskKind.GenerateImage ? 0 : 1) + aiReferences.Count; // cropped source and genuine user references only
            if (activeWorkflow == null)
                estimate = PartnerPricing.EstimateForTask(task, aiTasks.ServerCapabilities, fillEngine!.ApiModel!, fillProfile.ApiQuality, count, 1);
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
            const int thumbnailsPerRow = 4;
            var rows = (aiReferences.Count + thumbnailsPerRow - 1) / thumbnailsPerRow;
            var referencesHeight = rows * (ReferenceTileSize + AiGenerationControls.EditorSpacing);
            dock.SetExtraHeight(generationSection, referencesHeight + (aiFloatingVariants.IsVisible ? 34 : 0));
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

    private const double ReferenceTileSize = 60;

    private AiImageMention[] AiImageMentions(AiTaskKind task)
    {
        var first = AiImageMention.FirstReference(task);
        return Enumerable.Range(first, aiReferences.Count).Select(number => new AiImageMention(number)).ToArray();
    }

    private void RefreshPanelReferences()
    {
        BuildAiReferenceUi(aiReferenceHost, EditorTask);
        aiFloatingPrompt.SetImages(AiImageMentions(EditorTask));
    }

    private void RefreshAiReferenceUi()
    {
        RefreshPanelReferences();
        if (aiDialogReferences?.View is StackPanel dialogHost)
            BuildAiReferenceUi(dialogHost, aiDialogReferenceTask);
        aiDialogReferences?.NotifyChanged();
        RefreshAiFloatingUi();

    }

    private void RemoveAiReference(AiReferenceItem item)
    {
        var index = aiReferences.IndexOf(item);
        if (index < 0) return;
        aiReferences.RemoveAt(index);
        item.Dispose();
        foreach (var (task, text) in settings.AiPromptDrafts.ToArray())
            settings.RememberAiPrompt(task, AiPromptField.AfterRemoval(text, AiImageMention.FirstReference(task) + index));
        var current = aiFloatingPrompt.Text ?? "";
        var updated = AiPromptField.AfterRemoval(current, AiImageMention.FirstReference(EditorTask) + index);
        if (updated != current) aiFloatingPrompt.Text = updated;
        aiDialogReferences?.NotifyRemoved(index);
        RefreshAiReferenceUi();
    }

    private void BuildAiReferenceUi(StackPanel host, AiTaskKind task)
    {
        host.Children.Clear();
        Avalonia.Automation.AutomationProperties.SetName(host, $"Reference images ({aiReferences.Count}/{AiReferenceEditor.Capacity})");
        var items = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            ItemSpacing = AiGenerationControls.EditorSpacing, LineSpacing = AiGenerationControls.EditorSpacing,
            IsVisible = aiReferences.Count > 0 };
        var mentions = AiImageMentions(task);
        for (var index = 0; index < aiReferences.Count; index++)
        {
            var item = aiReferences[index];
            var mention = mentions[index];
            const double side = ReferenceTileSize, radius = 6;
            var preview = new Image { Source = item.Preview, Width = side, Height = side, Stretch = Stretch.UniformToFill };
            var remove = new Button
            {
                Content = Icons.Create(Icons.Close, 13, Brushes.White), MinWidth = 0, MinHeight = 0,
                Width = 18, Height = 18, Padding = new Thickness(0), Margin = new Thickness(0, 3, 3, 0),
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Color.Parse("#D9222326")), Foreground = Brushes.White
            };
            Avalonia.Automation.AutomationProperties.SetName(remove, "Remove reference: " + item.Name);
            remove.Click += (_, e) => { e.Handled = true; RemoveAiReference(item); };
            remove.ZIndex = 1;
            Locale.Tip(remove, "Remove reference");
            var caption = Ui.Label(mention.Label, Palette.Window, 11, weight: FontWeight.SemiBold);
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            var badge = new Border { Background = mention.Color, Height = 17, VerticalAlignment = VerticalAlignment.Bottom, Child = caption };
            var thumbnail = new Grid { Children = { preview, badge } };
            var show = new Button
            {
                Classes = { "flat" }, Content = thumbnail, Padding = new Thickness(0), MinWidth = 0, MinHeight = 0,
                Width = side, Height = side, BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch
            };
            Avalonia.Automation.AutomationProperties.SetName(show, $"{mention.Label} · {item.Name}");
            show.Click += (_, _) => _ = ShowAiReferencePreview(item, TopLevel.GetTopLevel(host) as Window);
            Locale.Tip(show, $"{mention.Label} · {item.Name}");
            var cell = new Border { Width = side, Height = side, CornerRadius = new CornerRadius(radius), ClipToBounds = true,
                Child = new Grid { Children = { show, remove } } };
            items.Children.Add(cell);
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
            if (aiReferences.Count >= AiReferenceEditor.Capacity) break;
            try { AddAiReference(ImageFiles.Load(path), Path.GetFileName(path)); }
            catch (Exception error) { ShowProblem($"Couldn't add {Path.GetFileName(path)}: {error.Message}"); }
        }
        RefreshAiReferenceUi();
    }

    private void AddAiReference(SKBitmap pixels, string name)
    {
        if (aiReferences.Count >= AiReferenceEditor.Capacity) { pixels.Dispose(); return; }
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
        if (aiReferences.Count >= AiReferenceEditor.Capacity) return false;
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
        host.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            var supported = e.DataTransfer.TryGetValue(LayersPanel.ReferenceDragFormat) != null || e.DataTransfer.TryGetFiles()?.Any() == true;
            if (!supported) return;
            e.DragEffects = aiReferences.Count < AiReferenceEditor.Capacity ? e.DragEffects & DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        });
    }

    private void OnAiReferenceDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetValue(LayersPanel.ReferenceDragFormat) is { } id)
        {
            e.Handled = true;
            if (layers.GetDraggedReference(id, session) is { } layer && session is { } source && !IsDragging &&
                !source.IsStroking && aiReferences.Count < AiReferenceEditor.Capacity)
                _ = AddAiLayerReference(source.Document, layer);
            return;
        }
        if (e.DataTransfer.TryGetFiles() is not { } files || !files.Any()) return;
        AddAiReferencePaths(files.Select(file => file.TryGetLocalPath()).OfType<string>());
        e.Handled = true;
    }

    private async Task AddAiLayerReference(Document document, Layer layer)
    {
        try
        {
            var snapshot = layer.Clone();
            snapshot.Visible = true;
            snapshot.Clipped = false;
            var bounds = SKRectI.Empty;
            foreach (var item in Document.Flatten([snapshot]))
                bounds = Composa.Model.Geometry.Union(bounds, Composa.Model.Geometry.RoundOut(item.VisibleBounds));
            if (bounds.IsEmpty) throw new InvalidOperationException("The layer has no image content.");
            var bytesPerPixel = document.WorkingDepth == ColorDepth.Sixteen ? 16 : 4;
            if (!DocumentLimits.FitsSurface(bounds.Width, bounds.Height, bytesPerPixel))
                bounds = SKRectI.Intersect(bounds, document.Bounds);
            if (!DocumentLimits.FitsSurface(bounds.Width, bounds.Height, bytesPerPixel))
                throw new InvalidOperationException("The layer is too large to use as a reference.");
            var renderDocument = new Document(document.Width, document.Height) { Depth = document.Depth, Profile = document.Profile };
            var name = snapshot.Name;
            var owner = aiDialogReferences;
            var pixels = await Task.Run(() => DocumentRenderer.RenderLayers(renderDocument, [snapshot], bounds));
            if (aiDialogReferences != owner || session?.Document != document) { pixels.Dispose(); return; }
            AddAiReference(pixels, name);
            RefreshAiReferenceUi();
        }
        catch (Exception ex) { ShowProblem(ex.Message); }
    }

    internal async Task ShowAiSettings(int page = 6, string? field = null, AiTaskKind? operation = null,
        bool firstRun = false, Action? firstRunBack = null)
    {
        try
        {
            if (!await AiDialogs.SettingsDialog(this, settings, aiTasks, page, field, operation, firstRun, firstRunBack)) return;
            dock.SetTabbed(settings.TabbedPanels);
            aiTasks.ConnectionTimeoutSeconds = settings.ComfyConnectionTimeoutSeconds;
            aiTasks.SelectedEngine = aiTasks.Engines.Find(settings.AiEngineId);
            modelDrafts.Clear();
            RefreshEditorModels();
            RefreshAiUi();
            RefreshObjectModelChoices();
        }
        catch (Exception error) { ShowProblem(error.Message); }
    }

    private async Task ShowAiAdvanced()
    {
        var previous = aiTasks.SelectedEngine;
        var task = DockEditorVisible || session?.Selection != null ? EditorTask : AiTaskKind.ImageEdit;
        if (DirectImageFor(task) is { } direct)
        {
            await AiDialogs.Advanced(this, settings, task: task, initialProfile: AiOptions(task) with { AlignPixels = settings.AiAlignMaskedEdits },
                acceptDraft: profile =>
                {
                    settings.SetOperation(direct.Id, task, profile);
                    if (profile.AlignPixels is { } align) settings.AiAlignMaskedEdits = align;
                    settings.Save();
                });
            RefreshAiUi();
            return;
        }
        aiTasks.SelectedEngine = PanelEngine(task);
        try { await AiDialogs.Advanced(this, settings, aiTasks, task, useAssigned: PanelUsesAssignedWorkflow(task)); }
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
            if (!await AiDialogs.Upscale(this, settings, area.Width, area.Height, upscaleSession.Selection != null,
                PanelEngine(task), direct == null ? aiTasks : null, PanelChoice(task)?.Name ?? direct?.Label)) return;
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
            aiDialogReferenceTask = task;
            aiDialogReferences = new AiReferenceEditor(host, () => aiReferences.Count, PasteAiReferenceOrText, PickAiReferences, () => AiImageMentions(task));
            BuildAiReferenceUi(host, task);
            try { options = await AiDialogs.Prompt(this, task, settings, aspect.Width, aspect.Height, initialPrompt, aiTasks, aiReferences.Count,
                session?.Selection != null && !(task == AiTaskKind.GenerativeExpand && session.Tool == Tool.Crop), aiDialogReferences,
                session?.Document.Bounds, session?.Selection is { } selected ? AutomaticAiMask.ContourBounds(selected) : null,
                modelDrafts.GetValueOrDefault(task)?.Engine.Id, direct?.PromptProfile()); }
            finally { aiDialogReferences = null; }
            if (presentedTask == task && settings.RememberAiPrompts && settings.AiPromptFor(task) != (aiFloatingPrompt.Text ?? ""))
                aiFloatingPrompt.Text = settings.AiPromptFor(task);
        }
        RefreshAiUi();
        if (options == null) return;
        if (target != null && !sessions.Contains(target)) return;
        if (target == null)
        {
            if (task != AiTaskKind.GenerateImage) return;
            AddSession(EditorSession.NewCanvas(options.Width, options.Height));
            target = session;
        }
        var seed = options.Seed < 0 ? Random.Shared.NextInt64(long.MaxValue) : options.Seed;
        var engine = direct?.PromptProfile() ?? (options.EngineId == null ? PanelEngine(task) : aiTasks.EngineFor(task, options.EngineId));
        var useAssigned = PanelUsesAssignedWorkflow(task);
        if (direct == null && options.EngineId != null && engine != null && engine.Id != PanelEngine(task)?.Id)
        {
            modelDrafts[task] = aiTasks.Engines.ModelFor(engine, task, settings.ComfyModelsFor(settings.ComfyServerUrl));
            useAssigned = false;
            RefreshEditorModels();
        }
        profile = AiOptions(task, engine);
        try
        {
            var values = new Dictionary<string, object?>
            {
                ["fluxMemory"] = profile.FluxMemory, ["apiQuality"] = profile.ApiQuality,
                ["apiSize"] = "Custom", ["imageOriginalSize"] = profile.OriginalSize
            };
            if (aiTasks.ActiveWorkflow(task, engine, useAssigned) != null)
            {
                if (profile.WorkflowSteps is { } steps) values["steps"] = steps;
                if (profile.WorkflowSampler is { } sampler) values["sampler"] = sampler;
            }
            var targetSession = target!;
            var cropAtStart = ReferenceEquals(session, targetSession) && targetSession.Tool == Tool.Crop ? canvas.CropRect : null;
            var request = new AiTaskRequest
            {
                Task = task,
                Scene=profile.Scene.Normalized(),
                AlignMaskedEdits = settings.AiAlignMaskedEdits,
                EngineId = engine?.Id,
                UseAssignedWorkflow = useAssigned,
                ModelFileOverrides = modelDrafts.GetValueOrDefault(ImageOperationModels.ModelTask(task)) is { FileSlot: { } slot } model
                    ? new Dictionary<string, string> { [slot.Key] = model.Identifier } : null,
                SelectionRegion = selectionRegion, SelectionOperation = selectionOperation, SelectionPoint = selectionPoint,
                Prompt = task == AiTaskKind.GenerativeExpand ? AiPromptDefaults.Expand
                    : AiPromptField.ForModel(options.Prompt, AiImageMention.FirstReference(task), aiReferences.Count),
                ExpansionMode = profile.ExpansionMode,
                ExpansionMinimumSide = profile.ExpansionMode == AiExpansionMode.WholeImage ? profile.WholeExpansionMinimumSide : profile.ExpansionMinimumSide,
                ReferenceImages = aiReferences.Select(item => item.Pixels).ToList(),
                ReferenceMegapixels = profile.ReferenceMegapixels,
                ExpansionBounds = task == AiTaskKind.GenerativeExpand && cropAtStart is { } crop
                    ? new SKRectI((int)Math.Floor(crop.Left), (int)Math.Floor(crop.Top), (int)Math.Ceiling(crop.Right), (int)Math.Ceiling(crop.Bottom)) : null,
                Settings = new AiGenerationSettings
                {
                    Width = options.Width, Height = options.Height, Seed = seed,
                    Variants = Math.Clamp(options.Variants, 1, 3), VariantMode = profile.VariantMode,
                    Loras = profile.ActiveLoras(engine),
                    UpscaleFactor = profile.UpscaleFactor == 4 ? 4 : 2,
                    Values = values
                }
            };
            bool TargetAvailable() => sessions.Contains(targetSession);
            bool ExpansionFrameUnchanged() => !ReferenceEquals(session, targetSession) ||
                (targetSession.Tool == Tool.Crop && !IsDragging && canvas.CropRect == cropAtStart);
            var editor = new EditorCommandService(targetSession);
            var canvasExpanded = direct != null
                ? await aiTasks.RunDirectImageAsync(editor, direct, request, TargetAvailable, expansionFrameUnchanged: ExpansionFrameUnchanged)
                : await aiTasks.RunAsync(editor, request, TargetAvailable, expansionFrameUnchanged: ExpansionFrameUnchanged);
            if (task == AiTaskKind.GenerateImage && direct == null && engine?.Binding(AiTaskKind.ImageEdit) != null &&
                modelDrafts.ContainsKey(task) && !modelDrafts.ContainsKey(AiTaskKind.ImageEdit))
            {
                modelDrafts[AiTaskKind.ImageEdit] = aiTasks.Engines.ModelFor(engine, AiTaskKind.ImageEdit, settings.ComfyModelsFor(settings.ComfyServerUrl));
                RefreshEditorModels();
            }
            if (canvasExpanded && ReferenceEquals(session, targetSession) && targetSession.Tool == Tool.Crop)
                canvas.ResetCropToCanvas();
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
            var context = AutomaticAiMask.Geometry(bounds, session.Document.Bounds, settings.AiSelectionContextPercent).Bounds;
            return (context.Width, context.Height);
        }
        return session == null ? AiDimensions.FromMegapixels(settings.AiMegapixels, 1, 1) : (session.Document.Width, session.Document.Height);
    }

    private static bool IsEmpty(SKBitmap bitmap) => bitmap.GetPixelSpan().IndexOfAnyExcept((byte)0) < 0;
}
