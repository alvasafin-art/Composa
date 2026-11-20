using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.Filters;
using Composa.IO;
using Composa.IO.Psd;
using Composa.Model;
using Composa.Rendering;
using Composa.AI;
using SkiaSharp;

namespace Composa.App;

public sealed partial class MainWindow
{
    private readonly List<Shortcut> commands = [];
    /// <summary>Keys that pick tools or act on the canvas without a menu entry; letters also work with Shift held.</summary>
    private readonly List<Shortcut> toolKeys = [];
    private readonly List<(MenuItem Item, Shortcut Command)> menuItems = [];
    private MenuItem? undoItem, redoItem, mergeItem, clipItem;
    private readonly List<(MenuItem Item, Func<ViewOptions, bool> Checked)> viewToggles = [];
    /// <summary>The Window menu's checkboxes, by the title of the dock section each shows or hides.</summary>
    private readonly List<(MenuItem Item, string Section)> panelToggles = [];
    private Guid? optionsLayer;
    private int jpegQuality = 90;
    private MenuItem? recentMenu, tabbedPanelItem;
    private const string FindCommandId = "Find Command";
    private MenuItem? canvasOnlyItem;

    private bool HasDocument => session != null;

    private Menu BuildMenu()
    {
        var menu = new Menu { Background = Palette.Window };
        const KeyModifiers ctrl = KeyModifiers.Control, shift = KeyModifiers.Shift, alt = KeyModifiers.Alt;

        MenuItem Top(string header, params object[] items)
        {
            var top = new MenuItem { Header = header };
            foreach (var item in items) top.Items.Add(item);
            top.SubmenuOpened += (_, _) => RefreshMenuState();
            menu.Items.Add(top);
            return top;
        }
        MenuItem Item(string name, Action run, Key key = Key.None, KeyModifiers modifiers = KeyModifiers.None, Func<bool>? enabled = null, bool needsDocument = true, string? id = null, bool viewOnly = false, string? unavailableReason = null)
        {
            var gesture = key == Key.None ? null : new KeyGesture(key, modifiers);
            var guard = needsDocument ? () => HasDocument && (enabled?.Invoke() ?? true) : enabled;
            var command = new Shortcut(id ?? name.TrimEnd('…'), name.TrimEnd('…'), "Menus", gesture, run, guard, viewOnly: viewOnly) { UnavailableReason = unavailableReason };
            commands.Add(command);
            var item = new MenuItem { Header = name, InputGesture = gesture };
            item.Click += (_, _) => Execute(command);
            command.Item = item;
            menuItems.Add((item, command));
            return item;
        }
        MenuItem Sub(string header, params object[] items)
        {
            var sub = new MenuItem { Header = header };
            foreach (var item in items) sub.Items.Add(item);
            return sub;
        }
        Separator Line() => new();

        Top("_File",
            Item("New Canvas…", () => _ = NewCanvas(), Key.N, ctrl, needsDocument: false),
            Item("Open…", () => _ = Open(), Key.O, ctrl, needsDocument: false),
            recentMenu = Sub("Open Recent"),
            Item("Place Images as Layers…", () => _ = PlaceImages()),
            Item("Place Linked Smart Object…", () => _ = ChooseSmartObjectFile(true)),
            Item("Place Embedded Smart Object…", () => _ = ChooseSmartObjectFile(false)),
            Line(),
            Item("Save", () => _ = Save(session!, false), Key.S, ctrl),
            Item("Save As…", () => _ = Save(session!, true), Key.S, ctrl | shift),
            Item("Save as PSD…", () => _ = Save(session!, true, photoshop: true)),
            Line(),
            Item("Export PNG…", () => _ = Export(ExportFormat.Png), Key.E, ctrl | shift),
            Item("Export JPEG…", () => _ = Export(ExportFormat.Jpeg), Key.S, ctrl | shift | alt),
            Item("Export WebP…", () => _ = Export(ExportFormat.Webp)),
            Item("Export TIFF…", () => _ = Export(ExportFormat.Tiff)),
            Line(),
            Item("Close Project", () => _ = CloseSession(session!), Key.W, ctrl),
            Item("Quit", Close, Key.Q, ctrl, needsDocument: false));

        undoItem = Item("Undo", () => session!.Undo(), Key.Z, ctrl, () => session!.CanUndo);
        redoItem = Item("Redo", () => session!.Redo(), Key.Z, ctrl | shift, () => session!.CanRedo);
        commands.Add(new Shortcut("Redo (Ctrl+Y)", "Redo", "Menus", new KeyGesture(Key.Y, ctrl), () => session!.Redo(), () => HasDocument && session!.CanRedo, hidden: true));
        Top("_Edit", undoItem, redoItem, Line(),
            Item("Cut", () => _ = Cut(), Key.X, ctrl, () => session!.CanCopy, unavailableReason: "Select a layer or channel to copy."),
            Item("Copy", () => _ = Copy(merged: false), Key.C, ctrl, () => session!.CanCopy, unavailableReason: "Select a layer or channel to copy."),
            Item("Copy Merged", () => _ = Copy(merged: true), Key.C, ctrl | shift),
            Item("Paste", () => _ = Paste(), Key.V, ctrl),
            Sub("Paste Special",
                Item("Paste in Place", () => _ = Paste(PasteKind.InPlace), Key.V, ctrl | shift),
                Item("Paste Into", () => _ = Paste(PasteKind.Into), Key.V, ctrl | alt | shift, () => session!.Selection != null, unavailableReason: "Make a selection first.")),
            Line(),
            Item("Fill with Foreground Color", () => session!.Fill(session.Foreground, "Fill"), Key.Back, alt, () => session!.CanFill, unavailableReason: "Select an editable layer or mask to fill."),
            Item("Fill with Background Color", () => session!.Fill(session.Background, "Fill"), Key.Back, ctrl, () => session!.CanFill, unavailableReason: "Select an editable layer or mask to fill."),
            Item("Clear", DeletePressed, Key.Delete),
            Item("Content-Aware Fill", () => _ = RunContentAwareFill(interactive: false), Key.F5, shift, () => session!.Selection != null && session.CanEditPixels && !session.IsEditingMask),
            Item("Content-Aware Fill Workspace…", () => _ = ContentAwareFillWorkspace(), enabled: () => session!.Selection != null && session.CanEditPixels && !session.IsEditingMask),
            Line(),
            Item("Preferences…", () => _ = ShowAiSettings(3), needsDocument: false, viewOnly: true),
            Item("Keyboard Shortcuts…", () => _ = ShowShortcuts(), needsDocument: false, id: "Keyboard Shortcuts (Edit menu)"));

        Top("_Select",
            Item("All", () => session!.SelectAll(), Key.A, ctrl),
            Item("Deselect", () => session!.Deselect(), Key.D, ctrl, () => session!.Selection != null, unavailableReason: "Make a selection first."),
            Item("Inverse", () => session!.InvertSelection(), Key.I, ctrl | shift),
            Line(),
            Item("Layer's Pixels", () => session!.SelectLayerPixels(session.ActiveLayer!), enabled: () => session!.ActiveLayer?.Pixels != null),
            Item("Layer's Mask", () => session!.SelectLayerMask(session.ActiveLayer!), enabled: () => session!.ActiveLayer?.Mask != null),
            Item("Subject", SelectSubject, Key.A, ctrl | alt),
            Item("Color Range…", ShowColorRange, enabled: () => session!.CanSelectColorRange),
            Item("Save Selection…", () => _ = SaveSelectionChannel(), enabled: () => session!.Selection != null, unavailableReason: "Make a selection first."),
            Item("Load Selection…", () => _ = LoadSelectionChannel(), enabled: () => session!.Document.AlphaChannels.Count > 0),
            Item("Select and Mask…", () => _ = RefineSelection(), enabled: () => session!.Selection != null, unavailableReason: "Make a selection first."),
            Line(),
            Item("Expand…", () => _ = ModifySelection("Expand Selection", "Expand by", () => session!.SelectionExpandAmount, 500, v => { session!.SelectionExpandAmount = v; session.ExpandSelection(v); }), enabled: () => session!.Selection != null, unavailableReason: "Make a selection first."),
            Item("Contract…", () => _ = ModifySelection("Contract Selection", "Contract by", () => session!.SelectionContractAmount, 500, v => { session!.SelectionContractAmount = v; session.ContractSelection(v); }), enabled: () => session!.Selection != null, unavailableReason: "Make a selection first."),
            Item("Feather…", () => _ = ModifySelection("Feather Selection", "Feather radius", () => session!.SelectionFeatherAmount, 250, v => { session!.SelectionFeatherAmount = v; session.FeatherSelection(v); }), Key.F6, shift, () => session!.Selection != null, unavailableReason: "Make a selection first."));

        Top("_Image",
            Item("Curves…", () => _ = Adjust(AdjustmentKind.Curves), Key.M, ctrl, () => session!.CanEditPixels, unavailableReason: "Select an unlocked pixel layer or mask."),
            Item("Levels…", () => _ = Adjust(AdjustmentKind.Levels), Key.L, ctrl, () => session!.CanEditPixels, unavailableReason: "Select an unlocked pixel layer or mask."),
            Item("Hue/Saturation…", () => _ = Adjust(AdjustmentKind.HueSaturation), Key.U, ctrl, () => session!.CanEditPixels, unavailableReason: "Select an unlocked pixel layer or mask."),
            Item("Brightness/Contrast…", () => _ = Adjust(AdjustmentKind.BrightnessContrast), enabled: () => session!.CanEditPixels, unavailableReason: "Select an unlocked pixel layer or mask."),
            Item("Exposure…", () => _ = Adjust(AdjustmentKind.Exposure), enabled: () => session!.CanEditPixels, unavailableReason: "Select an unlocked pixel layer or mask."),
            Item("Black & White…", () => _ = Adjust(AdjustmentKind.BlackAndWhite), enabled: () => session!.CanEditPixels, unavailableReason: "Select an unlocked pixel layer or mask."),
            Item("Color Balance…", () => _ = Adjust(AdjustmentKind.ColorBalance), enabled: () => session!.CanEditPixels, unavailableReason: "Select an unlocked pixel layer or mask."),
            Item("Channel Mixer…", () => _ = Adjust(AdjustmentKind.ChannelMixer), enabled: () => session!.CanEditPixels, unavailableReason: "Select an unlocked pixel layer or mask."),
            Item("Color Lookup…", () => _ = Adjust(AdjustmentKind.ColorLookup), enabled: () => session!.CanEditPixels, unavailableReason: "Select an unlocked pixel layer or mask."),
            Item("Gradient Map…", () => _ = Adjust(AdjustmentKind.GradientMap), enabled: () => session!.CanEditPixels, unavailableReason: "Select an unlocked pixel layer or mask."),
            Item("Grain…", () => _ = Adjust(AdjustmentKind.Grain), enabled: () => session!.CanEditPixels, unavailableReason: "Select an unlocked pixel layer or mask."),
            Item("Invert", () => session!.Adjust(new InvertAdjustment()), Key.I, ctrl, () => session!.CanEditPixels, unavailableReason: "Select an unlocked pixel layer or mask."),
            Item("Auto Levels", AutoLevels, Key.L, ctrl | shift, () => session!.CanEditPixels, unavailableReason: "Select an unlocked pixel layer or mask."),
            Line(),
            Item("Canvas Size…", () => _ = CanvasSize(), Key.C, ctrl | alt),
            Item("Image Size…", () => _ = ImageSize(), Key.I, ctrl | alt),
            Sub("RGB Precision",
                Item("8 Bits/Channel", () => session!.ChangeColorDepth(ColorDepth.Eight)),
                Item("16 Bits/Channel", () => session!.ChangeColorDepth(ColorDepth.Sixteen))),
            Item("Assign Color Profile…", () => _ = EditColorProfile(assign: true)),
            Item("Convert Color Profile…", () => _ = EditColorProfile(assign: false)),
            Item("Trim…", () => _ = Trim()),
            Item("Reveal All", () => { if (session!.RevealAll()) canvas.Fit(); }, enabled: () => session!.CanRevealAll, unavailableReason: "There are no pixels outside the canvas."),
            Item("Duplicate", () => AddSession(session!.Duplicate())),
            Line(),
            Item("Rotate Canvas 90° Clockwise", () => { session!.RotateCanvas(true); canvas.Fit(); }),
            Item("Rotate Canvas 90° Counterclockwise", () => { session!.RotateCanvas(false); canvas.Fit(); }),
            Item("Flip Canvas Horizontal", () => session!.FlipCanvas(true)),
            Item("Flip Canvas Vertical", () => session!.FlipCanvas(false)));

        Top("F_ilter", new object[] { Item("Liquify…", () => _ = Liquify(), enabled: () => session!.CanLiquify), Line(), Sub("Add Smart Filter", Enum.GetValues<FilterKind>().Select(kind => (object)Item(FilterSettings.DisplayName(kind) + "…", () => _ = SmartFilter(kind), enabled: () => session!.CanSmartFilter, id: "Smart Filter: " + kind)).ToArray()), Line() }
            .Concat(Enum.GetValues<FilterKind>().Select(kind => (object)Item(FilterSettings.DisplayName(kind) + "…", () => _ = Filter(kind), enabled: () => session!.CanEditPixels, unavailableReason: "Select an unlocked pixel layer or mask."))).ToArray());

        Top("_AI",
            Item("Generate Image…", () => ChooseAiOperation(AiTaskKind.GenerateImage), enabled: () => CanRunAi(AiTaskKind.GenerateImage), needsDocument: false),
            Item("Generative Fill…", () => ChooseAiOperation(AiTaskKind.GenerativeFill), enabled: () => CanRunAi(AiTaskKind.GenerativeFill)),
            Item("Image Edit…", () => ChooseAiOperation(AiTaskKind.ImageEdit), enabled: () => CanRunAi(AiTaskKind.ImageEdit)),
            Item("Remove Object", () => ChooseAiOperation(AiTaskKind.RemoveObject), enabled: () => CanRunAi(AiTaskKind.RemoveObject)),
            Item("Generative Expand", () => ChooseAiOperation(AiTaskKind.GenerativeExpand), enabled: () => CanRunAi(AiTaskKind.GenerativeExpand)),
            Item("Change Background…", () => ChooseAiOperation(AiTaskKind.ChangeBackground), enabled: () => CanRunAi(AiTaskKind.ChangeBackground)),
            Item("Harmonize…", () => ChooseAiOperation(AiTaskKind.Harmonize), enabled: () => CanRunAi(AiTaskKind.Harmonize)),
            Item("Match to Scene", () => ChooseAiOperation(AiTaskKind.MatchToScene), enabled: () => CanRunAi(AiTaskKind.MatchToScene)),
            Item("Relight…", () => ChooseAiOperation(AiTaskKind.Relight), enabled: () => CanRunAi(AiTaskKind.Relight)),
            Item("Upscale…", () => ChooseAiOperation(AiTaskKind.Upscale), enabled: () => CanRunAi(AiTaskKind.Upscale)),
            Item("Object Selection", () => ChooseAiOperation(AiTaskKind.ObjectSelection), enabled: () => CanRunAi(AiTaskKind.ObjectSelection)),
            Line(),
            Sub("Presets", (aiTasks.Presets.Count == 0
                ? [new MenuItem { Header = "No presets installed", IsEnabled = false }]
                : aiTasks.Presets.Select(preset => (object)Item(preset.Name, () => _ = RunAi(preset.Task, preset.Prompt), enabled: () => CanRunAi(preset.Task), needsDocument: preset.Task != AiTaskKind.GenerateImage)).ToArray())),
            Item("ComfyUI Settings…", () => _ = ShowAiSettings(), needsDocument: false),
            Item("Assistant…", ShowAssistant, needsDocument: false));

        mergeItem = Item("Merge Down", () => session!.MergeLayers(), Key.E, ctrl, () => session!.CanMerge);
        clipItem = Item("Create Clipping Mask", () => session!.ToggleClippingMask(session.ActiveLayer!), Key.G, ctrl | alt, () => session!.ActiveLayer is { } l && session.CanClip(l));
        Top("_Layer",
            Item("New Layer", () => session!.AddBlankLayer(), Key.N, ctrl | shift),
            Sub("New Adjustment Layer", Enum.GetValues<AdjustmentKind>().Select(kind => (object)Item(Adjustment.Create(kind).DisplayName + "…", () => _ = NewAdjustmentLayer(kind), id: "New " + Adjustment.Create(kind).DisplayName + " Layer")).ToArray()),
            Item("Edit Adjustment…", () => _ = EditAdjustmentLayer(session!.ActiveLayer!, false), enabled: () => session!.ActiveLayer?.IsAdjustment == true),
            Line(),
            Item("Transform Layer", () => { canvas.ShowTransformControls = true; SelectTool(Tool.Move); }, Key.T, ctrl),
            Item("Warp…", StartWarp, enabled: () => session!.CanWarp),
            Item("Duplicate Layer / Layer via Copy", () => session!.LayerViaCopy(), Key.J, ctrl, () => session!.ActiveLayer != null),
            Item("Rename Layer…", layers.BeginRename, Key.F2, enabled: () => session!.ActiveLayer != null),
            Item("Delete Layer", layers.DeleteLayerOrMask, enabled: () => session!.ActiveLayer != null),
            Line(),
            Item("Add Layer Mask", () => session!.AddMask(session.ActiveLayer!), enabled: () => session!.ActiveLayer is { Mask: null }),
            Item("Link Layer Mask", () => session!.SetMaskLinked(session.ActiveLayer!, true), enabled: () => session!.ActiveLayer is { Mask: not null, MaskLinked: false }),
            Item("Unlink Layer Mask", () => session!.SetMaskLinked(session.ActiveLayer!, false), enabled: () => session!.ActiveLayer is { Mask: not null, MaskLinked: true }),
            Item("Invert Layer Mask", () => { session!.EditingMask = true; session.Adjust(new InvertAdjustment()); }, enabled: () => session!.ActiveLayer?.Mask != null),
            Item("Apply Layer Mask", () => session!.ApplyMask(session.ActiveLayer!), enabled: () => session!.ActiveLayer is { Mask: not null, Pixels: not null }),
            clipItem,
            Line(),
            Item("Group Selected Layers", () => session!.GroupSelectedLayers(), Key.G, ctrl),
            Item("Ungroup", () => session!.Ungroup(session.ActiveLayer!), Key.G, ctrl | shift, () => session!.ActiveLayer?.IsGroup == true),
            Item("Move Layer Up", () => session!.MoveActiveLayer(1), Key.OemCloseBrackets, ctrl),
            Item("Move Layer Down", () => session!.MoveActiveLayer(-1), Key.OemOpenBrackets, ctrl),
            mergeItem,
            Item("Merge Visible", () => session!.MergeVisible(), enabled: () => session!.CanMergeVisible),
            Item("Stamp Visible", () => session!.StampVisible(), Key.E, ctrl | alt | shift),
            Item("Flatten Image", () => session!.FlattenImage()),
            Line(),
            Sub("Layer Effects", Enum.GetValues<LayerEffectKind>().Select(kind => (object)Item(LayerEffects.DisplayName(kind) + "…", () => _ = NewEffect(kind), enabled: () => session!.ActiveLayer?.Pixels != null, id: "Add " + LayerEffects.DisplayName(kind)))
                .Append(Line()).Append(Item("Delete Effect", () => session!.RemoveSelectedEffect(), enabled: () => session!.SelectedEffect != null)).ToArray()),
            Item("Edit Text…", () => BeginTextEdit(session!.ActiveLayer!), enabled: () => session!.ActiveLayer?.Text != null),
            Item("Rasterize Layer", () => session!.RasterizeShape(session.ActiveLayer!), enabled: () => session!.ActiveLayer?.IsLive == true),
            Sub("Smart Objects", Item("Convert to Smart Object", () => session!.ConvertToSmartObject(), enabled: () => session!.SelectedRoots().Count > 0),
            Item("Edit Smart Object Contents…", () => OpenSmartObject(session!.ActiveLayer!), enabled: () => session!.ActiveLayer?.IsSmartObject == true),
            Item("New Smart Object via Copy", () => session!.DuplicateSmartObjectIndependent(session.ActiveLayer!), enabled: () => session!.ActiveLayer?.IsSmartObject == true),
            Item("Replace Smart Object Contents…", () => _ = ChooseSmartObjectFile(false, session!.ActiveLayer!), enabled: () => session!.ActiveLayer?.IsSmartObject == true),
            Item("Relink Smart Object…", () => _ = ChooseSmartObjectFile(true, session!.ActiveLayer!, relink: true), enabled: () => session!.ActiveLayer?.IsSmartObject == true),
            Item("Update Linked Smart Object", () => _ = UpdateLinkedFromUi(session!.ActiveLayer!), enabled: () => session!.ActiveLayer?.SmartObject?.Link != null),
            Item("Embed Linked Smart Object", () => session!.SetSmartObjectLink(session.ActiveLayer!.SmartObject!, null), enabled: () => session!.ActiveLayer?.SmartObject?.Link != null),
            Item("Linked Source Info…", () => _ = LinkedSourceInfo(session!.ActiveLayer!), enabled: () => session!.ActiveLayer?.SmartObject?.Link != null)),
            Item("Rotate Layer 90° Clockwise", () => session!.RotateLayers(90)),
            Item("Rotate Layer 90° Counterclockwise", () => session!.RotateLayers(-90)),
            Item("Rotate Layer 180°", () => session!.RotateLayers(180)),
            Item("Flip Layer Horizontal", () => session!.FlipLayers(true)),
            Item("Flip Layer Vertical", () => session!.FlipLayers(false)));

        var grid = new MenuItem { Header = "Pixel Grid (800% and above)", ToggleType = MenuItemToggleType.CheckBox, IsChecked = canvas.ShowPixelGrid };
        grid.Click += (_, _) => { canvas.ShowPixelGrid = !canvas.ShowPixelGrid; grid.IsChecked = canvas.ShowPixelGrid; canvas.InvalidateCanvas(); RememberToolSettings(); };
        // View options are flags on the session, so a checkmark follows the current tab.
        MenuItem ViewToggle(string name, Func<ViewOptions, bool> get, Func<ViewOptions, ViewOptions> flip, Key key = Key.None, KeyModifiers modifiers = KeyModifiers.None, string? id = null)
        {
            var item = Item(name, () =>
            {
                var rulersShown = session!.View.ShowRulers;
                session.View = flip(session.View);
                canvas.ViewOptionsChanged(rulersShown);
                RememberToolSettings();
            }, key, modifiers, id: id, viewOnly: true);
            item.ToggleType = MenuItemToggleType.CheckBox;
            viewToggles.Add((item, get));
            return item;
        }
        canvasOnlyItem = Item("Canvas Only", () => SetCanvasOnly(!settings.CanvasOnly), Key.F6, needsDocument: false, viewOnly: true);
        canvasOnlyItem.ToggleType = MenuItemToggleType.CheckBox; canvasOnlyItem.IsChecked = settings.CanvasOnly;
        Top("_View",
            Item("Find Command…", () => _ = FindCommand(), Key.P, ctrl | shift, needsDocument: false, viewOnly: true),
            canvasOnlyItem,
            Line(),
            Item("Fit Canvas", canvas.Fit, Key.D0, ctrl, viewOnly: true),
            Item("Actual Pixels", () => canvas.ZoomTo(1), Key.D1, ctrl, viewOnly: true),
            Item("Zoom In", canvas.ZoomIn, Key.OemPlus, ctrl, viewOnly: true),
            Item("Zoom Out", canvas.ZoomOut, Key.OemMinus, ctrl, viewOnly: true),
            Line(), grid,
            Item("Show Transform Controls", () => { canvas.ShowTransformControls = !canvas.ShowTransformControls; canvas.InvalidateCanvas(); RebuildOptions(); RememberToolSettings(); }, Key.H, ctrl, viewOnly: true),
            Line(),
            ViewToggle("Rulers", v => v.ShowRulers, v => v with { ShowRulers = !v.ShowRulers }, Key.R, ctrl),
            Sub("Show",
                ViewToggle("Grid", v => v.ShowGrid, v => v with { ShowGrid = !v.ShowGrid }, Key.OemQuotes, ctrl, "Show Grid"),
                ViewToggle("Guides", v => v.ShowGuides, v => v with { ShowGuides = !v.ShowGuides }, Key.OemSemicolon, ctrl, "Show Guides")),
            Item("Grid Settings…", () => _ = ShowGridSettings()),
            Item("Display Color…", () => _ = EditDisplayColor(), needsDocument: false, viewOnly: true),
            Line(),
            ViewToggle("Snap", v => v.Snap, v => v with { Snap = !v.Snap }, Key.OemSemicolon, ctrl | shift),
            Sub("Snap To",
                ViewToggle("Guides", v => v.SnapToGuides, v => v with { SnapToGuides = !v.SnapToGuides }, id: "Snap To Guides"),
                ViewToggle("Grid", v => v.SnapToGrid, v => v with { SnapToGrid = !v.SnapToGrid }, id: "Snap To Grid"),
                ViewToggle("Layers", v => v.SnapToLayers, v => v with { SnapToLayers = !v.SnapToLayers }, id: "Snap To Layers"),
                ViewToggle("Document Bounds", v => v.SnapToDocumentBounds, v => v with { SnapToDocumentBounds = !v.SnapToDocumentBounds }, id: "Snap To Document Bounds")),
            Line(),
            ViewToggle("Lock Guides", v => v.LockGuides, v => v with { LockGuides = !v.LockGuides }, Key.OemSemicolon, ctrl | alt),
            Item("Clear Guides", () => session!.ClearGuides(), enabled: () => session!.CanClearGuides, unavailableReason: "This document has no guides to clear."));
        // '+' is Shift and '=' on most keyboards, so Zoom In answers with Shift held too.
        commands.Add(new Shortcut("Zoom In (with Shift)", "Zoom In", "Menus", new KeyGesture(Key.OemPlus, ctrl | shift), canvas.ZoomIn, () => HasDocument, hidden: true, viewOnly: true));
        commands.Add(new Shortcut("Zoom In (keypad)", "Zoom In", "Menus", new KeyGesture(Key.Add, ctrl), canvas.ZoomIn, () => HasDocument, hidden: true, viewOnly: true));
        commands.Add(new Shortcut("Zoom Out (keypad)", "Zoom Out", "Menus", new KeyGesture(Key.Subtract, ctrl), canvas.ZoomOut, () => HasDocument, hidden: true, viewOnly: true));

        // A package manager owns updates for a .deb or .rpm, so there is nothing to switch on there.
        var autoUpdates = new MenuItem
        {
            Header = "Check for Updates Automatically",
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = settings.CheckForUpdates,
            IsEnabled = UpdateCheck.Channel == UpdateChannel.GitHub
        };
        autoUpdates.Click += (_, _) =>
        {
            settings.CheckForUpdates = !settings.CheckForUpdates;
            autoUpdates.IsChecked = settings.CheckForUpdates;
            settings.Save();
        };

        // The MCP server, through which an AI agent drives the editor. Off until switched on, and remembered.
        var aiControl = new MenuItem { Header = "Allow AI Control", ToggleType = MenuItemToggleType.CheckBox, IsChecked = settings.AllowAiControl };
        aiControl.Click += async (_, _) => { await SetAiControl(!AiControl); aiControl.IsChecked = settings.AllowAiControl; };

        MenuItem PanelToggle(string title)
        {
            var item = Item(title, () => dock.SetVisible(dock.Section(title), !dock.Section(title).State.Visible), needsDocument: false, viewOnly: true);
            item.ToggleType = MenuItemToggleType.CheckBox;
            panelToggles.Add((item, title));
            return item;
        }
        scriptsMenu = Top("_Scripts"); pluginsMenu = Top("_Plugins"); ReloadAutomation();
        tabbedPanelItem = Item("Group panels in tabs", () =>
        {
            dock.SetTabbed(!dock.Tabbed);
            settings.TabbedPanels = dock.Tabbed; settings.Save();
        }, needsDocument: false, viewOnly: true);
        tabbedPanelItem.ToggleType = MenuItemToggleType.CheckBox;
        Top("_Window", PanelToggle("Navigator"), PanelToggle("Channels"), PanelToggle("AI Generation"), PanelToggle("History"), Line(), tabbedPanelItem,
            Item("Workspaces…", () => _ = new WorkspacesWindow(settings, CaptureWorkspace, ApplyWorkspace).ShowDialog(this), needsDocument: false, viewOnly: true));

        Top("_Help", Item("Keyboard Shortcuts…", () => _ = ShowShortcuts(), Key.F1, needsDocument: false),
            Item("Check for Updates…", () => _ = CheckForUpdatesNow(), needsDocument: false),
            autoUpdates,
            Line(),
            aiControl,
            Line(),
            Item("About Composa", () => _ = Prompts.Alert(this, "About Composa",
            $"Composa {AppInfo.Version}\n\nA layer-based image editor for compositing and retouching, built with .NET, Avalonia and Skia. " +
            "It is a from-scratch implementation of the open-source macOS app Compositor by Robbie Tilton (MIT license)."), needsDocument: false));
        BuildToolKeys();
        ApplyShortcutOverrides();
        return menu;
    }

