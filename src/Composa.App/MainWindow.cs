using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Composa.App.AI;
using Composa.App.Assistant;
using Composa.App.Automation;
using Composa.App.Controls;
using Composa.AI;
using Composa.Editing;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using Composa.Vision;
using SkiaSharp;

namespace Composa.App;

public sealed partial class MainWindow : Window
{
    private readonly List<EditorSession> sessions = [];
    private EditorSession? session;
    private readonly CanvasView canvas = new();
    private readonly LayersPanel layers = new() { Width = 296 };
    private readonly HistoryPanel history = new();
    private readonly ChannelsPanel channels = new();
    private readonly NavigatorPanel navigator;
    /// <summary>The right-hand column: the Layers panel with the panels of the Window menu under it.</summary>
    private readonly SideDock dock;
    private readonly List<Control> canvasChrome = [];
    private readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly TextBlock documentInfo = new() { Name = "DocumentInfo", FontSize = 11, Foreground = Palette.Secondary,
        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border optionsHost = new() { Height = 40, Background = Palette.Panel, Padding = new Thickness(12, 0) };
    private readonly Border toolOptionsHost = new();
    private readonly Grid combinedOptions = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
    private readonly Dictionary<Tool, ToolButton> toolButtons = [];
    private readonly TextBlock zoomText = new() { FontSize = 11.5, Foreground = Palette.Secondary };
    private readonly Border commandNotice = new() { Background = Palette.PanelRaised, CornerRadius = new CornerRadius(5),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
        Padding = new Thickness(12,6), Margin = new Thickness(10,8), IsVisible = false };
    private readonly TextBlock hintText = new() { Foreground = Palette.Secondary, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock aiText = new() { Foreground = Palette.Accent };
    private readonly TextBlock aiTaskText = new() { Foreground = Palette.Accent };
    private readonly Border aiContextHost = new() { Background = Brushes.Transparent, Padding = new Thickness(8, 0, 0, 0), IsVisible = false };
    private readonly AiTaskService aiTasks;
    private readonly LlamaServerHost assistantServer;
    private readonly JavaScriptRuntime scriptRuntime = new();
    private Mcp.McpHost? aiControl;
    private Mcp.McpHost? imageExchange;
    private readonly Border foregroundSwatch = new() { Width = 26, Height = 26, BorderBrush = Palette.SwatchOutline, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(3) };
    private readonly Border backgroundSwatch = new() { Width = 26, Height = 26, BorderBrush = Palette.SwatchOutline, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(3) };
    private readonly Panel welcome;
    private readonly UpdateNotice updateNotice = new();
    private Action? refreshOptions;
    private bool channelInspectorDragging;
    private IPointer? channelInspectorPointer;
    private readonly Settings settings = Settings.Load();
    private readonly Recovery? recovery = Settings.Persist ? new Recovery() : null;
    // The status bar shows a problem in orange, or a note in the hint's color, in place of the tool hint until the next command.
    private string? problem, note;

    public MainWindow(string? automationDirectory = null)
        : this(automationDirectory, url => new ComfyClient(url)) { }

    internal MainWindow(string? automationDirectory, Func<string, IComfyConnection> comfyClientFactory, string? libraryFolder = null,
        string? installationScripts = null)
    {
        (Application.Current as App)?.ApplyTheme(settings.Theme);
        selectionTool = settings.ToolPalette?.SelectionTool is Tool.SelectionBrush ? Tool.SelectionBrush : Tool.Wand;
        stampUsesPatch = settings.ToolPalette?.Patch ?? false;
        stampGroupTool = SavedStampTool(settings.ToolPalette);
        gradientShowsBucket = settings.ToolPalette?.Bucket ?? false;
        automation = new AutomationCatalog(automationDirectory ?? (Settings.Persist ? Path.Combine(AppPaths.Config, "automation") : null),
            installationScripts ?? (Settings.Persist ? Path.Combine(AppContext.BaseDirectory, "scripts") : null));
        aiTasks = new AiTaskService(() => settings.ComfyServerUrl, Path.Combine(AppContext.BaseDirectory, "ai", "engines"), comfyClientFactory)
        {
            ConnectionTimeoutSeconds = settings.ComfyConnectionTimeoutSeconds,
            ModelSelections = settings.ComfyModelsFor,
            WorkflowSettings = () => settings.Workflows,
            EngineIdForTask = settings.EngineForTask,
            PreferredModelRoutes = () => settings.AiModelRoutes,
            ModelKeyForTask = task => settings.AiTaskModelKeys.GetValueOrDefault((task == AiTaskKind.SelectSubject ? AiTaskKind.ObjectSelection : task).ToString()),
            AdditionalPromptForPack = id => settings.PromptFor(id) is { Enabled: true } value ? value.Text : "",
            ApiKey = () => (Settings.Persist ? UserApiKeys.Read(UserApiKeys.Target("Comfy", ComfyServerAddress.Parse(settings.ComfyServerUrl).HttpBase)) : null)
                ?? (string.IsNullOrWhiteSpace(settings.ComfyApiKeyEnvironment) ? null : Environment.GetEnvironmentVariable(settings.ComfyApiKeyEnvironment)),
            RequestApiKey = token => Dialogs.AiDialogs.ApiKey(this, token, settings.ComfyServerUrl)
        };
        aiTasks.SelectedEngine = aiTasks.Engines.Find(settings.AiEngineId);
        if (aiTasks.SelectedEngine is { } engine)
        {
            try { settings.MigrateComfyUpscaler(settings.ComfyServerUrl, aiTasks.Engines.ModelSlots(engine)); }
            catch (Exception error) when (error is FormatException or IOException or System.Text.Json.JsonException) { /* Settings can still repair a broken URL/pack. */ }
        }
        aiTasks.StateChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshAiState);
        assistantServer = new LlamaServerHost(settings);
        Title = "Composa";
        Width = Math.Clamp(settings.WindowWidth, 800, 10000);
        Height = Math.Clamp(settings.WindowHeight, 520, 10000);
        if (settings.Maximized) WindowState = WindowState.Maximized;
        canvas.ShowPixelGrid = settings.ShowPixelGrid;
        canvas.ShowTransformControls = settings.ShowTransformControls;
        canvas.AutoSelect = settings.AutoSelect;
        jpegQuality = Math.Clamp(settings.JpegQuality, 1, 100);
        MinWidth = 800;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://composa/Assets/icon.png")));

        welcome = BuildWelcome();
        aiFloatingHost.Child = BuildAiFloatingMenu();
        layers.CanDragReference = layer => !settings.CanvasOnly && (DockEditorVisible || aiFloatingHost.IsVisible)
            && aiReferences.Count < MaxAiReferences && Document.Flatten([layer]).Any(item => item.Pixels != null);
        EnableAiReferenceDrop(aiFloatingHost);
        aiFloatingHost.SizeChanged += (_, _) => RefreshAiFloatingPosition();
        aiFloatingLayer.Children.Add(aiFloatingHost);
        commandNotice.Child = hintText;
        var canvasHost = new Panel { Children = { canvas, welcome, aiFloatingLayer, commandNotice } };
        elementLibrary = new ElementLibrary(libraryFolder ?? AppPaths.Library);
        libraryPanel = BuildResourceLibrary();
        workspaceGrid.ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto,Auto,Auto");
        var center = workspaceGrid;
        toolSurface.Child = BuildToolRail();
        toolChrome = Chrome(toolSurface);
        center.Children.Add(toolChrome);
        var leftDivider = Ui.Separator(); leftDivider.Margin = new Thickness(0); AddAt(center, Chrome(leftDivider), 2);
        AddAt(center, canvasHost, 3);
        var rightDivider = Ui.Separator(); rightDivider.Margin = new Thickness(0); AddAt(center, Chrome(rightDivider), 4);
        var historySection = new DockSection("History", history, new());
        var channelsSection = new DockSection("Channels", channels, new(Visible: false, Height: 250));
        // The column has the Layers panel's width, so a long step name is cut off rather than widening it.
        navigator = new NavigatorPanel(canvas);
        var navigatorSection = new DockSection("Navigator", navigator, new(Visible: false, Height: 220));
        generationSection = new DockSection("AI Generation", BuildGenerationDock(), new(Visible: false, Height: 164));
        var properties = new DockSection("Properties", propertiesView, new(Height: 232));
        var documentInfoPanel = new Border
        {
            Name = "DocumentInfoPanel", Height = 26, Background = Palette.Panel, BorderBrush = Palette.Divider,
            BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(10, 0), Child = documentInfo
        };
        dock = new SideDock((header, title) => AttachPanelDrag(header, title), new DockSection("Layers", layers, new(Height: 285)),
            properties, navigatorSection, channelsSection, generationSection, historySection,
            new DockSection("Resources", libraryPanel, DefaultResourcePlacement),
            new DockSection("Document Info", documentInfoPanel, new(Height: 26, Order: int.MaxValue, Standalone: true), compactHeight: 26));
        dock.RestoreLayout(settings.Dock, persist: false);
        dock.LayoutChanged += () => { settings.Dock = dock.CaptureLayout(); settings.Save(); };
        AddAt(center, Chrome(dock.LeftHost), 1);
        AttachPanelDrag(toolSurface, "Tools", topOnly: true);
        PlaceTools(settings.ToolsPosition);
        SizeChanged += (_, _) => dock.UpdateWidths();
        dock.PresentationChanged += RefreshAiUi;
        dock.SetTabbed(settings.TabbedPanels);
        AddAt(center, Chrome(dock), 5);
        history.GoToRequested += GoToHistory;

        // The update notice sits under the menu, where it is visible without covering anything.
        BuildAiContextBar();
        combinedOptions.Children.Add(toolOptionsHost);
        var assistantButton = Ui.TextButton("Assistant", ShowAssistant);
        assistantButton.Classes.Add("flat");
        assistantButton.MinWidth = 0;
        Locale.Tip(assistantButton, "Open the local editing Assistant");
        generativeExpandButton = Ui.TextButton("Generative expand", () => _ = RunAi(AiTaskKind.GenerativeExpand));
        generativeExpandButton.IsVisible = false;
        Locale.Tip(generativeExpandButton, "Generate image content inside the extended crop frame");
        aiTaskText.IsVisible = aiText.IsVisible = false;
        var intelligentTools = Ui.Row(8, generativeExpandButton, aiContextHost, aiTaskText, aiText, assistantButton);
        AddAt(combinedOptions, intelligentTools, 1);
        optionsHost.Child = combinedOptions;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*") };
        root.Children.Add(BuildMenu());
        AddRow(root, Chrome(updateNotice), 1);
        AddRow(root, Chrome(BuildTabBar()), 2);
        AddRow(root, Chrome(optionsHost), 3);
        AddRow(root, center, 4);
        Content = root;
        aiFloatingLayer.IsVisible = !settings.CanvasOnly;

        StartUpdateCheck();

        canvas.ViewChanged += () => { UpdateStatus(); RefreshAiFloatingPosition(); };
        canvas.CreateShapeRequested += at => _ = CreateShapeAt(at);
        canvas.Problem += message => { problem = message; UpdateStatus(); };
        canvas.TransformPreviewChanged += QueueTransformFields;
        canvas.ToolStateChanged += () => { if (session?.WarpEdit != null || optionsHaveWarp) RebuildOptions(); else refreshOptions?.Invoke(); UpdateColors(); RefreshAiUi(); UpdateStatus(); };
        canvas.AiToolsAvailable = () => settings.ImageRouteFor(AiTaskKind.RemoveObject) == ImageGenerationRoute.ComfyWorkflow
            ? aiTasks.ConnectionState == ComfyConnectionState.Connected && CanRunAi(AiTaskKind.RemoveObject)
            : CanRunAi(AiTaskKind.RemoveObject);
        canvas.ObjectSelectionAvailable = () => !localSelectionBusy && (settings.ImageRouteFor(AiTaskKind.ObjectSelection) switch
        {
            ImageGenerationRoute.BuiltIn => true,
            ImageGenerationRoute.ComfyWorkflow => aiTasks.ConnectionState == ComfyConnectionState.Connected && CanRunAi(AiTaskKind.ObjectSelection),
            _ => CanRunAi(AiTaskKind.ObjectSelection)
        });
        canvas.AiSelectionCompleted += task => _ = RunAi(task, useInlinePrompt: true);
        canvas.AiObjectSelectionRequested += (region, mode) => _ = RunObjectSelection(region, mode);
        canvas.ObjectSelectionPointRequested += (x, y, mode) => _ = RunObjectSelection(null, mode, new SkiaSharp.SKPointI(x, y));
        // Opening text from the canvas with another tool switches to the Type tool, so the toolbar has to follow.
        canvas.TextEditingChanged += () => { if (session != null) ShowTool(session.Tool); RebuildOptions(); UpdateStatus(); };
        layers.EditTextRequested += BeginTextEdit;
        layers.AutoLayoutRequested += () => _ = PrepareAutoLayout();
        layers.EditShapeRequested += layer => { session?.SelectLayer(layer.Id); SelectTool(Tool.Shape); };
        layers.EditAdjustmentRequested += layer => _ = EditAdjustmentLayer(layer, isNew: false);
        layers.NewAdjustmentRequested += kind => _ = NewAdjustmentLayer(kind);
        layers.EditEffectRequested += (layer, kind) => _ = EditEffect(layer, kind);
        layers.NewEffectRequested += kind => _ = NewEffect(kind);
        layers.EditTagsRequested += layer => _ = EditLayerTags(layer);
        layers.EditBlendingRequested += layer => _ = EditBlending(layer);
        layers.EditMaskPropertiesRequested += layer => _ = EditMaskProperties(layer);
        layers.EditSmartObjectRequested += layer => OpenSmartObject(layer);
        layers.ReplaceSmartObjectRequested += (layer, relink) => _ = ChooseSmartObjectFile(relink, layer, relink);
        layers.UpdateLinkedSmartObjectRequested += layer => _ = UpdateLinkedFromUi(layer);
        layers.LinkedSourceInfoRequested += layer => _ = LinkedSourceInfo(layer);
        channels.NewRequested += fromSelection => _ = NewAlphaChannel(fromSelection);
        channels.RenameRequested += name => _ = RenameAlphaChannel(name);
        channels.Problem += ShowProblem;
        layers.EditSmartFilterRequested += (layer, filter) => _ = SmartFilter(filter.Settings.Kind, layer, filter);
        layers.EditSmartFilterBlendingRequested += (layer, filter) => _ = SmartFilterBlending(layer, filter);

        AddHandler(KeyDownEvent, OnWindowKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, e) => canvas.ModifierKeyChanged(e.Key, e.KeyModifiers, down: true), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, (_, e) => canvas.ModifierKeyChanged(e.Key, e.KeyModifiers, down: false), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, (_, e) => { if (!SwallowAlt(e)) canvas.HandleKeyUp(e); }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);
        Closing += OnClosing;
        Closed += (_, _) => { automationCancellation?.Cancel(); assistantServer.Dispose(); aiControl?.Dispose(); imageExchange?.Dispose(); };
        if (Settings.Persist)
            Opened += async (_, _) =>
            {
                try { await aiTasks.TestConnectionAsync(); }
                catch { /* ComfyUI is optional; the menu becomes active when a later connection test succeeds. */ }
            };

        SetSession(null);
        if (settings.AllowAiControl) _ = SetAiControl(true);
        // Image exchange is independent of full AI control. Headless tests use isolated hosts.
        if (Settings.Persist) _ = StartImageExchange(Mcp.McpPipe.ExchangeName);

        if (recovery != null)
        {
            var autosave = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
            autosave.Tick += async (_, _) => { await recovery.Save(sessions); UpdateStatus(); };
            Closed += (_, _) => autosave.Stop();
            autosave.Start();
        }
        Opened += async (_, _) =>
        {
            if (Settings.Persist) _ = DiscoverStartupScripts();
            if (settings.StorageNotice is { } notice) ShowNote(notice);
            if (recovery != null) await OfferRecovery();
            if (Settings.Persist && IsVisible) await ShowEditorWelcome();
        };
        InitializeDisplayColor();
        InitializeLinkedSources();
    }

