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
    private AiTaskKind EditorTask => editorInDock ? manualDockTask ?? automaticDockTask : AiTaskKind.GenerativeFill;
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
        menu.Opening += (_, _) => PopulateDockOperations(menu);
        dockOperation = new Button { Flyout = menu };
        dockOperation.Name = "AiDockOperation";
        dockOperation.Content = Ui.Row(5, dockOperationName, Icons.Create(Icons.ChevronDown, 13, Palette.Secondary));
        dockOperation.Classes.Add("flat");
        dockOperation.MinWidth = 0;
        dockOperation.Padding = new Thickness(2, 0);
        dockOperation.HorizontalAlignment = HorizontalAlignment.Left;
        ToolTip.SetTip(dockOperation, "Choose AI operation");
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(dockOperation);
        AddAt(header, Ui.Row(0, DockSlot(aiHeaderReference!), DockSlot(aiEnhance), DockSlot(aiFloatingAlignment!), DockSlot(aiAdvanced)), 1);
        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,6,Auto") };
        actions.Children.Add(DockSlot(aiEngineCombo!));
        AddAt(actions, DockSlot(aiFloatingActions!), 2);
        var body = Ui.Column(AiGenerationControls.EditorSpacing, header, DockSlot(aiFloatingPrompt), actions, DockSlot(aiReferenceHost),
            DockSlot(aiApiCost), DockSlot(aiFloatingStatus), DockSlot(aiFloatingVariants));
        body.Margin = new Thickness(AiGenerationControls.EditorSpacing);
        var view = new ScrollViewer { Name = "AiGenerationDock", Content = body, Background = Palette.Panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        EnableAiReferenceDrop(view);
        aiFloatingPrompt.TextChanged += (_, _) =>
        {
            if (editorInDock && EditorTask == AiTaskKind.GenerateImage) RefreshAiFloatingUi();
        };
        return view;
    }

    private void RefreshGenerationDock()
    {
        var useDock = DockEditorVisible;
        if (useDock != editorInDock)
        {
            editorInDock = useDock;
            foreach (var (editor, floating, docked) in generationSlots)
            {
                var source = useDock ? floating : docked;
                var destination = useDock ? docked : floating;
                source.Child = null;
                destination.Child = editor;
            }
            aiEngineCombo!.Width = useDock ? double.NaN : 141;
            aiEngineCombo.HorizontalAlignment = useDock ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            aiFloatingGenerate!.MinWidth = useDock ? 62 : 64;
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
        var task = EditorTask;
        if (presentedTask == task) return;
        presentedTask = task;
        dockOperationName.Text = DockTaskName(task);
        AutomationProperties.SetName(dockOperation, "Choose AI operation · " + dockOperationName.Text);
        aiFloatingPrompt.PlaceholderText = task == AiTaskKind.GenerateImage ? "Describe the image you want to create…" : "Describe the edit…";
        RefreshEditorModels();
    }

    private void PopulateDockOperations(MenuFlyout menu)
    {
        menu.Items.Clear();
        foreach (var task in new[] { AiTaskKind.GenerateImage, AiTaskKind.ImageEdit, AiTaskKind.GenerativeFill,
                     AiTaskKind.GenerativeExpand, AiTaskKind.RemoveObject })
        {
            var availability = AiAvailability(task);
            var item = new MenuItem { Header = DockTaskName(task), ToggleType = MenuItemToggleType.Radio,
                IsChecked = EditorTask == task, IsEnabled = availability.Available };
            if (availability.Reason != null) ToolTip.SetTip(item, availability.Reason);
            item.Click += (_, _) => { manualDockTask = task; RefreshAiUi(); };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var settingsItem = new MenuItem { Header = "AI Settings…" };
        settingsItem.Click += (_, _) => _ = ShowAiSettings();
        menu.Items.Add(settingsItem);
        if (!dock.Tabbed)
        {
            var collapse = new MenuItem { Header = "Collapse panel" };
            collapse.Click += (_, _) => dock.SetCollapsed(generationSection, true);
            menu.Items.Add(collapse);
        }
    }
}