    // The keys of tools that come in groups, named once for the table and for the groups the tool rail opens. Each is also the
    // id a rebound key is saved under, so the spelling must not change.
    private const string MarqueeKey = "Marquee tool (again switches Rectangle and Ellipse)", LassoKey = "Lasso tool (again switches Freehand and Polygonal)",
        MagicKey = "Magic tool", BrushKey = "Brush tool", EraserKey = "Eraser", SmearKey = "Smear tool (again switches its mode)", ShapeKey = "Shape tool (again switches the shape)";

    /// <summary>The keys that pick tools and act on the canvas, kept as a table so the shortcuts window can rebind them.</summary>
    private void BuildToolKeys()
    {
        void Key(string title, Avalonia.Input.Key key, Action run, KeyModifiers modifiers = KeyModifiers.None, bool hidden = false, string? label = null) =>
            toolKeys.Add(new Shortcut(title, label ?? title, "Tools and Canvas", new KeyGesture(key, modifiers), run, hidden: hidden));
        Key("Move tool", Avalonia.Input.Key.V, () => SelectTool(Tool.Move));
        Key("Pen tool", Avalonia.Input.Key.P, () => SelectTool(Tool.Pen));
        Key(MarqueeKey, Avalonia.Input.Key.M, () => SelectTool(Tool.Marquee), label: "Marquee tool");
        Key(LassoKey, Avalonia.Input.Key.L, () => SelectTool(selectionTool ?? Tool.Lasso), label: "Lasso group");
        Key(MagicKey, Avalonia.Input.Key.W, () => SelectTool(selectionTool ?? Tool.Wand));
        Key("Selection Brush", Avalonia.Input.Key.Q, () => SelectTool(selectionTool ?? Tool.SelectionBrush));
        Key("Crop tool", Avalonia.Input.Key.C, () => SelectTool(Tool.Crop));
        Key(BrushKey, Avalonia.Input.Key.B, () => SelectTool(Tool.Brush));
        Key(EraserKey, Avalonia.Input.Key.E, () => SelectTool(Tool.Eraser));
        Key("Spot Healing Brush", Avalonia.Input.Key.J, () => SelectTool(Tool.SpotHealing));
        Key("Clone Stamp", Avalonia.Input.Key.S, () => SelectTool(stampGroupTool == Tool.CloneStamp ? StampPaintingTool : stampGroupTool));
        Key(SmearKey, Avalonia.Input.Key.R, () => SelectTool(Tool.Smear), label: "Smear tool");
        Key("Gradient tool", Avalonia.Input.Key.G, () => SelectTool(gradientShowsBucket ? Tool.Bucket : Tool.Gradient));
        Key(ShapeKey, Avalonia.Input.Key.U, () => SelectTool(Tool.Shape), label: "Shape tool");
        Key("Next shape", Avalonia.Input.Key.U, () => { session!.ShapeKind = (ShapeKind)(((int)session.ShapeKind + 1) % Enum.GetValues<ShapeKind>().Length); SelectTool(Tool.Shape); }, KeyModifiers.Shift);
        Key("Type tool", Avalonia.Input.Key.T, () => SelectTool(Tool.Text));
        Key("Eyedropper tool", Avalonia.Input.Key.I, () => SelectTool(Tool.Eyedropper));
        Key("Hand tool", Avalonia.Input.Key.H, () => SelectTool(Tool.Hand));
        Key("Zoom tool", Avalonia.Input.Key.Z, () => SelectTool(Tool.Zoom));
        Key("Cycle tool mode", Avalonia.Input.Key.Tab, () => { session!.CycleToolMode(); SelectTool(session.Tool); });
        Key("Swap foreground and background", Avalonia.Input.Key.X, () => { session!.SwapColors(); UpdateColors(); });
        Key("Reset colors to black and white", Avalonia.Input.Key.D, () => { session!.ResetColors(); UpdateColors(); });
        Key("Delete selection, layer or effect", Avalonia.Input.Key.Back, DeletePressed);
        Key("Toggle painting on the mask", Avalonia.Input.Key.OemBackslash, ToggleMaskEditing);
        Key("Toggle painting on the mask (pipe)", Avalonia.Input.Key.OemPipe, ToggleMaskEditing, hidden: true);
    }

