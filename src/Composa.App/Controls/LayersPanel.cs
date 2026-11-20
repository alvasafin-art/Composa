using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Composa.Editing;
using Composa.Filters;
using Composa.Model;
using Composa.ColorManagement;
using SkiaSharp;
using BlendMode = Composa.Model.BlendMode;
using TextAlignment = Avalonia.Media.TextAlignment;

namespace Composa.App.Controls;

/// <summary>The layer stack: visibility, thumbnails, masks, folders, blend mode and opacity, drag-to-reorder.</summary>
public sealed partial class LayersPanel : UserControl
{
    private EditorSession? session;
    private readonly ItemsControl rows = new();
    private const double HeaderFieldHeight = 28;
    private readonly ComboBox blend;
    private readonly Slider opacity;
    private readonly NumericUpDown opacityNumber = new() { Name="LayerOpacity", Width=44, Minimum=0, Maximum=100, Increment=1,
        ShowButtonSpinner=false, ClipValueToMinMax=true, FormatString="0", Padding=new Thickness(3,0), VerticalContentAlignment=VerticalAlignment.Center };

    private readonly Border dropLine = new() { Height = 2, Background = Palette.Accent, IsVisible = false, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top };
    private readonly Dictionary<Guid, Border> rowFor = [];
    private bool updating;
    private bool opacityDragging;
    private double opacityStart;
    private Guid? renaming;
    private Action<bool>? finishRename;
    private static readonly ConditionalWeakTable<SKBitmap, Dictionary<RgbProfile, Bitmap>> Thumbnails = new();
    private RgbProfile displayProfile = RgbProfile.Srgb;
    public RgbProfile DisplayProfile
    {
        get => displayProfile;
        set { if (displayProfile.Equals(value)) return; displayProfile = value; Rebuild(); }
    }

    // Drag state.
    private Layer? pressed;
    private Point pressPoint;
    private bool dragging;
    private (Layer Target, LayerDrop Drop)? dropTarget;
    private bool? eyeSwipe;
    private bool? thumbnailTarget;
    private bool rebuilding, rebuildAgain;

    public event Action<Layer>? EditAdjustmentRequested;
    public event Action<Layer>? EditTextRequested;
    public event Action<Layer>? EditShapeRequested;
    public event Action<Layer>? EditSmartObjectRequested;
    public event Action<Layer, bool>? ReplaceSmartObjectRequested;
    public event Action<Layer>? UpdateLinkedSmartObjectRequested;
    public event Action<Layer>? LinkedSourceInfoRequested;
    public Func<Layer, Composa.IO.LinkedSourceStatus?>? LinkedSourceStatus { get; set; }
    public void RefreshSourceStatus() => Rebuild();
    public event Action<Layer, SmartFilter>? EditSmartFilterRequested;
    public event Action<Layer, SmartFilter>? EditSmartFilterBlendingRequested;
    public event Action<AdjustmentKind>? NewAdjustmentRequested;
    /// <summary>Double-click on an effect row: open its settings.</summary>
    public event Action<Layer, LayerEffectKind>? EditEffectRequested;
    /// <summary>The footer's effects menu: add an effect to the active layer.</summary>
    public event Action<LayerEffectKind>? NewEffectRequested;
    public event Action<Layer>? EditTagsRequested;
    public event Action<Layer>? EditBlendingRequested;
    public event Action<Layer>? EditMaskPropertiesRequested;

    /// <summary>The blend mode at each index of the blend menu; null for the lines between groups.</summary>
    private readonly List<BlendMode?> blendAt = [];

    // An effect row being Alt-dragged onto another layer.
    private (Layer Layer, LayerEffectKind Kind)? effectDrag;
    private Point effectDragStart;
    private Border? effectDropRow;

    static LayersPanel() => Composa.Rendering.Pixels.Invalidated += bitmap => Thumbnails.Remove(bitmap);

    internal TextBlock PanelTitle { get; } = Ui.Label(Locale.T("Layers"), size: 13, weight: FontWeight.SemiBold);