    private async Task OfferRecovery()
    {
        List<Recovery.Entry> abandoned;
        try { abandoned = await Task.Run(recovery!.FindAbandoned); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { ShowProblem("Couldn't read recovery copies: " + error.Message); return; }
        if (abandoned.Count == 0) return;
        var choices = abandoned.Select(entry => (Entry: entry, Check: new CheckBox
        {
            Content = $"{entry.Title} · {entry.SavedAt:g}", IsChecked = true
        })).ToArray();
        var selected = Ui.Column(4, choices.Select(choice => (Control)choice.Check).ToArray());
        var dialog = new Dialogs.DialogWindow("Recover unsaved work",
            Ui.Column(8, Ui.Label(Locale.T("Choose the documents to recover.")), new ScrollViewer { Content = selected, MaxHeight = 300 }), "Recover selected");
        dialog.SetCancelLabel("Later");
        bool deleting = false;
        dialog.AddSecondaryAction("Delete selected…", async () =>
        {
            if (await Dialogs.Prompts.Confirm(dialog, "Delete recovery copies?", "Permanently delete the selected recovery copies?", "Delete"))
            { deleting = true; dialog.Close(false); }
        });
        var recover = await dialog.Ask(this);
        if (!recover && !deleting) return;
        foreach (var (entry, check) in choices.Where(choice => choice.Check.IsChecked == true))
        {
            if (recover)
            {
                try
                {
                    var restored = new EditorSession(await Task.Run(() => Composa.IO.ProjectFile.Load(entry.ProjectPath))) { SuggestedName = entry.Title + " (recovered)" };
                    restored.History.BaseName = "Recovered";
                    restored.MarkModified();
                    AddSession(restored);
                    recovery.Adopt(restored, entry);
                    continue;
                }
                catch (Exception error)
                {
                    await Dialogs.Prompts.Alert(this, "Couldn't recover " + entry.Title, error.Message + "\n\nThe autosaved copy was kept at " + entry.ProjectPath);
                    continue;
                }
            }
            recovery.Discard(entry);
        }
    }

    private static T AddAt<T>(Grid grid, T control, int column) where T : Control
    {
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
        return control;
    }

    private static void AddRow(Grid grid, Control control, int row)
    {
        Grid.SetRow(control, row);
        grid.Children.Add(control);
    }

    public EditorSession? Session => session;
    /// <summary>Every open document, in tab order.</summary>
    public IReadOnlyList<EditorSession> Sessions => sessions;
    /// <summary>Whether a drag on the canvas is mutating a bitmap in place, when nothing else may touch the document.</summary>
    internal bool IsDragging => canvas.IsDragging || channelInspectorDragging;
    /// <summary>The remembered preferences; tests read them back without anything reaching disk.</summary>
    public Settings Settings => settings;
    public CanvasView Canvas => canvas;
    /// <summary>The tool rail's button for a tool.</summary>
    public ToolButton RailButton(Tool tool) => toolButtons[RailTool(tool)];

    private static Tool RailTool(Tool tool) => tool switch
    {
        Tool.SelectionBrush or Tool.ObjectSelectionAi => Tool.Wand,
        Tool.Bucket => Tool.Gradient,
        Tool.TextOnPath or Tool.VerticalText => Tool.Text,
        Tool.Patch or Tool.HealingBrush or Tool.SpotHealing => Tool.CloneStamp,
        _ => tool
    };

    // ---- Sessions and tabs --------------------------------------------------------------------------------------

    public void AddSession(EditorSession added)
    {
        // The first document starts from the remembered view options; later ones inherit them from the current tab.
        if (lastToolSource == null)
        {
            added.View = settings.View; added.PaintBrush = settings.Brush; added.EraserBrush = settings.EraserBrush ?? settings.Brush;
            if (settings.ToolPalette is { } palette)
            {
                added.Tool = Enum.IsDefined(palette.Active) ? palette.Active : Tool.Move;
                if (Enum.IsDefined(palette.Marquee)) added.MarqueeKind = palette.Marquee;
                if (Enum.IsDefined(palette.Lasso)) added.LassoKind = palette.Lasso;
                if (Enum.IsDefined(palette.Wand)) added.WandMode = palette.Wand;
                if (Enum.IsDefined(palette.Smear)) added.SmearMode = palette.Smear;
                if (Enum.IsDefined(palette.Shape)) added.ShapeKind = palette.Shape;
                selectionTool = palette.SelectionTool switch
                {
                    Tool.Wand or Tool.SelectionBrush => palette.SelectionTool,
                    _ when palette.SelectionBrush => Tool.SelectionBrush,
                    _ => Tool.Wand
                };
                if (palette.ObjectAi || palette.Active == Tool.ObjectSelectionAi) added.WandMode = WandMode.Object;
                if (added.Tool == Tool.Brush && palette.Eraser) added.Tool = Tool.Eraser;
                stampUsesPatch = palette.Patch;
                stampGroupTool = SavedStampTool(palette);
                gradientShowsBucket = palette.Bucket;
            }
        }
        sessions.Add(added);
        added.HistoryChanged += UpdateTabTitles;
        added.Problem += message => { if (added == session) ShowProblem(message); };
        added.LayersChanged += () => { if (added == session) OnSessionLayersChanged(); };
        added.ChannelsChanged += () => { if (added == session) { UpdateStatus(); if (added.IsEditingMask != optionsEditingMask) RebuildOptions(); else refreshOptions?.Invoke(); } };
        added.TextChanged += () => { if (added == session) refreshOptions?.Invoke(); };
        added.SelectionChanged += () =>
        {
            if (added != session) return;
            floatingBoundsMask = null;
            refreshOptions?.Invoke();
            RefreshAiUi();
        };
        SetSession(added);
    }

    private void SetSession(EditorSession? next)
    {
        if (session != null && session != next)
        {
            session.CommitChannelControlEdit();
            if (!CommitWarp(session)) return;
            canvas.CancelInteraction();
            if (session.IsPreviewing) session.CancelPreview();
            if (session.IsEditingText) session.FinishText();
            if (session.ColorRange != null) session.CommitColorRange();
        }
        var tool = session?.Tool ?? Tool.Move;
        previewRevision++;
        previewContext = null;
        session = next;
        floatingBoundsMask = null;
        modelDrafts.Clear();
        manualDockTask = null;
        aiFloatingPinnedPosition = null;
        if (session != null) CarryToolState(session, tool);
        canvas.Session = session;
        layers.Session = session;
        RefreshLibraryActions();
        history.Session = session;
        channels.Session = session;
        navigator.Session = session;
        welcome.IsVisible = session == null;
        dock.LeftHost.IsVisible = dock.IsVisible = session != null;
        RebuildTabs();
        RebuildOptions();
        RefreshCanvasLayoutView();
        UpdateColors();
        UpdateStatus();
        RefreshAiUi();
        if (session != null) canvas.Focus();
    }

    private EditorSession? lastToolSource;

    /// <summary>Tool choice, colors and brush settings follow the user from tab to tab.</summary>
    private void CarryToolState(EditorSession target, Tool tool)
    {
        target.Detect = LocalDetect;
        if (lastToolSource is { } from && from != target)
        {
            target.Foreground = from.Foreground; target.Background = from.Background; target.PaintBrush = from.PaintBrush; target.EraserBrush = from.EraserBrush;
            target.GradientRamp = from.GradientRamp;
            target.SmearMode = from.SmearMode; target.MarqueeKind = from.MarqueeKind; target.LassoKind = from.LassoKind;
            target.Feather = from.Feather; target.WandTolerance = from.WandTolerance; target.WandContiguous = from.WandContiguous;
            target.SampleAllLayers = from.SampleAllLayers; target.CloneAligned = from.CloneAligned; target.ShapeKind = from.ShapeKind;
            target.ShapePoints=from.ShapePoints; target.StarInnerRatio=from.StarInnerRatio; target.ShapeCornerRadius = from.ShapeCornerRadius; target.GradientRadial = from.GradientRadial; target.GradientToTransparent = from.GradientToTransparent; target.TextDefaults = from.TextDefaults;
            target.ShapeLineWidth = from.ShapeLineWidth; target.WandMode = from.WandMode; target.ObjectEdgeOffset = from.ObjectEdgeOffset; target.View = from.View;
            target.SelectionExpandAmount = from.SelectionExpandAmount; target.SelectionContractAmount = from.SelectionContractAmount; target.SelectionFeatherAmount = from.SelectionFeatherAmount;
            target.CropRatio = from.CropRatio;
            target.SelectionBrushSize = from.SelectionBrushSize; target.SelectionBrushFeather = from.SelectionBrushFeather; target.SelectionBrushMode = from.SelectionBrushMode;
            target.Tool = tool;
        }
        lastToolSource = target;
        ShowTool(target.Tool);
    }

    private readonly List<(EditorSession Document, TextBlock Title, TextBlock Mode, MenuItem Folder)> tabTitles = [];

    private void UpdateDocumentInfo()
    {
        documentInfo.Text = session?.Document is { } document
            ? $"{document.Width} × {document.Height} px · {document.Resolution:0.##} PPI" : "";
        ToolTip.SetTip(documentInfo, documentInfo.Text);
    }

    private void UpdateTabTitles()
    {
        foreach (var (item, title, mode, folder) in tabTitles)
        {
            title.Text = (item.FilePath == null && item.SuggestedName == null ? Locale.T("Untitled") : item.Title) + (item.IsModified ? " •" : "");
            mode.Text = $"(RGB/{(int)item.Document.WorkingDepth})";
            ToolTip.SetTip(mode, item.Document.Profile.Name);
            folder.IsEnabled = item.FilePath != null;
        }
        UpdateDocumentInfo();
        Title = session == null ? "Composa" : $"{session.Title}{(session.IsModified ? " •" : "")} - Composa";
    }

    private void RebuildTabs()
    {
        tabs.Children.Clear();
        tabTitles.Clear();
        foreach (var item in sessions)
        {
            var label = Ui.Label("", item == session ? Palette.Foreground : Palette.Secondary);
            var mode = Ui.Label("", Palette.Secondary, 11.5);
            var close = new Button { Classes = { "flat" }, Padding = new Thickness(3), Content = Icons.Create(Icons.Close, 10), VerticalAlignment = VerticalAlignment.Center };
            close.Click += (_, e) => { _ = CloseSession(item); e.Handled = true; };
            var tab = new Border
            {
                Child = Ui.Row(5, label, mode, close), Padding = new Thickness(12, 4, 6, 4), CornerRadius = new CornerRadius(6),
                Background = item == session ? Palette.ActiveTab : Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand)
            };
            if (item != session)
            {
                tab.PointerEntered += (_, _) => tab.Background = Palette.Hover;
                tab.PointerExited += (_, _) => tab.Background = Brushes.Transparent;
            }
            tab.PointerPressed += (_, e) =>
            {
                // A right-click opens the tab's menu without switching to it, as browsers do.
                var pressed = e.GetCurrentPoint(tab).Properties;
                if (pressed.IsMiddleButtonPressed) _ = CloseSession(item);
                else if (pressed.IsLeftButtonPressed && item != session) SetSession(item);
            };
            tab.ContextMenu = TabMenu(item, out var folder);
            tabTitles.Add((item, label, mode, folder));
            tabs.Children.Add(tab);
            if (item == session)
                Dispatcher.UIThread.Post(() => { if (ReferenceEquals(tab.Parent, tabs)) tab.BringIntoView(); }, DispatcherPriority.Loaded);
        }
        UpdateTabTitles();
    }

