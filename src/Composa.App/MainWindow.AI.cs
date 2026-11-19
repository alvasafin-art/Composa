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
    private SKBitmap? floatingBoundsMask;
    private SKRectI floatingBounds;
    private Point? aiFloatingPinnedPosition;
    private Point? aiFloatingDragStart;
    private Point aiFloatingDragPosition;
    private readonly Dictionary<AiTaskKind, string> modelDrafts = [];
    private Button aiEnhance = null!, aiAdvanced = null!;
    private EngineProfile? PanelEngine(AiTaskKind task) => aiTasks.EngineFor(task, modelDrafts.GetValueOrDefault(task));
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
        IsVisible = false, Width = 520, Padding = new Thickness(7), CornerRadius = new CornerRadius(5),
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
    private AiVariantButtons? aiVariantButtons;
    private ComboBox? aiEngineCombo;
    private StackPanel? aiFloatingActions;
    private readonly TextBlock aiApiCost = new() { Foreground = Palette.Secondary, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 225 };
    private readonly TextBlock aiFloatingStatus = new() { Foreground = Palette.Secondary, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 230 };

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
        var engine = PanelEngine(task);
        return AiTaskAvailability.Resolve(session, engine, task, CropExpands);
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
            ToolTip.SetTip(aiTaskMenuButton, "AI tasks and settings · ComfyUI " + aiTasks.ConnectionState.ToString().ToLowerInvariant());
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
                ToolTip.SetTip(button, $"Variant {i + 1}");
                Avalonia.Automation.AutomationProperties.SetName(button, $"Variant {i + 1}");
                button.Padding = new Thickness(2);
                button.MinWidth = 28; button.IsEnabled = aiTasks.Operation?.Status is not (AiOperationStatus.Queued or AiOperationStatus.Running);
                aiVariantStrip.Children.Add(button);
            }
            aiVariantStrip.Children.Add(CompareVariants(session, variants.Id));
        }

        var operation = aiTasks.Operation;
        var active = operation?.Status is AiOperationStatus.Queued or AiOperationStatus.Running;
        if (shownProgress != operation)
        {
            shownProgress = operation;
            aiProgressHost.Children.Clear();
            if (operation != null) AddProgress(operation, active);
        }
        aiTaskText.IsVisible = active;
        aiTaskText.Text = active ? "AI: " + (operation?.Stage ?? "Working") : "";
        aiContextHost.IsVisible = session != null;
        RefreshAiFloatingUi();
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
        aiHeaderReference = AiGenerationControls.IconAction(Icons.Image, "AiAddReference", "Add reference image · paste with Ctrl+V or drag files here", () => _ = PickAiReferences());
        aiEnhance = AiDialogs.EnhanceButton(aiFloatingPrompt, settings, aiTasks, () => EditorTask, () => PanelEngine(EditorTask));
        aiAdvanced = AiGenerationControls.IconAction(Icons.Gear, "AiAdvanced", "Generation settings", () => _ = ShowAiAdvanced());
        aiAdvanced.MinWidth = 0;
        var close = aiFloatingCollapse = Ui.TextButton("▾", () => { settings.AiFloatingCollapsed = !settings.AiFloatingCollapsed; settings.Save(); RefreshAiUi(); });
        close.Name = "AiFloatingCollapse";
        close.Classes.Add("flat");
        close.MinWidth = 25;
        close.Height = 28;
        close.Padding = new Thickness(3, 0);
        close.VerticalContentAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(close, "Collapse panel until you expand it");

        aiFloatingGenerate = Ui.TextButton("Run", () => _ = RunAi(EditorTask, aiFloatingPrompt.Text ?? "", useInlinePrompt: true), accent: true);
        aiFloatingMore = AiGenerationControls.IconAction(Icons.MoreActions, "AiMore", "More AI operations", OpenAiFloatingTaskMenu);
        aiVariantButtons = new AiVariantButtons(AiOptions(AiTaskKind.GenerativeFill).Variants, value =>
        {
            var task = EditorTask;
            var engine = PanelEngine(task);
            settings.SetOperation(engine?.Id, task, AiOptions(task, engine) with { Variants = value });
            settings.Save(); RefreshAiFloatingUi();
        });
        var run = AiGenerationControls.RunGroup(aiFloatingGenerate, aiVariantButtons);
        aiEngineCombo = new ComboBox { Width = 141 };
        RefreshEditorModels();
        aiEngineCombo.SelectionChanged += (_, _) =>
        {
            if (refreshingAiEngine || aiEngineCombo.SelectedItem is not ImageModelChoice model) return;
            modelDrafts[EditorTask] = model.Engine.Id;
            RefreshAiUi();
        };
        ToolTip.SetTip(aiEngineCombo, "Image model for this operation");
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
        aiFloatingTools.Children.AddRange([aiEngineCombo, aiHeaderReference, aiEnhance, aiFloatingAlignment, aiAdvanced]);
        var leading = new DockPanel();
        DockPanel.SetDock(grip, Dock.Left);
        leading.Children.Add(grip);
        leading.Children.Add(aiFloatingTools);
        aiFloatingActions = run;
        var bar = new AiPromptToolbar(leading, Ui.Row(3, aiFloatingMore, close, run));
        aiFloatingPrompt.HorizontalAlignment = HorizontalAlignment.Stretch;
        return Ui.Column(4, aiFloatingPrompt, bar, aiReferenceHost, aiApiCost, aiFloatingStatus, aiFloatingVariants);
    }

    private void RefreshEditorModels()
    {
        if (aiEngineCombo == null) return;
        var task = EditorTask;
        var engine = PanelEngine(task);
        var choices = aiTasks.Engines.ImageModels(task, engine, settings.ComfyModelsFor(settings.ComfyServerUrl), settings.AiModelRoutes);
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
            if (task == AiTaskKind.ImageEdit && session?.Selection != null) continue;
            var availability = AiAvailability(task);
            var item = new MenuItem
            {
                Header = task.DisplayName(),
                IsEnabled = availability.Available && aiTasks.Operation?.Status is not (AiOperationStatus.Queued or AiOperationStatus.Running)
            };
            if (availability.Reason != null) ToolTip.SetTip(item, availability.Reason);
            item.Click += (_, _) => _ = RunAi(task, task == AiTaskKind.RemoveObject ? "" : aiFloatingPrompt.Text ?? "", useInlinePrompt: true);
            flyout.Items.Add(item);
        }
        var fill = AiOptions(AiTaskKind.GenerativeFill);
        var sizes = new MenuItem { Header = "Image size" };
        foreach (var size in new[] { 0.0 }.Concat(AiDimensions.MegapixelOptions))
        {
            var option = new MenuItem { Header = size == 0 ? "Original size" : AiDimensions.Label(size),
                ToggleType = MenuItemToggleType.Radio, IsChecked = size == 0 ? fill.OriginalSize : !fill.OriginalSize && fill.Megapixels == size };
            option.Click += (_, _) =>
            {
                var engine = PanelEngine(AiTaskKind.GenerativeFill);
                var current = AiOptions(AiTaskKind.GenerativeFill, engine);
                settings.SetOperation(engine?.Id, AiTaskKind.GenerativeFill, current with
                    { OriginalSize = size == 0, Megapixels = size == 0 ? current.Megapixels : size });
                settings.Save();
            };
            sizes.Items.Add(option);
        }
        flyout.Items.Add(new Separator());
        flyout.Items.Add(sizes);
        var preview = new MenuItem { Header = "Preview generation area" };
        preview.Click += (_, _) =>
        {
            if (session?.Selection is not { } mask) return;
            var selection = AutomaticAiMask.ContourBounds(mask);
            if (!selection.IsEmpty) _ = PreviewAiContext(AutomaticAiMask.Geometry(selection, session.Document.Bounds).Bounds);
        };
        flyout.Items.Add(preview);
        if (modelDrafts.ContainsKey(AiTaskKind.GenerativeFill))
        {
            var save = new MenuItem { Header = "Use this model by default" };
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
        && session.Tool != Tool.RemoveObject && !(session.Tool == Tool.ObjectSelectionAi && canvas.IsDragging)
        && aiTasks.ConnectionState == ComfyConnectionState.Connected;

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
        if (aiFloatingCollapse != null)
        {
            if (settings.AiFloatingCollapsed) aiFloatingCollapse.Content = "Edit selection  ▸";
            else if (aiFloatingCollapse.Content is not Control) aiFloatingCollapse.Content = Icons.Create(Icons.ChevronDown, 14);
            Avalonia.Automation.AutomationProperties.SetName(aiFloatingCollapse, settings.AiFloatingCollapsed ? "Expand panel" : "Collapse panel");
            ToolTip.SetTip(aiFloatingCollapse, settings.AiFloatingCollapsed ? "Expand panel" : "Collapse panel until you expand it");
        }
        if (!visible && !DockEditorVisible) return;
        var busy = aiTasks.Operation?.Status is AiOperationStatus.Queued or AiOperationStatus.Running;
        var task = EditorTask;
        var fillEngine = PanelEngine(task);
        var fillProfile = AiOptions(task, fillEngine);
        if (aiFloatingAlignment != null) aiFloatingAlignment.IsEnabled = task != AiTaskKind.GenerateImage;
        if (aiHeaderReference != null) aiHeaderReference.IsEnabled = aiReferences.Count < 6;
        if (aiVariantButtons != null)
        {
            aiVariantButtons.Count = fillProfile.Variants;
            aiVariantButtons.IsEnabled = !busy;
        }
        if (aiEngineCombo != null)
        {
            var index = aiEngineCombo.Items.OfType<ImageModelChoice>().ToList().FindIndex(model => model.Engine.Id == fillEngine?.Id);
            refreshingAiEngine = true;
            try { if (index >= 0 && aiEngineCombo.SelectedIndex != index) aiEngineCombo.SelectedIndex = index; }
            finally { refreshingAiEngine = false; }
            aiEngineCombo.IsEnabled = !busy;
        }
        if (aiFloatingGenerate != null) aiFloatingGenerate.IsEnabled = !busy && AiAvailability(task).Available && (!DockEditorVisible || task != AiTaskKind.GenerateImage || !string.IsNullOrWhiteSpace(aiFloatingPrompt.Text));
        if (aiFloatingMore != null) aiFloatingMore.IsEnabled = !busy;
        var operation = aiTasks.Operation;
        aiFloatingStatus.Text = aiLastError ?? (DockEditorVisible && aiTasks.ConnectionState != ComfyConnectionState.Connected ? "ComfyUI · " + aiTasks.ConnectionState.ToString().ToLowerInvariant() : null) ?? (operation?.Status switch
        {
            AiOperationStatus.Queued or AiOperationStatus.Running => operation.Stage ?? "Working…",
            AiOperationStatus.Completed => "Completed",
            AiOperationStatus.Cancelled => "Cancelled",
            AiOperationStatus.Failed => operation.Error ?? "AI operation failed",
            _ => ""
        });
        aiFloatingStatus.IsVisible = expanded && !string.IsNullOrEmpty(aiFloatingStatus.Text);
        aiFloatingStatus.Foreground = aiLastError != null || operation?.Status == AiOperationStatus.Failed ? Brushes.Orange : Palette.Secondary;
        aiApiCost.IsVisible = expanded && fillEngine?.PaidApi == true;
        if (aiApiCost.IsVisible)
        {
            var count = (task == AiTaskKind.GenerateImage ? 0 : 1) + aiReferences.Count; // cropped source and genuine user references only
            var estimate = PartnerPricing.Estimate(aiTasks.ServerCapabilities, fillEngine!.ApiModel!, fillProfile.ApiQuality, "Custom", count, Math.Clamp(fillProfile.Variants, 1, 3));
            aiApiCost.Text = operation?.CreditsUsed is { } credits ? PartnerPricing.Reported(credits) : estimate?.Label ?? "Paid API · estimate unavailable";
            ToolTip.SetTip(aiApiCost, "ComfyUI bills credits, not a balance of OpenAI tokens. Estimate comes from this server's price badge and accounts for the input images and number of variants. Actual charges may differ. Local ComfyUI does not expose the account balance. The edit mask stays in Composa and is not a billed input.");
        }
        if (DockEditorVisible)
        {
            // Four reference cells fit in the standard 296 px dock. Keep the user's base height for the editor itself.
            var rows = (aiReferences.Count + 3) / 4;
            dock.SetExtraHeight(generationSection, rows * 65 + (aiFloatingVariants.IsVisible ? 34 : 0)
                + (aiApiCost.IsVisible ? 18 : 0) + (aiFloatingStatus.IsVisible ? 18 : 0));
        }
        ToolTip.SetTip(aiFloatingStatus, aiFloatingStatus.Text);
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
        Avalonia.Automation.AutomationProperties.SetName(host, $"Reference images ({aiReferences.Count}/6)");
        var items = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
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
            ToolTip.SetTip(remove, "Remove reference");
            var cell = new Grid { Width = side, Height = side, Margin = new Thickness(0, 0, 7, 5) };
            var show = new Button { Content = preview, Padding = new Thickness(0), CornerRadius = new CornerRadius(radius),
                BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            show.Click += (_, _) => _ = ShowAiReferencePreview(item, TopLevel.GetTopLevel(host) as Window);
            ToolTip.SetTip(show, "Preview reference: " + item.Name);
            cell.Children.Add(show);
            cell.Children.Add(remove);
            ToolTip.SetTip(cell, item.Name);
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
            add.Margin = new Thickness(0, 0, 7, 5);
            add.FontSize = 22;
            ToolTip.SetTip(add, aiReferences.Count == 0 ? "Add reference image · paste with Ctrl+V or drag files here" : "Add another reference image");
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
            if (!await AiDialogs.SettingsDialog(this, settings, aiTasks, page, field)) return;
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
        aiTasks.SelectedEngine = PanelEngine(task);
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
            if (!await AiDialogs.Upscale(this, settings, area.Width, area.Height, upscaleSession.Selection != null, PanelEngine(task), aiTasks)) return;
        }
        var aspect = AiAspect(task);
        var profile = AiOptions(task);
        var fallback = profile.OriginalSize
            ? task is AiTaskKind.Upscale or AiTaskKind.ObjectSelection or AiTaskKind.SelectSubject ? aspect : AiDimensions.OriginalGenerationSize(aspect.Width, aspect.Height)
            : AiDimensions.FromMegapixels(profile.Megapixels, aspect.Width, aspect.Height);
        AiPromptResult? options;
        if ((task != AiTaskKind.GenerativeExpand || DockEditorVisible && useInlinePrompt) && (useInlinePrompt || task == AiTaskKind.RemoveObject && session?.Selection != null || task is AiTaskKind.SelectSubject or AiTaskKind.ObjectSelection or AiTaskKind.Upscale))
            options = new(initialPrompt, fallback.Width, fallback.Height, profile.Seed);
        else
        {
            var host = new StackPanel { Name = "AiDialogReferences", Spacing = 5, MaxWidth = 430 };
            aiDialogReferences = new AiReferenceEditor(host, () => aiReferences.Count, PasteAiReferenceOrText, PickAiReferences);
            BuildAiReferenceUi(host);
            try { options = await AiDialogs.Prompt(this, task, settings, aspect.Width, aspect.Height, initialPrompt, aiTasks, aiReferences.Count,
                session?.Selection != null && !(task == AiTaskKind.GenerativeExpand && session.Tool == Tool.Crop), aiDialogReferences,
                session?.Document.Bounds, session?.Selection is { } selected ? AutomaticAiMask.ContourBounds(selected) : null); }
            finally { aiDialogReferences = null; }
        }
        RefreshAiUi();
        if (options == null) return;
        if (task == AiTaskKind.GenerateImage) { settings.GenerateImagePrompt = options.Prompt; settings.Save(); }
        if (task == AiTaskKind.ImageEdit) { settings.ImageEditPrompt = options.Prompt; settings.Save(); }
        if (session == null)
        {
            if (task != AiTaskKind.GenerateImage) return;
            AddSession(EditorSession.NewCanvas(options.Width, options.Height));
        }
        var seed = options.Seed < 0 ? Random.Shared.NextInt64(long.MaxValue) : options.Seed;
        var engine = options.EngineId == null ? PanelEngine(task) : aiTasks.EngineFor(task, options.EngineId);
        profile = AiOptions(task, engine);
        try
        {
            var request = new AiTaskRequest
            {
                Task = task,
                Scene=profile.Scene.Normalized(),
                AlignMaskedEdits = settings.AiAlignMaskedEdits,
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
                    Loras = profile.ActiveLoras(engine),
                    UpscaleFactor = profile.UpscaleFactor == 4 ? 4 : 2,
                    Values = new Dictionary<string, object?> { ["fluxMemory"] = profile.FluxMemory,
                        ["apiQuality"] = profile.ApiQuality, ["apiSize"] = "Custom", ["imageOriginalSize"] = profile.OriginalSize }
                }
            };
            await aiTasks.RunAsync(new EditorCommandService(session!), request);
            if (modelDrafts.GetValueOrDefault(task) == engine?.Id && aiTasks.Operation?.Status == AiOperationStatus.Completed) modelDrafts.Remove(task);
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