    private void ToggleMaskEditing()
    {
        if (session?.ActiveLayer?.Mask == null) return;
        session.EditingMask = !session.EditingMask;
        session.NotifyLayersChanged();
    }

    /// <summary>Every shortcut the window lists, menus first.</summary>
    private IReadOnlyList<Shortcut> AllShortcuts => commands.Concat(toolKeys).ToList();

    /// <summary>Puts the keys saved in the settings on their commands.</summary>
    private void ApplyShortcutOverrides()
    {
        foreach (var shortcut in AllShortcuts)
        {
            if (!settings.Shortcuts.TryGetValue(shortcut.Id, out var stored)) continue;
            KeyGesture? gesture = null;
            if (!string.IsNullOrEmpty(stored))
            {
                try { gesture = KeyGesture.Parse(stored); }
                catch (Exception) { continue; } // A damaged entry keeps the default.
            }
            shortcut.Gesture = gesture;
            if (shortcut.Item != null) shortcut.Item.InputGesture = gesture;
        }
    }

    private async Task ShowShortcuts()
    {
        if (await ShortcutsDialog.Edit(this, AllShortcuts) is not { } chosen) return;
        var inactive = settings.Shortcuts.Where(pair => !AllShortcuts.Any(shortcut => shortcut.Id == pair.Key)).ToArray();
        settings.Shortcuts.Clear();
        foreach (var (id, gesture) in inactive) settings.Shortcuts[id] = gesture;
        foreach (var shortcut in AllShortcuts)
        {
            if (!chosen.TryGetValue(shortcut.Id, out var gesture)) continue;
            shortcut.Gesture = gesture;
            if (shortcut.Item != null) shortcut.Item.InputGesture = gesture;
            if (!Equals(gesture, shortcut.Default)) settings.Shortcuts[shortcut.Id] = gesture?.ToString() ?? "";
        }
        settings.Save();
    }