    public LayersPanel()
    {
        // Grouped as Photoshop groups them (darkening, lightening, contrast, comparative, component) with a line between,
        // so the long list stays readable. The lines are disabled items, which the keyboard and the pointer skip.
        var items = new List<object>();
        for (var group = 0; group < BlendModeExtensions.Groups.Length; group++)
        {
            if (group > 0) { items.Add(new ComboBoxItem { Content = Ui.Separator(false), IsEnabled = false, Padding = new Thickness(0, 4), MinHeight = 0 }); blendAt.Add(null); }
            foreach (var mode in BlendModeExtensions.Groups[group]) { items.Add(new ComboBoxItem { Content = Locale.T(mode.DisplayName()) }); blendAt.Add(mode); }
        }
        blend = new ComboBox { ItemsSource = items, HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            MinWidth = 0, Height = HeaderFieldHeight, Padding = new Thickness(8, 0) };
        blend.SelectionChanged += (_, _) =>
        {
            if (updating || session?.ActiveLayer is not { } layer || blend.SelectedIndex < 0 || blendAt[blend.SelectedIndex] is not { } mode) return;
            foreach (var target in session.SelectedRoots()) session.SetBlend(target, mode);
        };
        opacity = new Slider { Minimum = 0, Maximum = 100, Value = 100, MinHeight = 0, Height = HeaderFieldHeight, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        // The stock slider reserves two fixed tick rows even when ticks are hidden.
        // Share the spare space around its track so the thumb stays on the label's centre line.
        opacity.Resources["SliderPreContentMargin"] = GridLength.Star;
        opacity.Resources["SliderPostContentMargin"] = GridLength.Star;
        opacity.Resources["SliderHorizontalHeight"] = HeaderFieldHeight;
        opacity.Resources["SliderHorizontalThumbHeight"] = 14d;
        opacity.Resources["SliderHorizontalThumbWidth"] = 14d;
        opacity.IsSnapToTickEnabled = true;
        opacity.TickFrequency = 1;
        opacity.ValueChanged += (_, e) =>
        {
            if (updating || session?.ActiveLayer is not { } layer) return;
            updating = true;
            try { opacityNumber.SetCurrentValue(NumericUpDown.ValueProperty, (decimal)Math.Round(e.NewValue)); }
            finally { updating = false; }
            if (!opacityDragging) { opacityDragging = true; opacityStart = layer.Opacity; session.Begin("Opacity"); }
            session.SetOpacity(layer, Math.Round(e.NewValue) / 100);
        };
        opacity.AddHandler(PointerReleasedEvent, (_, _) => EndOpacityDrag(), Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        opacity.LostFocus += (_, _) => EndOpacityDrag();
        opacity.KeyUp += (_, _) => EndOpacityDrag();

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,1.06*"), ColumnSpacing = 8,
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"), Margin = new Thickness(10, 8, 10, 6) };
        var title = PanelTitle;
        title.Margin = new Thickness(0, 0, 0, 6);
        header.Children.Add(title);
        Grid.SetRow(blend, 1);
        header.Children.Add(blend);

        opacityNumber.ValueChanged += (_, e) =>
        {
            if (updating || e.NewValue is not {} value || opacity.Value == (double)value) return;
            opacity.Value = (double)value;
            EndOpacityDrag();
        };
        opacityNumber.LostFocus += (_, _) => EndOpacityDrag();
        var opacityRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 3, Height = HeaderFieldHeight };
        opacityRow.Children.Add(opacity);
        Grid.SetColumn(opacityNumber, 1); opacityRow.Children.Add(opacityNumber);
        var percent = Ui.Label("%", Palette.Secondary, 11); percent.VerticalAlignment=VerticalAlignment.Center;
        Grid.SetColumn(percent, 2); opacityRow.Children.Add(percent);
        foreach (var control in new Control[] { opacity, opacityNumber })
        {
            Avalonia.Automation.AutomationProperties.SetName(control, Locale.T("Layer Opacity"));
            Locale.Tip(control, "Layer Opacity");
        }
        Grid.SetColumn(opacityRow, 1); Grid.SetRow(opacityRow, 1); header.Children.Add(opacityRow);
        AddSearchHeader(header);

        var adjustmentMenu = new ContextMenu();
        foreach (var kind in Enum.GetValues<AdjustmentKind>())
        {
            var item = new MenuItem { Header = Adjustment.Create(kind).DisplayName + "…" };
            item.Click += (_, _) => NewAdjustmentRequested?.Invoke(kind);
            adjustmentMenu.Items.Add(item);
        }
        var adjustButton = Ui.IconButton(Icons.Adjust, "New adjustment layer", () => { });
        adjustButton.Click += (_, _) => adjustmentMenu.Open(adjustButton);
        var effectsMenu = new ContextMenu();
        foreach (var kind in Enum.GetValues<LayerEffectKind>())
        {
            var item = new MenuItem { Header = LayerEffects.DisplayName(kind) + "…" };
            item.Click += (_, _) => NewEffectRequested?.Invoke(kind);
            effectsMenu.Items.Add(item);
        }
        var effectsButton = Ui.IconButton(Icons.Effects, "Layer effects: stroke, drop shadow, color overlay, inner shadow, outer glow, inner glow", () => { });
        effectsButton.Click += (_, _) => { if (session?.ActiveLayer is { Pixels: not null }) effectsMenu.Open(effectsButton); };

        var footer = Ui.Row(2,
            Ui.IconButton(Icons.Plus, "New layer (Ctrl+Shift+N)", () => session?.AddBlankLayer()),
            Ui.IconButton(Icons.Folder, "Group selected layers (Ctrl+G)", () => session?.GroupSelectedLayers()),
            Ui.IconButton(Icons.Mask, "Add layer mask (Alt: hide all)", () => { if (session?.ActiveLayer is { } layer) session.AddMask(layer); }),
            effectsButton,
            adjustButton,
            Ui.IconButton(Icons.Trash, "Delete layer, mask or effect", () => DeleteLayerOrMask()));
        footer.HorizontalAlignment = HorizontalAlignment.Center;
        footer.Margin = new Thickness(0, 4);

        InitializeRows();
        var list = new Panel();
        rows.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (eyeSwipe is not { } show || session == null) return;
            if (!e.GetCurrentPoint(rows).Properties.IsLeftButtonPressed) { eyeSwipe = null; return; }
            var position = e.GetPosition(rows);
            if (position.X > 40) return;
            var row = LayerRowAt(position.Y);
            if (row?.Tag is Layer under && under.Visible != show) session.SetVisible(under, show);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        rows.PointerMoved += (_, e) => { if (effectDrag != null) EffectDragMoved(e); };
        rows.PointerReleased += (_, e) => { if (effectDrag != null) EffectDragReleased(e); };
        rows.PointerCaptureLost += (_, e) =>
        {
            if (e.Source != rows) return;
            effectDrag = null;
            ClearEffectDrop();
        };
        rows.AddHandler(PointerReleasedEvent, (_, _) => eyeSwipe = null, Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        list.Children.Add(rows);
        list.Children.Add(dropLine);
        var scroll = new ScrollViewer { Content = list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        scroll.PointerPressed += (_, e) => { if (e.Source == scroll || e.Source == list) Focus(); };

        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Background = Palette.Panel };
        layout.Children.Add(header);
        Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        Grid.SetRow(footer, 2); layout.Children.Add(footer);
        Content = layout;
    }

    public EditorSession? Session
    {
        get => session;
        set
        {
            if (ReferenceEquals(session, value)) return;
            if (session != null) session.LayersChanged -= ModelChanged;
            ClearDropHighlight();
            ClearEffectDrop();
            pressed = null; dragging = false; effectDrag = null; dropTarget = null; eyeSwipe = null;
            dropLine.IsVisible = false;
            finishRename?.Invoke(true);
            renaming = null;
            ForgetRecentRows();
            session = value;
            if (session != null) session.LayersChanged += ModelChanged;
            Rebuild();
        }
    }

    /// <summary>The trash button and Delete: a highlighted effect goes first, then the targeted mask, then the layers.</summary>
    public void DeleteLayerOrMask()
    {
        if (session?.EditingAlphaChannel is { } alpha) { session.DeleteSelectionChannel(alpha); return; }
        if (session == null) return;
        if (session.SelectedEffect != null) { session.RemoveSelectedEffect(); return; }
        if (session.ActiveLayer is not { } layer) return;
        if (session.IsEditingSmartFilterMask) session.DeleteSmartFilterMask(layer);
        else if (session.IsEditingMask) session.DeleteMask(layer); else session.DeleteSelectedLayers();
    }

    public void BeginRename()
    {
        renaming = session?.ActiveLayer?.Id;
        if (renaming is {} id && entriesById.TryGetValue(id, out var entry)) rows.ScrollIntoView(entry);
        Rebuild();
    }

    private void EndOpacityDrag()
    {
        if (!opacityDragging) return;
        opacityDragging = false;
        // Dragging back to where it started is not an edit.
        if (session?.ActiveLayer is { } layer && layer.Opacity == opacityStart) session.Cancel(); else session?.Commit();
    }

    private void ModelChanged()
    {
        ForgetRecentRows();
        Rebuild();
    }

    private void Rebuild()
    {
        // Clearing the rows detaches a rename box, whose lost-focus handler commits the name and asks for another
        // rebuild. That request is honoured after this one finishes instead of interleaving with it.
        if (rebuilding) { rebuildAgain = true; return; }
        rebuilding = true;
        try
        {
            do
            {
                rebuildAgain = false;
                RebuildRows();
            } while (rebuildAgain);
        }
        finally { rebuilding = false; }
    }

    private void RebuildRows()
    {
        updating = true;
        var active = session?.ActiveLayer;
        blend.IsEnabled = opacityNumber.IsEnabled = opacity.IsEnabled = active != null;
        blend.SelectedIndex = blendAt.IndexOf(active?.Blend ?? BlendMode.Normal);
        if(blend.Items[blendAt.IndexOf(BlendMode.PassThrough)] is ComboBoxItem passThrough)passThrough.IsEnabled=active?.IsGroup==true;
        if (!opacityDragging) opacity.Value = (active?.Opacity ?? 1) * 100;
        opacityNumber.Value = (decimal)Math.Round(opacity.Value);
        updating = false;
        RefreshRows();
    }

    private Border BuildRow(Entry entry, Appearance state)
    {
        var current = session!;
        var layer = entry.Layer;
        var selected = state.Selected;
        var dim = !layer.Visible || !entry.ParentVisible;

        var eye = new Button { Classes = { "flat" }, Width = 28, Height = 28, Padding = new Thickness(0), Content = Icons.Create(Icons.Eye, 15, layer.Visible ? null : new SolidColorBrush(Color.Parse("#555555"))) };
        Locale.Tip(eye, "Show or hide (drag down the column to swipe, Alt-click to show only this layer)");
        eye.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                current.SoloLayerId = current.SoloLayerId == entry.Layer.Id ? null : entry.Layer.Id;
                current.InvalidateAll();
                e.Handled = true;
                return;
            }
            // Swiping down the eye column shows or hides every layer passed over. The pointer is released from the eye
            // (whose row is about to be rebuilt) and the list itself follows the drag from here.
            eyeSwipe = !entry.Layer.Visible;
            e.Pointer.Capture(null);
            current.SetVisible(entry.Layer, eyeSwipe.Value);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(entry.Depth * 14, 0, 0, 0), Opacity = dim ? 0.45 : 1 };
        if (layer.Clipped) content.Children.Add(Icons.Create(Icons.ClipArrow, 13, Palette.Secondary));
        if (layer.IsGroup)
        {
            var chevron = new Button { Classes = { "flat" }, Padding = new Thickness(2), IsEnabled = !IsFiltering, Content = Icons.Create(layer.Collapsed && !IsFiltering ? Icons.ChevronRight : Icons.ChevronDown, 12) };
            chevron.Click += (_, _) => { entry.Layer.Collapsed = !entry.Layer.Collapsed; Rebuild(); };
            content.Children.Add(chevron);
            content.Children.Add(Icons.Create(Icons.Folder, 18));
        }
        else if (layer.IsAdjustment) content.Children.Add(Thumb(Icons.Create(Icons.Adjust, 18), state.ActivePixels));
        else if (layer.Pixels != null)
        {
            var pixelThumb = Thumb(new Image { Source = state.Pixels, Stretch = Stretch.Uniform }, state.ActivePixels);
            // The row's own press handler (which bubbles next) selects the layer and then applies this target.
            pixelThumb.PointerPressed += (_, _) => thumbnailTarget = false;
            content.Children.Add(pixelThumb);
            if (layer.IsSmartObject)
            {
                var status = state.LinkStatus;
                var badge = Ui.Label(layer.SmartObject!.Link != null ? status is { State: not Composa.IO.LinkedSourceState.Current } ? "↗!" : "↗" : "◇", Palette.Accent);
                Locale.Tip(badge, layer.SmartObject.Link is { } link ? $"Linked smart object · {link.Path}" + (link.HasLocalEdits ? " · local cached edits" : "")
                    + (status?.Detail is { } detail ? "\n" + detail : status is { State: not Composa.IO.LinkedSourceState.Current } ? "\n" + status.State : "") : "Smart object · double-click the thumbnail to edit contents");
                content.Children.Add(badge);
            }
        }
        if (layer.Mask != null)
        {
            var link = new Button { Classes = { "flat" }, Width = 18, Height = 28, Padding = new Thickness(0), Content = Icons.Create(layer.MaskLinked ? Icons.Link : Icons.Unlink, 16, Palette.Secondary) };
            Locale.Tip(link, layer.MaskLinked ? "Unlink mask: move and transform it independently" : "Link mask: move it together with the layer");
            link.Click += (_, _) => current.SetMaskLinked(entry.Layer, !entry.Layer.MaskLinked);
            content.Children.Add(link);
            var maskThumb = Thumb(new Image { Source = state.Mask, Stretch = Stretch.Uniform, Opacity = layer.MaskEnabled ? 1 : 0.35 },
                state.ActiveMask, mask: true);
            maskThumb.PointerPressed += (_, _) => thumbnailTarget = true;
            Locale.Tip(maskThumb, "Layer mask: click to paint on it, Shift-click to disable, Ctrl-click to load as selection");
            maskThumb.AddHandler(PointerPressedEvent, (_, e) =>
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { current.SetMaskEnabled(entry.Layer, !entry.Layer.MaskEnabled); e.Handled = true; }
                else if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) { current.SelectLayerMask(entry.Layer); e.Handled = true; }
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            content.Children.Add(maskThumb);
        }

        if (renaming == layer.Id)
        {
            var box = new TextBox { Text = layer.Name, MinWidth = 110, Padding = new Thickness(4, 2) };
            var done = false;
            void Finish(bool keep)
            {
                if (done) return;
                done = true;
                if (finishRename == Finish) { finishRename = null; renaming = null; }
                if (keep && box.Text is { } text && ReferenceEquals(current.Document.Find(layer.Id), layer))
                    current.Rename(layer, text);
                Rebuild();
            }
            finishRename = Finish;
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Finish(true); e.Handled = true; } else if (e.Key == Key.Escape) { Finish(false); e.Handled = true; } };
            box.LostFocus += (_, _) => Finish(true);
            box.AttachedToVisualTree += (_, _) => { box.Focus(); box.SelectAll(); };
            content.Children.Add(box);
        }
        else
        {
            var name = Ui.Label(layer.Name);
            if (layer.Locks != LayerLocks.None) { name.Text += "  [locked]"; Locale.Tip(name, "Locked: " + layer.Locks); }
            if (layer.VectorMask != null) { name.Text += "  [vector mask]"; }
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.MaxWidth = 150;
            ToolTip.SetTip(name, state.Tags == null ? layer.Name : layer.Name + "\n" + Locale.T("Tags") + ": " + state.Tags);
            if (layer.IsLive) name.FontStyle = FontStyle.Italic;
            content.Children.Add(name);
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(eye);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        var row = new Border
        {
            Child = grid, Background = selected ? Palette.Selected : Brushes.Transparent, Padding = new Thickness(4, 3), MinHeight = 40,
            CornerRadius = new CornerRadius(4), Margin = new Thickness(4, 0), Tag = layer
        };
        row.PointerPressed += (_, e) => RowPressed(entry.Layer, row, e);
        row.PointerMoved += (_, e) => RowMoved(e);
        row.PointerReleased += (_, e) => RowReleased(e);
        row.PointerCaptureLost += (_, _) => { if (dragging) EndRowDrag(); };
        row.ContextMenu = BuildMenu(entry);
        return row;
    }

    /// <summary>An effect belongs visually to its layer but has its own selection and visibility control.</summary>
    private Control BuildEffectRow(Layer layer, LayerEffectKind kind, int depth, bool parentVisible)
    {
        var current = session!;
        var enabled = layer.Effects!.IsEnabled(kind);
        var selected = current.SelectedEffect is { } s && s.LayerId == layer.Id && s.Kind == kind;
        var eye = new Button { Classes = { "flat" }, Width = 24, Height = 22, Padding = new Thickness(0), Content = Icons.Create(enabled ? Icons.Eye : Icons.EyeOff, 12, enabled ? Palette.Secondary : new SolidColorBrush(Color.Parse("#555555"))) };
        Locale.Tip(eye, enabled ? "Hide " + LayerEffects.DisplayName(kind).ToLowerInvariant() : "Show " + LayerEffects.DisplayName(kind).ToLowerInvariant());
        eye.Click += (_, _) => current.ToggleEffect(layer, kind);
        var name = Ui.Label(LayerEffects.DisplayName(kind), enabled ? Palette.Foreground : Palette.Secondary);
        name.FontSize = 11.5;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(depth * 14 + 46, 0, 0, 0), Opacity = parentVisible ? 1 : 0.45, Children = { eye, name } };
        var row = new Border
        {
            Child = content, Background = selected ? Palette.Selected : Brushes.Transparent, Padding = new Thickness(4, 1), Height = 24,
            CornerRadius = new CornerRadius(4), Margin = new Thickness(4, 0), Tag = (layer, kind)
        };
        Locale.Tip(row, "Click to select, double-click to edit, Alt-drag onto another layer to copy the " + LayerEffects.DisplayName(kind).ToLowerInvariant());
        row.PointerPressed += (_, e) =>
        {
            if (e.Source == eye || (e.Source as Control)?.FindAncestorOfType<Button>() == eye) return;
            var properties = e.GetCurrentPoint(row).Properties;
            if (!properties.IsLeftButtonPressed && !properties.IsRightButtonPressed) return;
            if (current.Document.ActiveLayerId != layer.Id) current.SelectLayer(layer.Id);
            current.SelectedEffect = (layer.Id, kind);
            if (e.ClickCount == 2 && properties.IsLeftButtonPressed) { EditEffectRequested?.Invoke(layer, kind); e.Handled = true; return; }
            Rebuild();
            if (properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                effectDrag = (layer, kind);
                effectDragStart = e.GetPosition(rows);
                e.Pointer.Capture(rows);
            }
            e.Handled = true;
        };
        var menu = new ContextMenu();
        void Add(string header, Action action)
        {
            var item = new MenuItem { Header = Locale.T(header) };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("Edit " + LayerEffects.DisplayName(kind) + "…", () => EditEffectRequested?.Invoke(layer, kind));
        Add(enabled ? "Hide" : "Show", () => current.ToggleEffect(layer, kind));
        Add("Delete", () => current.RemoveEffect(layer, kind));
        row.ContextMenu = menu;
        return row;
    }

    private Control BuildSmartFilterRow(Layer layer, SmartFilter filter, int depth)
    {
        var current = session!;
        var toggle = Ui.Check("", filter.Enabled, enabled => current.ChangeSmartFilter(layer, filter.Id, enabled: enabled));
        var edit = Ui.TextButton(FilterSettings.DisplayName(filter.Settings.Kind) + "…", () => EditSmartFilterRequested?.Invoke(layer, filter));
        edit.MaxWidth = 130;
        Locale.Tip(edit, FilterSettings.DisplayName(filter.Settings.Kind));
        var blending = Ui.TextButton($"{filter.Opacity:P0}", () => EditSmartFilterBlendingRequested?.Invoke(layer, filter));
        Locale.Tip(blending, $"Smart filter blending: {filter.Blend.DisplayName()}, {filter.Opacity:P0}");
        var delete = Ui.IconButton(Icons.Trash, "Delete smart filter", () => current.ChangeSmartFilter(layer, filter.Id, remove: true));
        var row = new Border { Padding = new Thickness(38 + depth * 16, 2, 4, 2), Child = Ui.Row(5, toggle, edit, blending, delete) };
        var menu = new ContextMenu();
        void Add(string name, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = Locale.T(name), IsEnabled = enabled }; item.Click += (_, _) => action(); menu.Items.Add(item);
        }
        var index = Array.FindIndex(layer.SmartFilters, f => f.Id == filter.Id);
        Add("Blending Options…", () => EditSmartFilterBlendingRequested?.Invoke(layer, filter));
        Add("Move Filter Up", () => current.MoveSmartFilter(layer, filter.Id, 1), index < layer.SmartFilters.Length - 1);
        Add("Move Filter Down", () => current.MoveSmartFilter(layer, filter.Id, -1), index > 0);
        row.ContextMenu = menu; return row;
    }

    private Control BuildSmartFilterMaskRow(Layer layer, int depth)
    {
        var current = session!;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (layer.SmartFilterMask is { } mask)
        {
            var thumbnail = Thumb(new Image { Source = Thumbnail(mask), Stretch = Stretch.Uniform, Opacity = layer.SmartFilterMaskEnabled ? 1 : .35 },
                current.ActiveLayer?.Id == layer.Id && current.IsEditingSmartFilterMask, mask: true);
            Locale.Tip(thumbnail, "Smart filter mask: paint black to hide filtering, white to reveal it; Shift-click to disable");
            thumbnail.PointerPressed += (_, e) =>
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) current.SetSmartFilterMaskEnabled(layer, !layer.SmartFilterMaskEnabled);
                else { current.SelectLayer(layer.Id); current.EditingSmartFilterMask = true; current.NotifyLayersChanged(); }
                e.Handled = true;
            };
            content.Children.Add(thumbnail);
        }
        else
        {
            var add = Ui.TextButton("Add filter mask", () => { current.SelectLayer(layer.Id); current.AddSmartFilterMask(layer); });
            Locale.Tip(add, "Mask the smart filter stack without hiding the object"); content.Children.Add(add);
        }
        content.Children.Add(Ui.Label(Locale.T("Smart Filters"), Palette.Secondary));
        var row = new Border { Padding = new Thickness(38 + depth * 16, 2, 4, 2), Child = content };
        var menu = new ContextMenu();
        void Add(string title, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = Locale.T(title), IsEnabled = enabled }; item.Click += (_, _) => action(); menu.Items.Add(item);
        }
        Add("Add Filter Mask", () => { current.SelectLayer(layer.Id); current.AddSmartFilterMask(layer); }, layer.SmartFilterMask == null);
        Add("Hide All Filters", () => { current.SelectLayer(layer.Id); current.AddSmartFilterMask(layer, true); }, layer.SmartFilterMask == null);
        Add(layer.SmartFilterMaskEnabled ? "Disable Filter Mask" : "Enable Filter Mask", () => current.SetSmartFilterMaskEnabled(layer, !layer.SmartFilterMaskEnabled), layer.SmartFilterMask != null);
        Add("Delete Filter Mask", () => current.DeleteSmartFilterMask(layer), layer.SmartFilterMask != null);
        row.ContextMenu = menu; return row;
    }

    private void EffectDragMoved(PointerEventArgs e)
    {
        if (effectDrag == null || session == null) return;
        if (!e.GetCurrentPoint(rows).Properties.IsLeftButtonPressed) { effectDrag = null; ClearEffectDrop(); return; }
        var position = e.GetPosition(rows);
        if (Math.Abs(position.Y - effectDragStart.Y) < 6 && effectDropRow == null) return;
        ClearEffectDrop();
        var target = LayerRowAt(position.Y);
        if (target?.Tag is Layer layer && session.CanCopyEffect(effectDrag.Value.Kind, effectDrag.Value.Layer, layer))
        {
            effectDropRow = target;
            target.BorderBrush = Palette.Accent;
            target.BorderThickness = new Thickness(1);
        }
    }

    private void EffectDragReleased(PointerReleasedEventArgs e)
    {
        var drag = effectDrag;
        var drop = effectDropRow;
        effectDrag = null;
        ClearEffectDrop();
        e.Pointer.Capture(null);
        if (drag == null || session == null || drop?.Tag is not Layer target) return;
        session.CopyEffect(drag.Value.Kind, drag.Value.Layer, target);
    }

    private void ClearEffectDrop()
    {
        if (effectDropRow == null) return;
        effectDropRow.BorderBrush = null;
        effectDropRow.BorderThickness = new Thickness(0);
        effectDropRow = null;
    }

    private static Border Thumb(Control child, bool highlighted, bool mask = false)
    {
        var border = new Border
        {
            Width = 34, Height = 34, Child = child, Background = new SolidColorBrush(Color.Parse("#4A4A4A")), ClipToBounds = true,
            BorderBrush = highlighted ? Brushes.White : new SolidColorBrush(Color.Parse("#161616")), BorderThickness = new Thickness(highlighted ? 2 : 1)
        };
        if (highlighted && mask)
        {
            border.Name = "ActiveMaskThumbnail";
            border.CornerRadius = new CornerRadius(7);
            border.BorderBrush = Palette.Accent;
            border.Child = null;
            border.Child = new Border { CornerRadius = new CornerRadius(5), BorderBrush = Brushes.White,
                BorderThickness = new Thickness(1), ClipToBounds = true, Child = child };
        }
        return border;
    }

    private unsafe Bitmap Thumbnail(SKBitmap source)
    {
        var profiles = Thumbnails.GetValue(source, _ => new());
        if (profiles.TryGetValue(displayProfile, out var cached)) return cached;
        const int side = 68;
        var scale = Math.Min((double)side / source.Width, (double)side / source.Height);
        int w = Math.Max(1, (int)Math.Round(source.Width * scale)), h = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var small = new SKBitmap(Composa.Rendering.Pixels.WorkingInfo(w, h, ColorDepth.Eight, displayProfile).WithColorType(SKColorType.Bgra8888));
        using (var canvas = new SKCanvas(small))
        {
            if (source.ColorType == SKColorType.Alpha8)
            {
                // Masks show as grayscale: white where they reveal.
                canvas.Clear(SKColors.Black);
                using var paint = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(SKColors.White, SKBlendMode.SrcIn) };
                canvas.DrawBitmap(source, new SKRect(0, 0, w, h), paint);
            }
            else
            {
                // Sample a reduced copy first: far cheaper than filtering tens of megapixels for a 68 px thumbnail.
                using var image = SKImage.FromPixels(source.PeekPixels());
                canvas.DrawImage(image, new SKRect(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            }
        }
        var bitmap = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, small.GetPixels(), new PixelSize(w, h), new Vector(96, 96), small.RowBytes);
        profiles[displayProfile] = bitmap;
        // Old render operations may still own an evicted image, so leave disposal to their references/GC.
        while (profiles.Count > 4) profiles.Remove(profiles.Keys.First(p => !p.Equals(displayProfile)));
        return bitmap;
    }

    /// <summary>
    /// The row's menu, as the macOS app orders it: the layer itself, then clipping and folders, then its mask, then
    /// visibility. Layer effects have their own rows and footer button, so they are not repeated here. A right-click
    /// on a layer outside the selection selects it first (see <see cref="RowPressed"/>); inside a selection of several,
    /// the whole selection is what Duplicate, Delete, Group and Merge act on.
    /// </summary>
    private ContextMenu BuildMenu(Entry entry)
    {
        var menu = new ContextMenu();
        // Rows are refreshed during ordinary edits. Only the menu being opened needs
        // commands or a scan of the other layers for merge, clipping and mask targets.
        void EnsureCommands(object? sender, EventArgs e)
        {
            if (menu.Items.Count == 0) PopulateMenu(menu, entry.Layer);
        }
        menu.Opening += EnsureCommands;
        menu.Opened += EnsureCommands; // Programmatic Open bypasses Opening in Avalonia.
        menu.Closed += (_, _) => menu.Items.Clear();
        return menu;
    }

    private void PopulateMenu(ContextMenu menu, Layer layer)
    {
        var current = session!;
        var locks = new MenuItem { Header = Locale.T("Lock") };
        foreach (var (flag, title) in new[] { (LayerLocks.Pixels, "Pixels"), (LayerLocks.Position, "Position"), (LayerLocks.Transparency, "Transparency") })
        {
            var item = new MenuItem { Header = Locale.T(title), ToggleType = MenuItemToggleType.CheckBox, IsChecked = layer.Locks.HasFlag(flag) };
            item.Click += (_, _) => current.SetLayerLocks(layer, layer.Locks ^ flag);
            locks.Items.Add(item);
        }
        MenuItem Add(string header, Action action, bool enabled = true, MenuItem? parent = null)
        {
            var item = new MenuItem { Header = Locale.T(header), IsEnabled = enabled };
            item.Click += (_, _) => { if (!current.Document.SelectedLayerIds.Contains(layer.Id)) current.SelectLayer(layer.Id); action(); };
            (parent?.Items ?? menu.Items).Add(item);
            return item;
        }
        var several = current.Document.SelectedLayerIds.Count > 1 && current.Document.SelectedLayerIds.Contains(layer.Id);
        var targetsMask = current.IsEditingMask && layer.Id == current.ActiveLayer?.Id && layer.Mask != null;

        Add(several ? "Duplicate Layers" : "Duplicate Layer", () => current.DuplicateSelectedLayers());
        Add("Rename…", () => { renaming = layer.Id; Rebuild(); }, !several);
        Add(targetsMask ? "Delete Mask" : several ? "Delete Selected Layers" : "Delete Layer", () => { if (targetsMask) current.DeleteMask(layer); else current.DeleteSelectedLayers(); });
        menu.Items.Add(locks);
        Add("Tags…", () => EditTagsRequested?.Invoke(layer), !several);
        Add("Blending Options…", () => EditBlendingRequested?.Invoke(layer), !several);
        if (layer.IsAdjustment) Add("Edit Adjustment…", () => EditAdjustmentRequested?.Invoke(layer));
        if (layer.Shape != null) Add("Shape Properties…", () => EditShapeRequested?.Invoke(layer));
        if (layer.Text != null) Add("Edit Text…", () => EditTextRequested?.Invoke(layer));
        if (layer.IsLive) Add("Rasterize Layer", () => current.RasterizeShape(layer));
        if (layer.Shape?.Path is { Closed: true })
        {
            var targets = new MenuItem { Header = Locale.T("Use path as vector mask") };
            foreach (var target in current.Document.AllLayers().Where(l => l.Id != layer.Id && l.Pixels != null))
                Add(target.Name, () => current.ApplyPathAsVectorMask(layer, target), !current.PixelsLocked(target), targets);
            menu.Items.Add(targets);
        }
        if (layer.VectorMask != null)
        {
            Add("Vector mask to selection", () => current.PathToSelection(layer));
            Add("Delete vector mask", () => current.RemoveVectorMask(layer));
        }
        if (!layer.IsSmartObject) Add("Convert to Smart Object", () => current.ConvertToSmartObject());
        else
        {
            Add("Edit Smart Object Contents…", () => EditSmartObjectRequested?.Invoke(layer));
            Add("New Smart Object via Copy", () => current.DuplicateSmartObjectIndependent(layer));
            Add("Replace Contents…", () => ReplaceSmartObjectRequested?.Invoke(layer, false));
            Add("Relink to File…", () => ReplaceSmartObjectRequested?.Invoke(layer, true));
            if (layer.SmartObject!.Link != null)
            {
                Add("Update from File", () => UpdateLinkedSmartObjectRequested?.Invoke(layer));
                Add("Embed Linked Object", () => current.SetSmartObjectLink(layer.SmartObject!, null));
                Add("Linked Source Info…", () => LinkedSourceInfoRequested?.Invoke(layer));
            }
        }
        menu.Items.Add(new Separator());

        Add(layer.Clipped ? "Release Clipping Mask" : "Create Clipping Mask", () => current.ToggleClippingMask(layer), current.CanClip(layer));
        Add("Group Selected Layers", current.GroupSelectedLayers);
        if (layer.IsGroup) Add("Ungroup", () => current.Ungroup(layer));
        Add("Move Out of Folder", () => current.MoveOutOfFolder(layer), current.Document.ParentOf(layer.Id) != null);
        Add(current.MergeTitle, current.MergeLayers, current.CanMerge);
        menu.Items.Add(new Separator());

        var addMask = new MenuItem { Header = Locale.T("Add Mask"), IsEnabled = layer.Mask == null };
        Add("Reveal All (White)", () => current.AddMask(layer), parent: addMask);
        Add("Hide All (Black)", () => current.AddMask(layer, hideAll: true), parent: addMask);
        menu.Items.Add(addMask);
        Add(layer.Mask is { } && !layer.MaskEnabled ? "Enable Mask" : "Disable Mask", () => current.SetMaskEnabled(layer, !layer.MaskEnabled), layer.Mask != null);
        Add(layer.MaskLinked ? "Unlink Mask" : "Link Mask", () => current.SetMaskLinked(layer, !layer.MaskLinked), layer.Mask != null);
        Add("Mask Properties…", () => EditMaskPropertiesRequested?.Invoke(layer), layer.Mask != null);
        Add("Delete Mask", () => current.DeleteMask(layer), layer.Mask != null);
        Add("Apply Mask", () => current.ApplyMask(layer), layer.Mask != null && layer.Pixels != null && !layer.IsSmartObject);
        Add("Select Mask", () => current.SelectLayerMask(layer), layer.Mask != null);
        if (layer.Pixels != null) Add("Select Pixels", () => current.SelectLayerPixels(layer));
        menu.Items.Add(new Separator());

        Add(layer.Visible ? "Hide Layer" : "Show Layer", () => current.SetVisible(layer, !layer.Visible));
    }

    // ---- Selection and drag-reorder -----------------------------------------------------------------------------

    private void RowPressed(Layer layer, Border row, PointerPressedEventArgs e)
    {
        if (session == null || e.Source is TextBox || (e.Source as Control)?.FindAncestorOfType<TextBox>() != null) return;
        // Selection can now leave the preceding row attached. Commit the draft explicitly
        // before changing the active layer instead of relying on its editor being detached.
        if (renaming != layer.Id) finishRename?.Invoke(true);
        var properties = e.GetCurrentPoint(row).Properties;
        if (properties.IsRightButtonPressed)
        {
            if (!session.Document.SelectedLayerIds.Contains(layer.Id)) session.SelectLayer(layer.Id);
            return;
        }
        if (!properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
        {
            // Selecting on the first click rebuilt the rows, so the control's own double-tap never sees both clicks.
            if (layer.IsAdjustment) EditAdjustmentRequested?.Invoke(layer);
            else if (layer.IsSmartObject && e.Source is Image) EditSmartObjectRequested?.Invoke(layer);
            else if (layer.Text != null && e.Source is Image) EditTextRequested?.Invoke(layer); // Double-click the thumbnail to edit, the name to rename.
            else { renaming = layer.Id; Rebuild(); }
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && session.CanClip(layer)) { session.ToggleClippingMask(layer); return; }
        var extend = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var range = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        pressed = layer;
        pressPoint = e.GetPosition(rows);
        dragging = false;
        if (session.SelectedEffect != null) { session.SelectedEffect = null; session.NotifyLayersChanged(); }
        var target = thumbnailTarget;
        thumbnailTarget = null;
        if (extend || range || !session.Document.SelectedLayerIds.Contains(layer.Id)) session.SelectLayer(layer.Id, extend, range,
            IsFiltering ? entries.Select(entry => entry.Layer.Id).ToArray() : null);
        if (target is { } mask && !extend && !range)
        {
            // Clicking a thumbnail chooses what painting affects: the layer's pixels or its mask.
            if (session.Document.ActiveLayerId != layer.Id) session.SelectLayer(layer.Id);
            session.EditingMask = mask && layer.Mask != null;
            session.NotifyLayersChanged();
        }
        if (rowFor.TryGetValue(layer.Id, out var rebuilt)) e.Pointer.Capture(rebuilt);
    }

    private void RowMoved(PointerEventArgs e)
    {
        if (pressed == null || session == null || IsFiltering) return;
        if (!e.GetCurrentPoint(rows).Properties.IsLeftButtonPressed) { EndRowDrag(); return; }
        var position = e.GetPosition(rows);
        if (!dragging && Math.Abs(position.Y - pressPoint.Y) < 6) return;
        dragging = true;
        dropTarget = null;
        dropLine.IsVisible = false;
        ClearDropHighlight();
        var row = LayerRowAt(position.Y);
        if (row?.Tag is not Layer target) return;
        var top = row.TranslatePoint(default, rows)!.Value.Y;
        var fraction = (position.Y - top) / Math.Max(1, row.Bounds.Height);
        var drop = target.IsGroup && fraction is > 0.3 and < 0.7 ? LayerDrop.Into : fraction < 0.5 ? LayerDrop.Above : LayerDrop.Below;
        if (drop == LayerDrop.Below && target.IsGroup && !target.Collapsed && target.Children.Count > 0) drop = LayerDrop.Into;
        dropTarget = (target, drop);
        dropLine.IsVisible = drop != LayerDrop.Into;
        dropLine.Margin = new Thickness(0, (drop == LayerDrop.Above ? top : top + row.Bounds.Height) - 1, 0, 0);
        if (drop == LayerDrop.Into)
        {
            dropHighlight = row;
            row.BorderBrush = Palette.Accent;
            row.BorderThickness = new Thickness(1);
        }
    }

    private Border? dropHighlight;
    private void ClearDropHighlight()
    {
        if (dropHighlight == null) return;
        dropHighlight.BorderBrush = null;
        dropHighlight.BorderThickness = new Thickness(0);
        dropHighlight = null;
    }

    private void EndRowDrag()
    {
        ClearDropHighlight();
        pressed = null;
        dragging = false;
        dropTarget = null;
        dropLine.IsVisible = false;
    }

    private void RowReleased(PointerReleasedEventArgs e)
    {
        // Taken before the capture is released: releasing it raises capture-lost, which ends an abandoned drag.
        ClearDropHighlight();
        var layer = pressed;
        pressed = null;
        var wasDragging = dragging;
        dragging = false;
        e.Pointer.Capture(null);
        dragging = wasDragging;
        dropLine.IsVisible = false;
        if (session == null || layer == null) return;
        if (!dragging)
        {
            // A plain click on one of several selected layers narrows the selection to it.
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && session.Document.SelectedLayerIds.Count > 1)
                session.SelectLayer(layer.Id);
            return;
        }
        dragging = false;
        if (dropTarget is { } drop)
        {
            var moving = session.SelectedRoots();
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) { session.DuplicateSelectedLayers(); moving = session.SelectedRoots(); }
            session.MoveLayers(moving, drop.Target, drop.Drop);
        }
        dropTarget = null;
    }
}