    /// <summary>A click or a scrub in the History panel. Like any command it waits for a drag on the canvas to end.</summary>
    private void GoToHistory(int index)
    {
        if (session == null || canvas.IsDragging) return;
        var typing = session.IsEditingText;
        problem = note = null;
        session.GoToHistory(index);
        // Going to another state commits the text being typed, which the Type bar has to hear about.
        if (typing) RebuildOptions();
        UpdateStatus();
    }

    /// <summary>What a right-click on a tab offers, for that document whether or not it is the current one.</summary>
    private ContextMenu TabMenu(EditorSession item, out MenuItem folder)
    {
        MenuItem Entry(string header, Action run, bool enabled = true)
        {
            var entry = new MenuItem { Header = header, IsEnabled = enabled };
            entry.Click += (_, _) => run();
            return entry;
        }
        folder = Entry("Open Containing Folder", () => _ = OpenContainingFolder(item.FilePath!), item.FilePath != null);
        return new ContextMenu
        {
            Items =
            {
                Entry("Copy Image", () => _ = CopyImage(item)),
                Entry("Duplicate", () => AddSession(item.Duplicate())),
                new Separator(),
                folder,
                new Separator(),
                Entry("Close", () => _ = CloseSession(item)),
                Entry("Close Others", () => _ = CloseOthers(item), sessions.Count > 1)
            }
        };
    }