    private void RefreshMenuState()
    {
        if (recentMenu != null)
        {
            recentMenu.Items.Clear();
            foreach (var path in settings.RecentFiles.Where(p => File.Exists(p) || Directory.Exists(p)))
            {
                var item = new MenuItem { Header = path.Replace("_", "__") };
                item.Click += (_, _) => _ = OpenPaths([path]);
                recentMenu.Items.Add(item);
            }
            recentMenu.IsEnabled = recentMenu.Items.Count > 0;
        }
        foreach (var (item, command) in menuItems) item.IsEnabled = command.Enabled?.Invoke() ?? true;
        foreach (var (item, isChecked) in viewToggles) item.IsChecked = session != null && isChecked(session.View);
        if (tabbedPanelItem != null) tabbedPanelItem.IsChecked = dock.Tabbed;
        foreach (var (item, section) in panelToggles) item.IsChecked = dock.Section(section).State.Visible;
        if (canvasOnlyItem != null) canvasOnlyItem.IsChecked = settings.CanvasOnly;
        if (session == null) return;
        undoItem!.Header = session.History.CanUndo ? $"Undo {session.History.UndoName}" : "Undo";
        redoItem!.Header = session.History.CanRedo ? $"Redo {session.History.RedoName}" : "Redo";
        mergeItem!.Header = session.MergeTitle;
        clipItem!.Header = session.ActiveLayer?.Clipped == true ? "Release Clipping Mask" : "Create Clipping Mask";
    }

    private void Execute(Shortcut command)
    {
        if (Content is Control { IsEnabled: false }) return;
        if (command.Enabled?.Invoke() == false || IsDragging) return;
        problem = note = null;
        try { if (!command.ViewOnly) { session?.CommitChannelControlEdit(); if (session != null && !CommitWarp(session)) return; } command.Run(); }
        catch (Exception error) { _ = Prompts.Alert(this, command.Title, error.Message); }
        UpdateStatus();
    }

