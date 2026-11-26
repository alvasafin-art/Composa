using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Composa.AI;
using Composa.App.AI;
using Composa.App.Controls;
using Composa.Rendering;
using Composa.Editing;

namespace Composa.App;

public sealed partial class MainWindow
{
    private readonly DockSection generationSection;
    private readonly List<(Control Editor, Border Floating, Border Docked)> generationSlots = [];
    private readonly TextBlock dockOperationName = Ui.Label("", weight: FontWeight.SemiBold);
    private Button dockOperation = null!;
    private bool editorInDock;
    private AiTaskKind automaticDockTask = AiTaskKind.GenerateImage;
    private AiTaskKind? manualDockTask;
    private EditorSession? floatingTaskSession;
    private AiTaskKind? manualFloatingTask;
    private AiTaskKind EditorTask => editorInDock ? manualDockTask ?? automaticDockTask : manualFloatingTask ?? AiTaskKind.GenerativeFill;
    private bool DockEditorVisible => session != null && !settings.CanvasOnly && dock.Presents(generationSection);
    private AiTaskKind? presentedTask;

    private static string DockTaskName(AiTaskKind task) => task switch
    {
        AiTaskKind.GenerativeFill => "Gen Fill",
        AiTaskKind.GenerativeExpand => "Gen Expand",
        AiTaskKind.ImageEdit => "Edit Image",
        _ => task.DisplayName()
    };

    // Both presentations host the very same editor. This preserves the text selection,
    // undo buffer, ordered reference thumbnails and per-operation controls on a handoff.
    private Border DockSlot(Control editor)
    {
        var parent = (Panel)editor.Parent!;
        var floating = new Border();
        var docked = new Border();
        parent.Children[parent.Children.IndexOf(editor)] = floating;
        floating.Child = editor;
        floating.Bind(IsVisibleProperty, editor.GetObservable(IsVisibleProperty));
        docked.Bind(IsVisibleProperty, editor.GetObservable(IsVisibleProperty));
        generationSlots.Add((editor, floating, docked));
        return docked;
    }

    private Control BuildGenerationDock()
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        PopulateDockOperations(menu);
        dockOperation = new Button { Flyout = menu };
        dockOperation.Name = "AiDockOperation";
        dockOperation.Content = Ui.Row(5, dockOperationName, Icons.Create(Icons.ChevronDown, 13, Palette.Secondary));
        dockOperation.Classes.Add("flat");
        dockOperation.MinWidth = 0;
        dockOperation.Padding = new Thickness(2, 0);
        dockOperation.HorizontalAlignment = HorizontalAlignment.Left;
        Locale.Tip(dockOperation, "Choose AI operation");
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(dockOperation);
        AddAt(header, Ui.Row(0, DockSlot(aiHeaderReference!), DockSlot(aiEnhance), DockSlot(aiPixelAlignment), DockSlot(aiAdvanced)), 1);
        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,4,Auto") };
        actions.Children.Add(DockSlot(aiEngineCombo!));
        AddAt(actions, DockSlot(aiPriceBadge), 1);
        AddAt(actions, DockSlot(aiFloatingActions!), 3);
        var body = Ui.Column(AiGenerationControls.EditorSpacing, header, DockSlot(aiFloatingPrompt), actions, DockSlot(aiWorkflowLink),
            DockSlot(aiReferenceHost), DockSlot(aiFloatingVariants));
        body.Margin = new Thickness(AiGenerationControls.EditorSpacing);
        var view = new ScrollViewer { Name = "AiGenerationDock", Content = body, Background = Palette.Panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        EnableAiReferenceDrop(view);
        aiFloatingPrompt.TextChanging += (_, _) =>
        {
            if (presentedTask is { } task) settings.RememberAiPrompt(task, aiFloatingPrompt.Text ?? "");
        };
        aiFloatingPrompt.TextChanged += (_, _) =>
        {
            if (editorInDock && EditorTask == AiTaskKind.GenerateImage) RefreshAiFloatingUi();
        };
        return view;
    }

    private void RefreshGenerationDock()
    {
        var useDock = DockEditorVisible;
        var placementChanged = useDock != editorInDock;
        if (placementChanged)
        {
            editorInDock = useDock;
            foreach (var (editor, floating, docked) in generationSlots)
            {
                var source = useDock ? floating : docked;
                var destination = useDock ? docked : floating;
                source.Child = null;
                destination.Child = editor;
            }
            aiEngineCombo!.Width = useDock ? double.NaN : AiGenerationControls.ModelPickerWidth;
            aiEngineCombo.HorizontalAlignment = useDock ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            aiFloatingGenerate!.MinWidth = useDock ? 56 : 64;
        }
        if (useDock && !session!.IsStroking && !session.IsPaintingSelection)
        {
            var automatic = session.Selection != null ? AiTaskKind.GenerativeFill :
                session.Document.AllLayers().Any(layer => layer.IsLive || layer.Pixels is { } pixels && Pixels.HasVisiblePixel(pixels))
                    ? AiTaskKind.ImageEdit : AiTaskKind.GenerateImage;
            if (automatic != automaticDockTask)
            {
                automaticDockTask = automatic;
                manualDockTask = null;
            }
        }
        if (session?.Selection == null || !ReferenceEquals(floatingTaskSession, session))
        {
            manualFloatingTask = null;
            floatingTaskSession = session?.Selection == null ? null : session;
        }
        var task = EditorTask;
        if (presentedTask == task)
        {
            if (placementChanged) RefreshEditorModels();
            return;
        }
        presentedTask = task;
        RefreshPanelReferences();
        aiFloatingPrompt.Text = settings.AiPromptFor(task);
        dockOperationName.Text = DockTaskName(task);
        aiFloatingOperation.Text = Locale.T(task.DisplayName());
        AutomationProperties.SetName(dockOperation, "Choose AI operation · " + dockOperationName.Text);
        aiFloatingPrompt.PlaceholderText = Locale.T(task switch
        {
            AiTaskKind.GenerateImage => "Describe the image you want to create…",
            AiTaskKind.ChangeBackground => "Describe the background, surface and lighting",
            _ => "Describe the edit…"
        });
        RefreshEditorModels();
    }

    private void PopulateDockOperations(MenuFlyout menu)
    {
        foreach (var task in AiGenerationControls.VisibleOperations)
        {
            var item = new MenuItem { Header = DockTaskName(task), ToggleType = MenuItemToggleType.Radio };
            ToolTip.SetShowOnDisabled(item, true);
            // Avalonia's presenter snapshots the items before Opening. Only update their state here.
            menu.Opening += (_, _) =>
            {
                var availability = AiAvailability(task);
                item.IsEnabled = CanRunAi(task);
                item.IsChecked = EditorTask == task;
                Locale.Tip(item, availability.Reason);
            };
            item.Click += (_, _) => ChooseAiOperation(task);
            menu.Items.Add(item);
        }
    }
}