    /// <summary>Closes every other document, each asking about unsaved changes as Close does; a Cancel stops there.</summary>
    private async Task CloseOthers(EditorSession keep)
    {
        foreach (var other in sessions.Where(s => s != keep).ToList())
            if (!await CloseSession(other)) return;
        if (session != keep && sessions.Contains(keep)) SetSession(keep);
    }

    private async Task OpenContainingFolder(string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder == null || !Directory.Exists(folder) || !await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder)))
            ShowProblem("Couldn't open the folder " + (folder ?? path) + ".");
    }

    private IDisposable? closeCollection;

    private async Task<bool> CloseSession(EditorSession item)
    {
        if (!sessions.Contains(item)) return true;
        item.CommitChannelControlEdit();
        if (!CommitWarp(item)) return false;
        // Contents must be resolved before the parent's save prompt, including nested tabs.
        foreach (var child in embeddedTabs.Where(pair => pair.Value.Parent == item).Select(pair => pair.Key).ToArray())
            if (!await CloseSession(child)) return false;
        // A save still writing finishes first, so its file is never cut short and the prompt knows whether it is needed.
        await WaitForSave(item);
        // Text still being typed is an open edit: commit it so it counts as a change and is in what gets saved.
        if (item.IsEditingText) item.FinishText();
        if (item.IsModified)
        {
            if (item != session) SetSession(item);
            var answer = await Dialogs.Prompts.SaveChanges(this, item.Title);
            if (answer == null) return false;
            if (answer == true)
            {
                if (!await Save(item, saveAs: false)) return false;
                if (item.IsModified) { ShowNote("Newer edits are still open. Save again to close."); return false; }
            }
        }
        var index = sessions.IndexOf(item);
        sessions.Remove(item);
        fileVersions.Remove(item);
        foreach (var key in linkedObservations.Keys.Where(key => key.Parent == item).ToArray()) linkedObservations.Remove(key);
        embeddedTabs.Remove(item);
        recovery?.Forget(item);
        item.HistoryChanged -= UpdateTabTitles;
        if (lastToolSource == item) lastToolSource = null;
        if (item == session) SetSession(sessions.Count == 0 ? null : sessions[Math.Clamp(index, 0, sessions.Count - 1)]);
        else RebuildTabs();
        item.ReleaseClosedResources(sessions);
        GpuAcceleration.ReleaseCanvasCache();
        // Let the close call and the detached controls release their last references before
        // collecting native raster wrappers. Closing several tabs only schedules one collection.
        closeCollection?.Dispose();
        closeCollection = DispatcherTimer.RunOnce(
            static () => GC.Collect(2, GCCollectionMode.Forced, blocking: false),
            TimeSpan.FromMilliseconds(250), DispatcherPriority.Background);
        return true;
    }

    private bool closingConfirmed;

    private void RememberWindow()
    {
        settings.Maximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal) { settings.WindowWidth = Width; settings.WindowHeight = Height; }
        settings.JpegQuality = jpegQuality;
        RememberToolSettings();
    }

    /// <summary>
    /// Auto Select, the transform controls, the pixel grid, rulers, guides, the grid and the snapping switches keep
    /// whatever they were last set to, across tabs and across launches, the way Photoshop's tool options do.
    /// </summary>
    private void RememberToolSettings()
    {
        settings.ShowPixelGrid = canvas.ShowPixelGrid;
        settings.ShowTransformControls = canvas.ShowTransformControls;
        settings.AutoSelect = canvas.AutoSelect;
        if (session != null) { settings.View = session.View; settings.Brush = session.PaintBrush; settings.EraserBrush = session.EraserBrush; RememberToolPalette(); }
        settings.Save();
    }

    /// <summary>Whether the MCP server is running, so an AI agent can connect and drive the editor.</summary>
    public bool AiControl => aiControl != null;

    internal async Task<bool> StartImageExchange(string pipeName)
    {
        if (imageExchange != null) return true;
        var host = new Mcp.McpHost(this, pipeName, exchangeOnly: true);
        if (!await host.StartAsync()) { host.Dispose(); return false; }
        imageExchange = host; return true;
    }

    public async Task SetAiControl(bool on)
    {
        if (on == AiControl) return;
        settings.AllowAiControl = on;
        settings.Save();
        if (on)
        {
            var host = new Mcp.McpHost(this);
            host.ConnectionsChanged += UpdateAiText;
            if (await host.StartAsync()) aiControl = host;
            else ShowProblem("Another Composa window already allows AI control; agents reach that one.");
        }
        else
        {
            aiControl!.Dispose();
            aiControl = null;
        }
        UpdateAiText();
    }

    private void UpdateAiText()
    {
        var connected = aiControl?.Connections ?? 0;
        aiText.IsVisible = connected > 0;
        aiText.Text = connected == 1 ? "AI connected" : $"{connected} AIs connected";
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        var applyUpdate = restartForUpdate;
        restartForUpdate = false;
        if (Content is Control { IsEnabled: false })
        {
            e.Cancel = true; automationCancellation?.Cancel();
            ShowProblem("Stop the running script before closing the editor."); return;
        }
        RememberWindow();
        foreach (var item in sessions)
        {
            item.CommitChannelControlEdit();
            if (!CommitWarp(item)) { e.Cancel = true; return; }
        }
        if (session?.IsEditingText == true) session.FinishText();
        if (closingConfirmed || (saving.Count == 0 && sessions.All(s => !s.IsModified)))
        {
            if (applyUpdate && preparedUpdate != null)
            {
                try { deltaUpdates!.WaitExitThenApplyUpdates(preparedUpdate, silent: true, restart: true); }
                catch (Exception error) { e.Cancel = true; await Dialogs.Prompts.Alert(this, "Update", error.Message); }
            }
            return;
        }
        e.Cancel = true;
        // Saves still writing finish before the window goes, so no file is cut short.
        while (saving.Count > 0) await WaitForSave(saving.Keys.First());
        while (sessions.FirstOrDefault(s => s.IsModified) is { } item)
            if (!await CloseSession(item)) return;
        closingConfirmed = true;
        restartForUpdate = applyUpdate;
        Close();
    }

    // ---- Chrome -------------------------------------------------------------------------------------------------

    private Control BuildTabBar()
    {
        var newButton = Ui.IconButton(Icons.Plus, "New canvas (Ctrl+N)", () => _ = NewCanvas());
        var scroll = new ScrollViewer { Content = tabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        zoomText.Name = "DocumentZoom";
        zoomText.Margin = new Thickness(6, 0);
        var zoomControls = Ui.Row(2, LayoutViewButtons(), zoomText,
            Ui.TextButton("Fit", () => canvas.Fit()), Ui.TextButton("100%", () => canvas.ZoomTo(1)),
            Ui.IconButton(Icons.ZoomOut, "Zoom out (Ctrl+-)", canvas.ZoomOut), Ui.IconButton(Icons.ZoomIn, "Zoom in (Ctrl++)", canvas.ZoomIn));
        foreach (var button in zoomControls.Children.OfType<Button>()) { button.MinWidth = 0; button.Classes.Add("flat"); }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Background = Palette.Window, Margin = new Thickness(6, 2) };
        grid.Children.Add(newButton);
        var strip = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(6, 0) };
        Button Arrow(int direction, Icons.Icon icon, string tooltip, string name)
        {
            var button = Ui.IconButton(icon, tooltip, () =>
            {
                var step = Math.Max(32, scroll.Viewport.Width * .75);
                var end = Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width);
                scroll.Offset = new Vector(Math.Clamp(scroll.Offset.X + direction * step, 0, end), 0);
            }, 14);
            button.Name = name;
            button.Classes.Add("flat");
            button.Width = 24;
            button.MinWidth = 0;
            button.Padding = new Thickness(0);
            button.IsVisible = false;
            return button;
        }
        var before = Arrow(-1, Icons.ChevronLeft, "Scroll document tabs left", "DocumentTabsLeft");
        var after = Arrow(1, Icons.ChevronRight, "Scroll document tabs right", "DocumentTabsRight");
        strip.Children.Add(before);
        AddAt(strip, scroll, 1);
        AddAt(strip, after, 2);
        scroll.ScrollChanged += (_, _) =>
        {
            var overflow = scroll.Extent.Width > strip.Bounds.Width + .5;
            before.IsVisible = after.IsVisible = overflow;
            before.IsEnabled = scroll.Offset.X > .5;
            after.IsEnabled = scroll.Offset.X + scroll.Viewport.Width < scroll.Extent.Width - .5;
        };
        AddAt(grid, strip, 1);
        AddAt(grid, zoomControls, 2);
        grid.Name = "DocumentTabBar";
        DragDrop.SetAllowDrop(grid, true);
        grid.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (e.DataTransfer.TryGetFiles()?.Any(f => f.TryGetLocalPath() != null) != true) return;
            e.DragEffects &= DragDropEffects.Copy;
            e.Handled = true;
        });
        grid.AddHandler(DragDrop.DropEvent, OnTabDrop);
        return grid;
    }

    private static readonly (Tool Tool, Icons.Icon Icon, string Tip)[] ToolList =
    [
        (Tool.Move, Icons.Move, "Move / Transform (V)"), (Tool.Wand, Icons.Wand, "Magic Wand (W)"),
        (Tool.Lasso, Icons.Lasso, "Lasso (L)"), (Tool.Marquee, Icons.Marquee, "Marquee (M)"),
        (Tool.Crop, Icons.Crop, "Crop (C)"), (Tool.Brush, Icons.Brush, "Brush (B)"), (Tool.Eraser, Icons.Eraser, "Eraser (E)"),
        (Tool.RemoveObject, Icons.RemoveObject, "Remove Object · paint an area to remove with AI"),
        (Tool.CloneStamp, Icons.Stamp, "Clone Stamp (S) · Alt-click sets the source"),
        (Tool.Smear, Icons.Drop, "Smear (R)"), (Tool.Gradient, Icons.Gradient, "Gradient (G)"), (Tool.Shape, Icons.Shape, "Shape (U)"),
        (Tool.Text, Icons.Text, "Type (T) · click for point text, drag a paragraph box, click text to edit it"), (Tool.Eyedropper, Icons.Eyedropper, "Eyedropper (I)"),
        (Tool.Pen, Icons.Pen, "Pen (P) · drag points on the selected vector layer · double-click a segment to add a point · click away from the contour to draw · Enter finishes"),
        (Tool.Hand, Icons.Hand, "Hand (H) · hold Space with any tool"), (Tool.Zoom, Icons.Zoom, "Zoom (Z)")
    ];

    /// <summary>
    /// The tools a rail button holds, which it offers beside itself when held down, as Photoshop groups its tools. Empty for a
    /// tool on its own. The keys are looked up when the group opens, so a rebound key shows as it is now.
    /// </summary>
    private Tool? selectionTool;
    private bool stampUsesPatch;
    private Tool stampGroupTool;
    private static Tool SavedStampTool(ToolPaletteSettings? palette) => palette?.Choices?.GetValueOrDefault(nameof(Tool.CloneStamp)) switch
    {
        "Healing Brush" => Tool.HealingBrush,
        "Spot Healing Brush" => Tool.SpotHealing,
        _ => Tool.CloneStamp
    };
    private Tool StampPaintingTool => stampUsesPatch ? Tool.Patch : Tool.CloneStamp;
    private bool gradientShowsBucket;
    private Tool typeTool = Tool.Text;
    private static readonly Icons.Icon PathTypeIcon = new("M6 4h12 M12 4v10 M3 21Q12 12 21 21");
    private static readonly Icons.Icon VerticalTypeIcon = new("M3 4h10 M8 4v15 M18 5v14 m-3-3 3 3 3-3");
    private IReadOnlyList<ToolChoice> ToolGroup(Tool tool)
    {
        ToolChoice Choice(string name, Icons.Icon icon, string key, Func<EditorSession, bool> isCurrent, Action<EditorSession> apply, Tool? member = null) => new(
            name, icon, () => toolKeys.FirstOrDefault(k => k.Id == key)?.Gesture,
            () => (session ?? lastToolSource) is { } current ? isCurrent(current) : settings.ToolPalette?.Choices?.GetValueOrDefault(tool.ToString()) == name,
            () =>
            {
                if (session == null) return;
                apply(session);
                SelectTool(member ?? tool);
                canvas.Focus();
            });
        ToolChoice[] Kinds<T>(string key, Func<EditorSession, T> get, Action<EditorSession, T> set, params (T Kind, string Name, Icons.Icon Icon)[] kinds) where T : struct, Enum =>
            kinds.Select(k => Choice(k.Name, k.Icon, key, s => get(s).Equals(k.Kind), s => set(s, k.Kind))).ToArray();
        return tool switch
        {
            Tool.Marquee => Kinds(MarqueeKey, s => s.MarqueeKind, (s, v) => s.MarqueeKind = v,
                (MarqueeKind.Rectangle, "Rectangle Marquee", Icons.Marquee), (MarqueeKind.Ellipse, "Ellipse Marquee", Icons.MarqueeEllipse)),
            Tool.Lasso => Kinds(LassoKey, s => s.LassoKind, (s, v) => s.LassoKind = v,
                (LassoKind.Freehand, "Freehand Lasso", Icons.Lasso), (LassoKind.Polygonal, "Polygonal Lasso", Icons.PolygonLasso)),
            Tool.Wand =>
            [
                Choice("Magic Wand", Icons.Wand, MagicKey, s => selectionTool == Tool.Wand && s.WandMode == WandMode.Wand, s => s.WandMode = WandMode.Wand, Tool.Wand),
                Choice("Object Selection", Icons.ObjectSelect, MagicKey, s => selectionTool == Tool.Wand && s.WandMode == WandMode.Object, s => s.WandMode = WandMode.Object, Tool.Wand),
                Choice("Selection Brush", Icons.SelectionBrush, "Selection Brush", s => selectionTool == Tool.SelectionBrush, _ => { }, Tool.SelectionBrush)
            ],
            Tool.CloneStamp =>
            [
                new ToolChoice("Clone Stamp", Icons.Stamp, () => toolKeys.FirstOrDefault(k => k.Id == "Clone Stamp")?.Gesture,
                    () => stampGroupTool == Tool.CloneStamp, () => { SelectTool(StampPaintingTool); canvas.Focus(); }),
                Choice("Healing Brush", Icons.HealingBrush, "Clone Stamp", _ => stampGroupTool == Tool.HealingBrush, _ => { }, Tool.HealingBrush),
                Choice("Spot Healing Brush", Icons.Heal, "Spot Healing Brush", _ => stampGroupTool == Tool.SpotHealing, _ => { }, Tool.SpotHealing)
            ],
            Tool.Gradient =>
            [
                new ToolChoice("Gradient", Icons.Gradient, () => toolKeys.FirstOrDefault(k => k.Id == "Gradient tool")?.Gesture,
                    () => !gradientShowsBucket, () => { SelectTool(Tool.Gradient); canvas.Focus(); }),
                new ToolChoice("Paint Bucket", Icons.Bucket, () => toolKeys.FirstOrDefault(k => k.Id == "Gradient tool")?.Gesture,
                    () => gradientShowsBucket, () => { SelectTool(Tool.Bucket); canvas.Focus(); })
            ],
            Tool.Text =>
            [
                Choice("Type", Icons.Text, "Type tool", _ => typeTool == Tool.Text, _ => { }, Tool.Text),
                Choice("Text on Path", PathTypeIcon, "", _ => typeTool == Tool.TextOnPath, _ => { }, Tool.TextOnPath),
                Choice("Vertical Type", VerticalTypeIcon, "", _ => typeTool == Tool.VerticalText, _ => { }, Tool.VerticalText)
            ],
            Tool.Smear => Kinds(SmearKey, s => s.SmearMode, (s, v) => s.SmearMode = v,
                (SmearMode.Liquify, "Liquify", Icons.Liquify), (SmearMode.Blur, "Blur", Icons.Drop), (SmearMode.Smudge, "Smudge", Icons.Smudge),
                (SmearMode.Dodge, "Dodge", Icons.Dodge), (SmearMode.Burn, "Burn", Icons.Burn)),
            Tool.Shape => Kinds(ShapeKey, s => s.ShapeKind == ShapeKind.RoundedRectangle ? ShapeKind.Rectangle : s.ShapeKind, (s, v) => s.ShapeKind = v,
                (ShapeKind.Rectangle, ShapeStyle.DisplayName(ShapeKind.Rectangle), Icons.Rectangle),
                (ShapeKind.Polygon, "Polygon", new Icons.Icon("M12 3 L21 10 L18 21 H6 L3 10 Z")),
                (ShapeKind.Star, "Star", new Icons.Icon("M12 2 L15 9 L22 10 L17 15 L18 22 L12 18 L6 22 L7 15 L2 10 L9 9 Z")),
                (ShapeKind.Ellipse, ShapeStyle.DisplayName(ShapeKind.Ellipse), Icons.Ellipse), (ShapeKind.Line, ShapeStyle.DisplayName(ShapeKind.Line), Icons.Line)),
            _ => []
        };
    }

    private Control BuildToolRail()
    {
        var rail = new StackPanel { Spacing = 2, Margin = new Thickness(0, 8, 0, 8), HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var (tool, icon, tip) in ToolList)
        {
            var button = new ToolButton(icon, tip, ToolGroup(tool));
            button.Click += (_, _) => { if (button.Current is { } current) current.Choose(); else SelectTool(tool); };
            toolButtons[tool] = button;
            button.Refresh();
            rail.Children.Add(button);
        }

        foregroundSwatch.Cursor = backgroundSwatch.Cursor = new Cursor(StandardCursorType.Hand);
        Locale.Tip(foregroundSwatch, Locale.T("Foreground color"));
        Locale.Tip(backgroundSwatch, Locale.T("Background color"));
        foregroundSwatch.PointerPressed += (_, _) => _ = PickColor(foreground: true);
        backgroundSwatch.PointerPressed += (_, _) => _ = PickColor(foreground: false);
        backgroundSwatch.Margin = new Thickness(14, 14, 0, 0);
        foregroundSwatch.HorizontalAlignment = backgroundSwatch.HorizontalAlignment = HorizontalAlignment.Left;
        foregroundSwatch.VerticalAlignment = backgroundSwatch.VerticalAlignment = VerticalAlignment.Top;
        var swatches = new Panel { Width = 42, Height = 42, Margin = new Thickness(0, 8, 0, 0), Children = { backgroundSwatch, foregroundSwatch } };
        var colors = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
        colors.Children.Add(swatches);
        var swap = Ui.IconButton(Icons.Swap, "Swap colors (X) · D resets to black and white", () => { session?.SwapColors(); UpdateColors(); }, 14);
        swap.HorizontalAlignment = HorizontalAlignment.Center;
        colors.Children.Add(swap);

        rail.Children.Add(colors);
        var tools = new ScrollViewer
        {
            Content = rail, Width = 56, Background = Palette.Panel, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        // Keep all tools and the swatches together on ordinary-height windows.
        // Very small windows can still scroll the complete rail.
        tools.SizeChanged += (_, e) =>
        {
            if (rail.Orientation == Orientation.Horizontal) return;
            var height = Math.Clamp((e.NewSize.Height - 92) / ToolList.Length - rail.Spacing, 22, 33);
            foreach (var button in rail.Children.OfType<ToolButton>()) button.Height = height;
        };
        return tools;
    }

    private Panel BuildWelcome()
    {
        var content = Ui.Column(12);
        content.HorizontalAlignment = HorizontalAlignment.Center;
        content.VerticalAlignment = VerticalAlignment.Center;
        content.Width = 390;
        content.Children.Add(Ui.Label("Composa", size: 30, weight: FontWeight.SemiBold));
        content.Children.Add(Ui.Row(8, Ui.TextButton("Open…", () => _ = Open(), accent: true),
            Ui.TextButton("New Canvas…", () => _ = NewCanvas())));
        var recent = new StackPanel { Spacing = 2, Margin = new Thickness(0, 12, 0, 0) };
        void RefreshRecent()
        {
            foreach (var image in recent.GetVisualDescendants().OfType<Image>())
                if (image.Source is IDisposable bitmap) { image.Source = null; bitmap.Dispose(); }
            recent.Children.Clear();
            var paths = settings.RecentFiles.Where(p => File.Exists(p) || Directory.Exists(p)).Take(5).ToList();
            if (paths.Count == 0) { recent.Children.Add(Ui.Label(Locale.T("Drop an image or project here"), Palette.Secondary)); return; }
            recent.Children.Add(Ui.Label(Locale.T("Recent files"), Palette.Secondary, 12, FontWeight.SemiBold));
            foreach (var path in paths)
            {
                var preview = RecentPreviews.Load(path);
                Control visual = preview == null ? Icons.Create(Icons.Layers, 18, Palette.Secondary)
                    : new Image { Source = preview, Stretch = Stretch.Uniform };
                var tile = new Border { Width = 42, Height = 36, Child = visual, CornerRadius = new CornerRadius(4), Background = Palette.PanelRaised };
                var name = Ui.Label(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)), weight: FontWeight.SemiBold);
                var folder = Ui.Label(Path.GetDirectoryName(path) ?? "", Palette.Secondary, 11);
                name.TextTrimming = folder.TextTrimming = TextTrimming.CharacterEllipsis;
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,12,*") };
                row.Children.Add(tile); AddAt(row, Ui.Column(2, name, folder), 2);
                var open = new Button { Classes = { "flat" }, Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Stretch };
                ToolTip.SetTip(open, path); open.Click += (_, _) => _ = OpenPaths([path]);
                recent.Children.Add(open);
            }
        }
        content.Children.Add(recent);
        var panel = new Panel { Children = { content } };
        panel.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && panel.IsVisible) RefreshRecent(); };
        panel.AttachedToVisualTree += (_, _) => { if (panel.IsVisible) RefreshRecent(); };
        return panel;
    }

    private void UpdateColors()
    {
        if (session == null) return;
        foregroundSwatch.Background = new SolidColorBrush(session.Foreground.ToDisplay(session.Document.Profile, DisplayProfile));
        backgroundSwatch.Background = new SolidColorBrush(session.Background.ToDisplay(session.Document.Profile, DisplayProfile));
    }

    private async Task PickColor(bool foreground)
    {
        if (session == null) return;
        var target = session;
        // Text being typed follows the foreground color, so it previews the picker's working color as the Type bar's own
        // swatch does (on the selected letters, or all of them), and goes back to its own colors on Cancel.
        var editing = foreground ? target.TextEdit : null;
        var original = target.CurrentTextStyle;
        void Recolor(SKColor color) { if (editing != null && target.TextEdit == editing) target.SetTextColor((uint)color | 0xFF000000); }
        var picked = await Dialogs.Prompts.Color(this, foreground ? "Foreground Color" : "Background Color", foreground ? target.Foreground : target.Background, editing != null ? Recolor : null);
        if (picked is not { } color)
        {
            if (editing != null && target.TextEdit == editing) target.RestoreTextColors(original);
            return;
        }
        if (foreground) target.Foreground = color; else target.Background = color;
        Recolor(color);
        UpdateColors();
        refreshOptions?.Invoke();
    }

    /// <summary>Opens a text layer for typing on the canvas.</summary>
    private void BeginTextEdit(Layer layer)
    {
        if (session == null || layer.Text is not { } text) return;
        var tool = text.Path != null ? Tool.TextOnPath : text.IsVertical ? Tool.VerticalText : Tool.Text;
        if (session.Tool != tool) SelectTool(tool);
        canvas.EditText(layer);
        RebuildOptions();
        UpdateStatus();
    }

    public void SelectTool(Tool tool)
    {
        if (session == null) { foreach (var button in toolButtons.Values) button.IsChecked = false; return; }
        session.Tool = tool;
        problem = note = null;
        ShowTool(session.Tool);
        canvas.ToolChanged();
        RebuildOptions(updateLayout: false);
        UpdateStatus();
        // The Move bar has a second row. Settle the viewport before the next pointer event maps its coordinates.
        UpdateLayout();
    }

    /// <summary>Marks the tool's button and shows each group's current tool on its button.</summary>
    private void ShowTool(Tool tool)
    {
        if (session == null) return;
        if (RailTool(tool) == Tool.Wand) selectionTool = tool == Tool.SelectionBrush ? tool : Tool.Wand;
        if (tool is Tool.CloneStamp or Tool.Patch) stampUsesPatch = tool == Tool.Patch;
        if (RailTool(tool) == Tool.CloneStamp) stampGroupTool = tool == Tool.Patch ? Tool.CloneStamp : tool;
        if (tool is Tool.Gradient or Tool.Bucket) gradientShowsBucket = tool == Tool.Bucket;
        if (RailTool(tool) == Tool.Text) typeTool = tool;
        foreach (var (key, button) in toolButtons)
        {
            button.IsChecked = key == RailTool(tool);
            button.Refresh();
        }
        RememberToolPalette();
        settings.SaveInBackground();
    }

    private void RememberToolPalette()
    {
        if ((session ?? lastToolSource) is not { } s) return;
        settings.ToolPalette = new()
        {
            Active = s.Tool, Marquee = s.MarqueeKind, Lasso = s.LassoKind, Wand = s.WandMode,
            Smear = s.SmearMode, Shape = s.ShapeKind, Eraser = s.EraserMode,
            SelectionTool = selectionTool, Patch = stampUsesPatch, Bucket = gradientShowsBucket,
            Choices = toolButtons.Where(pair => pair.Value.Current != null)
                .ToDictionary(pair => pair.Key.ToString(), pair => pair.Value.Current!.Name)
        };
    }

    internal void RefreshLanguage()
    {
        RebuildOptions();
        UpdateTabTitles();
        UpdateStatus();
        foreach(var tool in toolButtons.Values) tool.Refresh();
    }

    private void UpdateStatus()
    {
        hintText.Text = Locale.Message(problem ?? recovery?.Warning ?? (saving.Count > 0
            ? "Saving " + string.Join(", ", saving.Values.Select(w => Path.GetFileName(w.Path))) + "…" : note) ?? "");
        commandNotice.IsVisible = !string.IsNullOrEmpty(hintText.Text);
        hintText.Foreground = problem != null ? new SolidColorBrush(Color.Parse("#FFB454")) : Palette.Secondary;
        if (session == null)
        {
            zoomText.Text = "";
            return;
        }
        zoomText.Text = canvas.Zoom >= 0.1 ? $"{canvas.Zoom * 100:0.#}%" : $"{canvas.Zoom * 100:0.##}%";
    }

    private static string Hint(EditorSession s) => s.WarpEdit != null ? "Drag Warp nodes · Shift locks direction · Arrows nudge selected node · Ctrl+Z undoes a point · Enter applies · Esc cancels"
        : s.IsEditingAlphaChannel || s.IsEditingComponent
        ? (s.IsEditingAlphaChannel ? "Alpha channel · " : "RGB component · ") + (s.Tool == Tool.Move ? "Drag channel contents · Handles transform · A selection limits the edit · Alt-drag duplicates" : "White adds · Black removes · Gray sets partial intensity · Other channels stay unchanged")
        : s.Tool == Tool.Move ? ToolHint(s) : ToolHint(s) + " · Ctrl-drag moves the layer";

    private static string ToolHint(EditorSession s) => s.Tool switch
    {
        Tool.Move when s.ActiveLayer is { } item && s.AutoLayoutParent(item) != null =>
            "Drag the selected layout item to reorder it · Ctrl-click picks a layer",
        Tool.Move when s.ActiveLayer?.AutoLayout != null =>
            "Drag to move the whole layout group · Ctrl-click picks an item · Drag the gap label to adjust spacing",
        Tool.Move => "Drag to move · Handles resize (Shift free, Alt from center) · Outside a corner rotates · Ctrl-drag a handle distorts · Ctrl-click picks a layer · 1–0 opacity",
        Tool.Marquee => "Drag to select · Shift add · Alt subtract · Shift+Alt intersect · Drag inside to move · Delete clears · Ctrl+D deselect",
        Tool.Lasso => s.LassoKind == LassoKind.Freehand ? "Drag to select · Shift add · Alt subtract · Drag inside to move" : "Click corners · Click the start, double-click or Enter to close · Backspace removes a corner · Escape cancels",
        Tool.Wand => s.WandMode == WandMode.Object ? "Click an object or drag a rectangle · Tab for Wand · Shift add · Alt subtract" : "Click to select similar colors · Tab for Object · Shift add · Alt subtract",
        Tool.SelectionBrush => "Paint the selection mask · Shift adds · Alt subtracts · [ ] changes size · Ctrl+D deselects",
        Tool.RemoveObject => "Paint over the object · release to remove with AI · [ ] changes size",
        Tool.Bucket => "Click to fill connected pixels with the foreground color",
        Tool.Crop => "Drag to crop · Shift keeps proportions · Alt symmetric · Enter applies · Escape cancels",
        Tool.Brush or Tool.Eraser => (s.EraserMode ? "Drag to erase" : "Drag to paint · Alt-click picks a color") + " · Shift-click draws a line · [ ] size · { } hardness · 1–0 opacity",
        Tool.SpotHealing => "Drag over blemishes to heal · [ ] size",
        Tool.CloneStamp => "Alt-click sets the source · Drag to clone · [ ] size · 1–0 opacity",
        Tool.HealingBrush => "Alt-click sets the texture source · Drag to heal · [ ] size",
        Tool.Patch => "Select an area, then drag it to the donor · Escape cancels",
        Tool.Smear => "Drag to " + (s.SmearMode == SmearMode.Liquify ? "push pixels" : s.SmearMode.ToString().ToLowerInvariant()) + " · [ ] size · 1–0 strength",
        Tool.Gradient => "Drag to draw from foreground to " + (s.GradientToTransparent ? "transparent" : "background") + " · Drag an end to adjust · Shift snaps to 45° · Enter applies · Escape cancels",
        Tool.Shape => s.ShapeKind == ShapeKind.Line ? "Drag to draw a line on a new layer · Shift snaps to 45° · Tab for the next shape" : "Click for dimensions · Drag to draw a shape on a new layer · Shift square · Alt from center · Tab for the next shape",
        Tool.Text => s.IsEditingText ? "Type · Drag the box's handles to resize it · Alt+arrows tracking and leading · Ctrl+Enter finishes · Escape cancels" : "Click for point text · Drag a box for paragraph text · Click text to edit it",
        Tool.TextOnPath => s.IsEditingText ? "Drag the start handle · Enter finishes · Escape cancels" : "Click a vector contour where the text should begin",
        Tool.VerticalText => "Click for vertical type · Drag a box for vertical columns",
        Tool.Eyedropper => "Click to pick the foreground color · Alt-click for the background",
        Tool.Hand => "Drag to pan · Ctrl+wheel zooms",
        _ => "Click to zoom in · Alt-click to zoom out · Drag right or left to zoom smoothly"
    };

    private bool reportingFailure;

    /// <summary>Tells the user about an unexpected error, once at a time, after putting the editor back into a sane state.</summary>
    public void ReportFailure(Exception error)
    {
        try { canvas.CancelInteraction(); }
        catch (Exception secondary) { Console.Error.WriteLine(secondary); }
        if (reportingFailure) return;
        reportingFailure = true;
        _ = Show();

        async Task Show()
        {
            try
            {
                await Dialogs.Prompts.Alert(this, "Something went wrong",
                    $"{error.GetType().Name}: {error.Message}\n\nThe last action may not have completed. Your document is still open; saving a copy now (File > Save As) is a good idea.");
            }
            finally { reportingFailure = false; }
        }
    }

    public void ShowProblem(string message)
    {
        problem = message;
        UpdateStatus();
    }

    private void ShowNote(string message)
    {
        note = message;
        UpdateStatus();
    }
}