    /// <summary>Runs a slow edit with the wait cursor showing.</summary>
    private void Busy(Action action)
    {
        var previous = Cursor;
        Cursor = new Cursor(StandardCursorType.Wait);
        try { action(); }
        finally { Cursor = previous; }
    }

    private async Task<T> Busy<T>(Func<Task<T>> action)
    {
        var previous = Cursor;
        Cursor = new Cursor(StandardCursorType.Wait);
        try { return await action(); }
        finally { Cursor = previous; }
    }

    private void OnSessionLayersChanged()
    {
        RefreshAiUi();
        UpdateColors();
        if (session?.Tool is not (Tool.Move or Tool.Shape or Tool.Pen or Tool.Text)) return;
        if (session.ActiveLayerIdOrNull() != optionsLayer || session.ActiveLayer?.Shape?.Kind != optionsShapeKind || (session.WarpEdit != null) != optionsHaveWarp || session.IsEditingText != optionsEditingText) RebuildOptions();
        else refreshOptions?.Invoke();
        optionsLayer = session.ActiveLayerIdOrNull();
    }

    // ---- Keyboard -----------------------------------------------------------------------------------------------

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (Content is Control { IsEnabled: false })
        { if (e.Key == Key.Escape) automationCancellation?.Cancel(); e.Handled = true; return; }
        var focused = FocusManager?.GetFocusedElement();
        if (SwallowAlt(e)) return;
        if (channelInspectorDragging)
        {
            if (e.Key == Key.Escape)
            {
                channelInspectorDragging = false; var pointer = channelInspectorPointer; channelInspectorPointer = null;
                pointer?.Capture(null); session?.CancelTransform();
            }
            e.Handled = true; return;
        }
        if (!settings.CanvasOnly && (aiFloatingHost.IsVisible || DockEditorVisible && (focused is not TextBox || focused == aiFloatingPrompt)) && e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            _ = PasteAiReferenceOrText(focused as TextBox);
            return;
        }
        if (focused is TextBox)
        {
            // Text fields keep their own editing keys; Enter or Escape hands the keyboard back to the canvas.
            if (e.Key is Key.Enter or Key.Escape && session != null) Avalonia.Threading.Dispatcher.UIThread.Post(() => canvas.Focus());
            return;
        }
        if (focused is Control control && control.FindAncestorOfType<MenuItem>() != null) return;
        // A slider field that was just dragged keeps the keys that step or jump its value; letters still reach the tool shortcuts.
        if (focused is Controls.SliderField && e.Key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End) return;
        if (focused is ComboBox && session?.WarpEdit != null && e.Key is Key.Up or Key.Down or Key.Left or Key.Right) return;
        if (navigator.OwnsPanKeys(focused) && e.Key is Key.Up or Key.Down or Key.Left or Key.Right) return;

        // Searching does not finish a canvas text edit or a retained transform.
        var viewCommand = commands.FirstOrDefault(c => c.ViewOnly && c.Matches(e));
        if (viewCommand != null) { Execute(viewCommand); e.Handled = true; return; }

        if (canvas.HandleKeyDown(e)) { e.Handled = true; return; }
        // While text is being typed, letters are text, not tool keys or shortcuts; the key stays unhandled so the
        // platform still delivers the character.
        if (session?.IsEditingText == true) return;
        if (canvas.IsDragging) { e.Handled = true; return; }

        var gesture = commands.FirstOrDefault(c => c.Matches(e));
        if (gesture != null) { Execute(gesture); e.Handled = true; return; }

        if (session == null) return;
        // Tool letters also work with Shift held (Shift+U has its own meaning, so an exact match wins).
        var tool = toolKeys.FirstOrDefault(c => c.Matches(e))
            ?? (e.KeyModifiers == KeyModifiers.Shift ? toolKeys.FirstOrDefault(c => c.Gesture is { KeyModifiers: KeyModifiers.None } g && g.Key == e.Key) : null);
        if (tool == null) return;
        e.Handled = true;
        problem = note = null;
        tool.Run();
        UpdateStatus();
    }

    /// <summary>
    /// Alt is a tool modifier here (subtract from selection, clone source, pick color). Left alone, a bare Alt press
    /// would hand keyboard focus to the menu bar and tool shortcuts would stop working after every Alt-click.
    /// </summary>
    private bool SwallowAlt(KeyEventArgs e)
    {
        if (session == null || e.Key is not (Key.LeftAlt or Key.RightAlt)) return false;
        e.Handled = true;
        canvas.InvalidateCanvas();
        return true;
    }

    private void DeletePressed()
    {
        if (session == null) return;
        if (session.SelectedEffect != null) { session.RemoveSelectedEffect(); return; }
        if (session.Selection != null && session.CanEditPixels) session.ClearSelection();
        else if (session.IsEditingAlphaChannel || session.IsEditingComponent) session.Fill(SKColors.Black, "Clear Channel");
        else if (session.Selection == null) layers.DeleteLayerOrMask();
    }

    // ---- Files --------------------------------------------------------------------------------------------------

    private static readonly FilePickerFileType ProjectType = new("Composa project") { Patterns = ["*" + ProjectFile.Extension] };
    private static readonly FilePickerFileType PhotoshopType = new("Photoshop document") { Patterns = ["*.psd"] };
    private static readonly string[] ImageExtensions = [.. ImageFiles.ImportExtensions, .. RawImporter.Extensions];
    private static readonly FilePickerFileType ImageType = new("Images") { Patterns = ImageExtensions.Select(e => "*" + e).ToArray() };
    private static readonly FilePickerFileType RawType = new("Camera RAW") { Patterns = RawImporter.Extensions.Select(e => "*" + e).ToArray() };
    private static readonly FilePickerFileType AnyOpenable = new("Projects and images") { Patterns = ImageExtensions.Select(e => "*" + e).Append("*" + ProjectFile.Extension).ToArray() };

    private async Task NewCanvas()
    {
        var result = await CanvasDialogs.NewCanvas(this, session?.Background ?? SKColors.White, await ClipboardImageSize());
        if (result == null) return;
        var created = EditorSession.NewCanvas(result.Width, result.Height, result.Background, result.Depth);
        created.Document.Resolution = result.Resolution;
        AddSession(created);
    }

    private async Task Open()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open", AllowMultiple = true, FileTypeFilter = [AnyOpenable, ProjectType, ImageType, RawType] });
        await OpenPaths(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    /// <summary>Opens projects in tabs and images as new documents.</summary>
    public async Task OpenPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try { await OpenPath(path); }
            catch (Exception error) { _ = Prompts.Alert(this, "Couldn't open " + Path.GetFileName(path), error.Message); }
        }
    }

    /// <summary>
    /// Opens one file: a project in a tab, an image as a new document, or the tab it is already open in. Null means the
    /// person declined a dialog on the way; anything wrong with the file is thrown, so the caller decides who hears it.
    /// </summary>
    private static readonly StringComparer FileNames = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly Dictionary<string, Task<EditorSession?>> opening = new(FileNames);
    private EditorSession? DocumentAt(string path) => sessions.FirstOrDefault(s => s.FilePath != null && FileNames.Equals(Path.GetFullPath(s.FilePath), path));

    public async Task<EditorSession?> OpenPath(string path)
    {
        path = Path.GetFullPath(path);
        if (DocumentAt(path) is { } open) { SetSession(open); return open; }
        if (opening.TryGetValue(path, out var pending)) return await pending;
        ShowNote("Opening " + Path.GetFileName(path) + "…");
        var load = LoadDocument(path);
        opening[path] = load;
        try
        {
            var result = await load;
            if (result != null) ShowNote("Opened " + Path.GetFileName(path));
            return result;
        }
        finally
        {
            opening.Remove(path);
            if (opening.Count == 0 && note == "Opening " + Path.GetFileName(path) + "…") { note = null; UpdateStatus(); }
        }
    }

    private async Task<EditorSession?> LoadDocument(string path)
    {
        EditorSession opened;
        if (PsdImport.IsPsd(path))
        {
            // Photoshop files open as unsaved documents; what had to be converted is shown before anything is applied.
            if (await ImportPhotoshop(path, DocumentLimits.DocumentPixelBudget) is not { } import) return null;
            AddSession(opened = EditorSession.OpenPhotoshop(import, Path.GetFileNameWithoutExtension(path)));
            if (import.Conversions.Count == 0 && Path.GetExtension(path).Equals(PsdExport.Extension, StringComparison.OrdinalIgnoreCase)) opened.MarkSaved(path);
        }
        else if (Path.GetExtension(path).Equals(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase))
        {
            opened = new EditorSession(await Task.Run(() => ProjectFile.Load(path)));
            opened.MarkSaved(path);
            AddSession(opened);
        }
        else
        {
            var pixels = RawImporter.IsRaw(path) ? await DevelopRaw(path)
                : SvgImporter.IsSvg(path) ? await Busy(() => Task.Run(() => SvgImporter.Render(path))) // At the size the file declares.
                : await Task.Run(() => ImageFiles.Load(path, preserveProfile: true));
            if (pixels == null) return null;
            var resolution = await Task.Run(() => ImageResolution.Read(path));
            var document = new Document(pixels.Width, pixels.Height) { Resolution = resolution, Depth = Pixels.DepthOf(pixels), Profile = Pixels.ProfileOf(pixels) };
            var layer = Layer.Raster(Path.GetFileNameWithoutExtension(path), pixels);
            document.Layers.Add(layer);
            document.SetActive(layer.Id);
            AddSession(opened = new EditorSession(document) { SuggestedName = Path.GetFileNameWithoutExtension(path) });
        }
        if (Settings.Persist) _ = RecentPreviews.Remember(path, opened.Document.Clone());
        if (opened.FilePath != null) RecordFileVersion(opened, path);
        settings.AddRecent(Path.GetFullPath(path));
        return opened;
    }

    private async Task PlaceImages()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Place Images as Layers", AllowMultiple = true, FileTypeFilter = [ImageType] });
        await PlacePaths(files.Select(f => f.TryGetLocalPath()).OfType<string>(), null);
    }

    /// <summary>Adds images (and Photoshop files, inside a folder each) to the current document as layers, centered on <paramref name="at"/> when given.</summary>
    public async Task PlacePaths(IEnumerable<string> paths, SKPoint? at)
    {
        if (session is not { } target) return;
        foreach (var path in paths)
        {
            if (session != target) return;
            try
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (PsdImport.IsPsd(path))
                {
                    // Into an existing document, a Photoshop file's layers arrive inside a folder named after it.
                    // The file's layers join what the document already holds, so they get what is left of its budget.
                    if (await ImportPhotoshop(path, DocumentLimits.DocumentPixelBudget - session.Document.RasterPixels()) is not { } import) continue;
                    if (target != session) { import.Discard(); continue; }
                    session.PlacePhotoshop(import, name, at);
                }
                else if (RawImporter.IsRaw(path))
                {
                    if (await DevelopRaw(path) is not { } developed) continue;
                    if (target != session) { developed.Dispose(); continue; }
                    session.AddImageLayer(name, developed, at);
                }
                else if (SvgImporter.IsSvg(path))
                {
                    // Fitted to the canvas as it is drawn, so a small icon comes in sharp rather than enlarged from a few pixels.
                    var canvas = new SKSizeI(target.Document.Width, target.Document.Height);
                    var budget = DocumentLimits.DocumentPixelBudget - target.Document.RasterPixels();
                    var fitted = await Busy(() => Task.Run(() => SvgImporter.Render(path, canvas, budget)));
                    if (target != session) { fitted.Dispose(); continue; }
                    session.AddImageLayer(name, fitted, at);
                }
                else
                {
                    var pixels = await Task.Run(() => ImageFiles.Load(path, preserveProfile: true));
                    if (session != target) { pixels.Dispose(); return; }
                    target.AddImageLayer(name, pixels, at);
                }
            }
            catch (Exception error) { _ = Prompts.Alert(this, "Import couldn't finish", error.Message); }
        }
        SelectTool(Tool.Move);
    }

    /// <summary>
    /// Decodes a camera RAW file off the UI thread, puts the develop sheet up and develops the full frame with what was
    /// chosen. Null means the import was cancelled.
    /// </summary>
    private async Task<SKBitmap?> DevelopRaw(string path)
    {
        var raw = await Busy(() => Task.Run(() => RawImporter.Decode(path)));
        if (await RawDevelopDialog.Show(this, Path.GetFileName(path), raw) is not { } settings) return null;
        return await Busy(() => Task.Run(() => raw.Develop(settings, depth: settings.OutputDepth)));
    }

    /// <summary>Reads a Photoshop file and, when anything has to be converted, asks before going on. Null means the user declined.</summary>
    private async Task<PsdImport?> ImportPhotoshop(string path, long pixelBudget)
    {
        var import = await Task.Run(() => PsdImport.Load(path, pixelBudget, preserveProfile: true));
        if (import.Conversions.Count == 0) return import;
        var merged = false;
        if (await PsdConversionDialog.Confirm(this, Path.GetFileName(path), import.Conversions, chooseMerged: value => merged = value))
        {
            if (!merged) return import;
            import.Discard();
            return await Task.Run(() => PsdImport.LoadAppearance(path, pixelBudget, preserveProfile: true));
        }
        import.Discard();
        return null;
    }

    private static List<string> DroppedPaths(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? [];

    private void OnTabDrop(object? sender, DragEventArgs e)
    {
        var paths = DroppedPaths(e);
        if (paths.Count == 0) return;
        e.Handled = true; // Do not also place the files in the active document as this event bubbles.
        _ = OpenPaths(paths);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.Handled) return;
        var paths = DroppedPaths(e);
        if (paths.Count == 0) return;
        e.Handled = true;
        if (session == null || paths.Any(p => Path.GetExtension(p).Equals(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase)))
            _ = OpenPaths(paths);
        else
        {
            var position = e.GetPosition(canvas);
            var inside = position.X >= 0 && position.Y >= 0 && position.X <= canvas.Bounds.Width && position.Y <= canvas.Bounds.Height;
            _ = PlacePaths(paths, inside ? canvas.ToDocument(position) : null);
        }
    }

    private async Task<bool> Save(EditorSession target, bool saveAs, bool photoshop = false)
    {
        if (IsDragging) return false;
        target.CommitChannelControlEdit();
        if (!CommitWarp(target)) return false;
        if (!saveAs && embeddedTabs.ContainsKey(target))
        {
            try { return await SaveSmartObjectContents(target); }
            catch (Exception updateError) { await Prompts.Alert(this, "Couldn't update smart object", updateError.Message); return false; }
        }
        var path = target.FilePath;
        if (!saveAs && path != null && ChangedExternally(target, Path.GetFullPath(path)))
        {
            if (!await Prompts.Confirm(this, "File changed on disk", "Save your edits under a different name to keep both versions.", "Save a copy…")) return false;
            saveAs = true;
        }
        if (saveAs || path == null)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = photoshop ? "Save as PSD" : "Save Document", SuggestedFileName = target.Title + (photoshop ? PsdExport.Extension : ProjectFile.Extension),
                DefaultExtension = (photoshop ? PsdExport.Extension : ProjectFile.Extension).TrimStart('.'),
                FileTypeChoices = photoshop ? [PhotoshopType] : [ProjectType, PhotoshopType]
            });
            path = file?.TryGetLocalPath();
            if (path == null) return false;
            if (!path.EndsWith(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase) && !path.EndsWith(PsdExport.Extension, StringComparison.OrdinalIgnoreCase))
                path += photoshop ? PsdExport.Extension : ProjectFile.Extension;
        }
        var copy = false;
        if (Path.GetExtension(path).Equals(PsdExport.Extension, StringComparison.OrdinalIgnoreCase))
        {
            var conversions = PsdExport.Conversions(target.Document);
            if (conversions.Count > 0 && !await PsdConversionDialog.Confirm(this, Path.GetFileName(path), conversions, exporting: true, saveCopy: () => copy = true)) return false;
        }
        if (await SaveTo(target, path, copy) is not { } error) return true;
        await Prompts.Alert(this, "Couldn't save", error.Message);
        return false;
    }

    /// <summary>
    /// Saves the document to a project file, as Ctrl+S does once the path is known, and returns what went wrong or
    /// null. Another save of this document still writing finishes first; this one then saves whatever changed since.
    /// </summary>
    private readonly Dictionary<EditorSession, (string Path, long Size, DateTime Modified)> fileVersions = [];
    private void RecordFileVersion(EditorSession target, string path)
    {
        var info = new FileInfo(path);
        if (info.Exists) fileVersions[target] = (path, info.Length, info.LastWriteTimeUtc);
    }
    private bool ChangedExternally(EditorSession target, string path)
    {
        if (!fileVersions.TryGetValue(target, out var previous) || !FileNames.Equals(previous.Path, path)) return false;
        var info = new FileInfo(path);
        return !info.Exists || info.Length != previous.Size || info.LastWriteTimeUtc != previous.Modified;
    }

    private async Task WaitForSave(EditorSession target)
    {
        while (saving.TryGetValue(target, out var entry))
        {
            await entry.Task;
            if (saving.TryGetValue(target, out var current) && current.Task == entry.Task) saving.Remove(target);
        }
    }

    public Task<Exception?> SaveTo(EditorSession target, string path) => SaveTo(target, path, copy: false);
    private async Task<Exception?> SaveTo(EditorSession target, string path, bool copy)
    {
        path = Path.GetFullPath(path);
        await WaitForSave(target);
        if (DocumentAt(path) is { } owner && owner != target || saving.Any(pair => pair.Key != target && FileNames.Equals(pair.Value.Path, path)))
            return new IOException("This file is open in another tab. Choose a different filename.");
        if (ChangedExternally(target, path)) return new IOException("The file on disk changed. Use Save As and choose a different filename.");
        if (copy && target.FilePath != null && FileNames.Equals(Path.GetFullPath(target.FilePath), path))
            return new IOException("Choose a different filename for the PSD copy.");
        var task = Write(target, path, copy);
        saving[target] = (path, task);
        UpdateStatus();
        try { return await task; }
        finally
        {
            if (saving.TryGetValue(target, out var current) && current.Task == task) saving.Remove(target);
            UpdateStatus();
        }
    }

    /// <summary>The saves still writing, by document. Close and quit wait for them, and the status bar names them.</summary>
    private readonly Dictionary<EditorSession, (string Path, Task<Exception?> Task)> saving = [];

    /// <summary>True while a save of the document is still being written.</summary>
    public bool IsSaving(EditorSession target) => saving.ContainsKey(target);

    /// <summary>
    /// Writes the document as it is now, off the UI thread, so the tools stay usable while a large project encodes.
    /// Committed bitmaps are immutable, so the snapshot can be read while editing goes on; only that snapshot counts as
    /// saved, and an edit made meanwhile leaves the document modified. The task never faults: close and quit await it.
    /// </summary>
    private async Task<Exception?> Write(EditorSession target, string path, bool copy)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase) && !extension.Equals(PsdExport.Extension, StringComparison.OrdinalIgnoreCase))
            return new IOException("Save a .cmps project or a .psd Photoshop document.");
        if (IsDragging) return new IOException("Finish dragging the canvas or controls before saving.");
        target.CommitChannelControlEdit();
        if (!CommitWarp(target)) return new InvalidOperationException("Couldn't apply Warp before saving.");
        target.FinishText();
        var snapshot = target.Document.Clone();
        var state = target.History.CurrentId;
        // Also protect Save As targets that another program changes during the encode.
        var destination = new FileInfo(path);
        (long, DateTime)? expected = destination.Exists ? (destination.Length, destination.LastWriteTimeUtc) : null;
        void CheckDestination()
        {
            var file = new FileInfo(path);
            (long, DateTime)? current = file.Exists ? (file.Length, file.LastWriteTimeUtc) : null;
            if (expected != current) throw new IOException("The destination changed during the save. Choose a different filename.");
        }
        try { await Task.Run(() =>
        {
            if (extension.Equals(PsdExport.Extension, StringComparison.OrdinalIgnoreCase)) PsdExport.Save(snapshot, path, CheckDestination);
            else ProjectFile.Save(snapshot, path, CheckDestination);
        }); }
        catch (Exception error) { return error; }
        if (!copy && !embeddedTabs.ContainsKey(target))
        {
            target.MarkSaved(path, state);
            RecordFileVersion(target, path);
        }
        if (Settings.Persist) _ = RecentPreviews.Remember(path, snapshot);
        if (!target.IsModified) recovery?.Forget(target);
        settings.AddRecent(Path.GetFullPath(path));
        return null;
    }

    private async Task Export(ExportFormat format)
    {
        if (session is not { } target) return;
        var snapshot = target.Document.Clone();
        var extension = format switch { ExportFormat.Jpeg => "jpg", ExportFormat.Webp => "webp", ExportFormat.Tiff => "tif", _ => "png" };
        SKBitmap? flattened = null;
        try
        {
            if (format == ExportFormat.Jpeg)
            {
                if (!await ExportStep("Rendering JPEG preview…", _ => { flattened = DocumentRenderer.Flatten(snapshot); })) return;
                if (await CanvasDialogs.JpegQuality(this, jpegQuality, flattened!) is not { } quality) return;
                jpegQuality = quality;
            }
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export " + extension.ToUpperInvariant(), SuggestedFileName = target.Title + "." + extension, DefaultExtension = extension,
                FileTypeChoices = [new FilePickerFileType(extension.ToUpperInvariant() + " image") { Patterns = ["*." + extension] }]
            });
            if (file?.TryGetLocalPath() is not { } path) return;
            var encodeQuality = format == ExportFormat.Png ? 100 : jpegQuality;
            if (await ExportStep($"Exporting {Path.GetFileName(path)}…", token =>
            {
                flattened ??= DocumentRenderer.Flatten(snapshot);
                token.ThrowIfCancellationRequested();
                ImageFiles.Save(flattened, path, format, encodeQuality, snapshot.Resolution, token);
            })) ShowNote($"Exported {Path.GetFileName(path)}");
        }
        catch (Exception error) { await Prompts.Alert(this, "Couldn't export", error.Message); }
        finally { flattened?.Dispose(); }
    }

    private async Task<bool> ExportStep(string message, Action<CancellationToken> work)
    {
        using var cancellation = new CancellationTokenSource();
        var dialog = new DialogWindow("Export", Ui.Label(message), "Cancel", cancellable: false);
        dialog.Closed += (_, _) => cancellation.Cancel();
        var shown = dialog.Ask(this);
        try
        {
            await Task.Run(() => work(cancellation.Token), cancellation.Token);
            return !cancellation.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return false; }
        finally
        {
            if (dialog.IsVisible) dialog.Close(false);
            await shown;
        }
    }

    // ---- Clipboard ----------------------------------------------------------------------------------------------

    private async Task Copy(bool merged)
    {
        if (session == null || !(merged ? session.CopyMerged() : session.Copy())) { ShowProblem("There is nothing to copy here."); return; }
        ShowCopied();
        if (EditorSession.Clipboard == null) { await ClearExternalClipboard(); return; }
        await PublishClipboard();
    }

    private async Task Cut()
    {
        if (session == null) return;
        session.Cut();
        ShowCopied();
        await PublishClipboard();
    }

    /// <summary>Copies a document's whole flattened picture, whatever is selected in it: the tab menu's Copy Image.</summary>
    private async Task CopyImage(EditorSession item)
    {
        problem = note = null;
        item.CopyMerged(whole: true);
        ShowCopied();
        await PublishClipboard();
    }

    /// <summary>Says what went to the clipboard, because a copy is otherwise silent and the first thing anyone doubts.</summary>
    private void ShowCopied()
    {
        if (EditorSession.CopiedLayers is { Layers.Count: var count }) ShowNote(count == 1 ? "Copied 1 layer" : $"Copied {count} layers");
        else if (EditorSession.Clipboard is { } image) ShowNote($"Copied {image.Pixels.Width} × {image.Pixels.Height} px");
    }

    /// <summary>Shares the copied pixels with other apps.</summary>
    private async Task PublishClipboard()
    {
        if (EditorSession.Clipboard is not { } image || Clipboard == null) return;
        try
        {
            using var bgra = new SKBitmap(new SKImageInfo(image.Pixels.Width, image.Pixels.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            image.Pixels.CopyTo(bgra, SKColorType.Bgra8888);
            var bitmap = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, bgra.GetPixels(), new Avalonia.PixelSize(bgra.Width, bgra.Height), new Avalonia.Vector(96, 96), bgra.RowBytes);
            await Clipboard.SetBitmapAsync(bitmap);
        }
        catch { /* The in-app clipboard still works when the desktop's clipboard refuses the image. */ }
    }

    /// <summary>Layers with nothing to show other apps (an adjustment) leave the desktop's clipboard empty, so Paste here is not mistaken for an outside image.</summary>
    private async Task ClearExternalClipboard()
    {
        if (Clipboard == null) return;
        try { await Clipboard.ClearAsync(); }
        catch { /* Nothing to share; the in-app clipboard still has the layers. */ }
    }

    private enum PasteKind { Normal, InPlace, Into }

    private async Task Paste(PasteKind kind = PasteKind.Normal)
    {
        if (session == null) return;
        ClipboardImage? external = null;
        try
        {
            if (await ExternalClipboardBitmap() is { } bitmap)
            {
                using (bitmap)
                {
                    using var stream = new MemoryStream();
                    bitmap.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                    stream.Position = 0;
                    external = new ClipboardImage(ImageFiles.Load(stream, "clipboard", preserveProfile: true), new SKPointI(int.MinValue / 2, int.MinValue / 2));
                }
            }
        }
        catch { /* Fall back to the in-app clipboard. */ }
        var pasted = kind switch
        {
            PasteKind.InPlace => session.PasteInPlace(external),
            PasteKind.Into => session.PasteInto(external),
            _ => session.Paste(external)
        };
        if (pasted == null) ShowProblem("The clipboard has no image.");
        else if (!session.IsEditingAlphaChannel && !session.IsEditingComponent) SelectTool(Tool.Move);
    }

    /// <summary>
    /// The image another app put on the clipboard, or null when the clipboard holds what was copied here, or no image.
    /// Pixels copied in this app keep their position; anything newer from another app wins. Paste and New Canvas both
    /// decide through this, so the size New Canvas offers is the size a paste brings.
    /// </summary>
    private async Task<Bitmap?> ExternalClipboardBitmap()
    {
        if (Clipboard == null) return null;
        try { return await Clipboard.TryGetInProcessDataAsync() == null ? await Clipboard.TryGetBitmapAsync() : null; }
        catch { return null; } // The in-app clipboard still works when the desktop's does not.
    }

    /// <summary>How long New Canvas waits for another app's clipboard before opening without its size.</summary>
    private static readonly TimeSpan ClipboardPatience = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The size of the image Paste would paste, for New Canvas to offer. A desktop clipboard that has not answered in
    /// time offers nothing rather than the in-app clipboard, which may be older than what it holds.
    /// </summary>
    private async Task<(int W, int H)?> ClipboardImageSize()
    {
        var external = ExternalClipboardBitmap();
        if (await Task.WhenAny(external, Task.Delay(ClipboardPatience)) != external)
        {
            _ = external.ContinueWith(late => late.Result?.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
            return null;
        }
        if (await external is { } bitmap)
            using (bitmap) return (bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        return EditorSession.Clipboard is { } image ? (image.Pixels.Width, image.Pixels.Height) : null;
    }

    // ---- Dialog-driven edits ------------------------------------------------------------------------------------

    private async Task ModifySelection(string title, string label, Func<int> initial, int max, Action<int> apply)
    {
        if (await Prompts.Number(this, title, label, initial(), 1, max) is { } value) apply((int)Math.Round(value));
        refreshOptions?.Invoke();
    }

    /// <summary>Select > Subject: what the picture shows in front of its plain backdrop.</summary>
    /// <summary>Select > Color Range: the panel is not modal, so the colors are clicked on the canvas while it is open.</summary>
    private void ShowColorRange()
    {
        if (session == null || !session.BeginColorRange()) return;
        ColorRangeWindow.Open(this, session);
    }

    private void SelectSubject()
    {
        var area = session?.Selection is { } selection ? Composa.Selections.SelectionMask.Bounds(selection) : (SkiaSharp.SKRectI?)null;
        _ = RunObjectSelection(area, Composa.Selections.SelectionMode.Replace);
    }

    private Histogram? HistogramOfActive()
    {
        if (session?.ActiveLayer is not { } layer) return null;
        if (session.EditingMaskBitmap is { } mask) return Histogram.Of(mask);
        if (layer.Pixels != null) return Histogram.Of(layer.RenderFilterPixels()!);
        return Histogram.Of(session.Composite());
    }

    private void AutoLevels()
    {
        if ((session?.EditingMaskBitmap ?? session?.ActiveLayer?.RenderFilterPixels()) is { } pixels) session!.Adjust(LevelsAdjustment.Auto(Histogram.Of(pixels)));
    }

    private async Task Adjust(AdjustmentKind kind)
    {
        if (session == null) return;
        var target = session;
        var histogram = HistogramOfActive();
        if (!target.BeginPreview(Adjustment.Create(kind).DisplayName)) { ShowProblem("Select a pixel layer or a mask first."); return; }
        var result = await AdjustmentDialogs.Edit(this, Adjustment.Create(kind), target.PreviewAdjustment, histogram, target.Foreground, target.Background);
        if (result == null || result.IsIdentity) target.CancelPreview();
        else { target.PreviewAdjustment(result); target.CommitPreview(); }
    }

    /// <summary>The last Camera Raw grade, so the panel opens where it was left; a hidden group is absent from it.</summary>
    private CameraRawSettings lastCameraRaw = new();

    private async Task Filter(FilterKind kind)
    {
        if (session == null) return;
        var target = session;
        if (!target.BeginFilter(kind)) { ShowProblem("Select a pixel layer or a mask first."); return; }
        var revision = 0;
        var serial = new SemaphoreSlim(1);
        FilterSettings? applied = null;
        async Task<bool> Render(FilterSettings value)
        {
            var mine = ++revision;
            await serial.WaitAsync();
            try
            {
                if (mine != revision) return false;
                if (value == applied) return true;
                if (!await target.PreviewFilterAsync(value, () => mine == revision)) return false;
                applied = value;
                return true;
            }
            finally { serial.Release(); }
        }

        async Task<bool> Preview(FilterSettings value)
        {
            try { return await Render(value); }
            catch (Exception error) { ShowProblem(error.Message); return false; }
        }
        try
        {
            FilterSettings? result;
            var seed = (uint)Random.Shared.Next();
            if (kind == FilterKind.CameraRaw)
            {
                FilterSettings Grade(CameraRawSettings value) => new() { Kind = kind, CameraRaw = value, Seed = seed };
                var grade = await CameraRawDialog.Show(this, lastCameraRaw, target.PreviewOriginal!, _ => { },
                    () => target.IsEditingMask ? target.EditingMaskBitmap : target.ActiveLayer?.Pixels, value => Preview(Grade(value)));
                if (grade != null) lastCameraRaw = grade;
                result = grade == null || grade.IsIdentity ? null : Grade(grade);
            }
            else
            {
                var initial = new FilterSettings { Kind = kind, Radius = kind is FilterKind.Sharpen or FilterKind.HighPass ? 2 : kind == FilterKind.MotionBlur ? 30 : 8, Amount = kind == FilterKind.Sharpen ? 60 : 20, Seed = seed };
                result = await AdjustmentDialogs.EditFilter(this, initial, value => { _ = Preview(value); });
            }
            if (result == null || result.IsIdentity) { revision++; target.CancelPreview(); return; }
            var enabled = IsEnabled;
            IsEnabled = false;
            try { if (await Render(result)) target.CommitPreview(); else target.CancelPreview(); }
            finally { IsEnabled = enabled; }
        }
        catch (Exception error) { revision++; target.CancelPreview(); ShowProblem(error.Message); }
    }

    private async Task NewAdjustmentLayer(AdjustmentKind kind)
    {
        if (session == null) return;
        var adjustment = Adjustment.Create(kind);
        if (adjustment is InvertAdjustment) { session.AddAdjustmentLayer(adjustment); return; }
        // The layer and its settings are one undo step, and cancelling the dialog leaves no trace of either.
        var target = session;
        var layer = target.AddAdjustmentLayer(adjustment, commit: false);
        var result = await AdjustmentDialogs.Edit(this, adjustment, a => target.SetAdjustment(layer, a), Histogram.Of(target.Composite()), target.Foreground, target.Background);
        if (result == null) target.Cancel();
        else
        {
            target.SetAdjustment(layer, result);
            target.Commit();
            target.NotifyLayersChanged();
        }
    }

    private async Task EditAdjustmentLayer(Layer layer, bool isNew)
    {
        if (session == null || layer.Adjustment == null) return;
        var target = session;
        var original = layer.Adjustment;
        if (original is InvertAdjustment) return;
        var histogram = Histogram.Of(target.Composite());
        target.Begin("Edit Adjustment");
        var result = await AdjustmentDialogs.Edit(this, original, a => target.SetAdjustment(layer, a), histogram, target.Foreground, target.Background);
        if (result != null && !result.ContentEquals(original))
        {
            target.SetAdjustment(layer, result);
            target.Commit();
        }
        else target.Cancel();
    }

    /// <summary>Adds an effect to the active layer and opens its settings; cancelling the dialog takes the effect away again.</summary>
    private async Task NewEffect(LayerEffectKind kind)
    {
        if (session == null) return;
        var target = session;
        if (target.ActiveLayer is not { Pixels: not null } layer) { ShowProblem("Select a layer with pixels to add an effect to."); return; }
        if (layer.Effects?.Contains(kind) == true) { await EditEffect(layer, kind); return; }
        target.AddEffect(layer, kind, commit: false);
        if (await EffectsDialog.Edit(this, target, layer, kind)) target.Commit();
        else { target.Cancel(); target.SelectedEffect = null; }
        target.NotifyLayersChanged();
    }

    /// <summary>Edits one effect with a live preview; the dialog undoes as one step.</summary>
    private async Task EditEffect(Layer layer, LayerEffectKind kind)
    {
        if (session == null || layer.Effects?.Contains(kind) != true) return;
        var target = session;
        var original = layer.Effects;
        target.SelectedEffect = (layer.Id, kind);
        target.Begin("Edit " + LayerEffects.DisplayName(kind));
        if (await EffectsDialog.Edit(this, target, layer, kind) && layer.Effects != original) target.Commit();
        else target.Cancel();
        target.NotifyLayersChanged();
    }

    private async Task CanvasSize()
    {
        if (session == null) return;
        if (await CanvasDialogs.CanvasSize(this, session.Document.Width, session.Document.Height) is not { } result) return;
        session.ResizeCanvas(result.Width, result.Height, result.Anchor);
        canvas.Fit();
    }

    private TrimOptions trimOptions = new();

    /// <summary>Image &gt; Trim: the dialog remembers its last choices for the session.</summary>
    private async Task Trim()
    {
        if (session == null) return;
        var target = session;
        if (await TrimDialog.Show(this, trimOptions) is not { } options) return;
        trimOptions = options;
        if (!target.Trim(options)) { ShowProblem("Nothing to trim: no edge is empty in that sense."); return; }
        canvas.Fit();
    }

    private async Task ImageSize()
    {
        if (session == null) return;
        if (await CanvasDialogs.ImageSize(this, session.Document.Width, session.Document.Height, session.Document.Resolution) is not { } result) return;
        Busy(() => session.ResizeImage(result.Width, result.Height, result.Resolution));
        canvas.Fit();
    }

    /// <summary>
    /// View > Grid Settings: the grid shows while the dialog is open, changing as it is edited, and goes back to how it
    /// was on Cancel. The settings are the person's, like the other view options: nothing is saved with the project or undone.
    /// </summary>
    private async Task ShowGridSettings()
    {
        if (session == null) return;
        var target = session;
        var original = target.View;
        void Preview(LayoutGrid grid, GridAppearance appearance)
        {
            target.View = target.View with { ShowGrid = true, Grid = grid, GridAppearance = appearance };
            canvas.InvalidateCanvas();
        }
        Preview(original.Grid, original.GridAppearance);
        var result = await GridSettingsDialog.Show(this, original.Grid, original.GridAppearance, Preview);
        target.View = original with { Grid = result?.Grid ?? original.Grid, GridAppearance = result?.Appearance ?? original.GridAppearance };
        canvas.InvalidateCanvas();
        RememberToolSettings();
    }
}

internal static class SessionExtensions
{
    public static Guid? ActiveLayerIdOrNull(this EditorSession session) => session.Document.ActiveLayerId;
}
