using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Composa.Editing;
using Composa.AI;
using Composa.Model;
using Composa.Selections;
using SkiaSharp;
using TextAlignment = Composa.Model.TextAlignment;

namespace Composa.App;

public sealed partial class MainWindow
{
    private static ShapeKind? InspectorShapeKind(ShapeStyle? style) => style?.IsRectangle() == true ? ShapeKind.Rectangle : style?.Kind;
    private ShapeKind? optionsShapeKind;
    private bool optionsHaveWarp;
    private Action? refreshTransformFields;
    private bool transformRefreshPending;

    private void QueueTransformFields()
    {
        if (transformRefreshPending || refreshTransformFields == null) return;
        transformRefreshPending = true;
        RequestAnimationFrame(_ =>
        {
            transformRefreshPending = false;
            if (session?.Transform != null) refreshTransformFields?.Invoke();
        });
    }
    private bool optionsEditingText;
    private bool optionsEditingMask;
    private Button generativeExpandButton = null!;

    /// <summary>Rebuilds the bar under the tabs with the current tool's settings.</summary>
    private void RebuildOptions(bool updateLayout = true)
    {
        refreshOptions = null;
        refreshTransformFields = null;
        optionsHost.Height = double.NaN;
        optionsHost.MinHeight = 40;
        optionsShapeKind = InspectorShapeKind(session?.ActiveLayer?.Shape);
        optionsHaveWarp = session?.WarpEdit != null;
        optionsEditingText = session?.IsEditingText == true;
        optionsEditingMask = session?.IsEditingMask == true;
        ClearStaleLayoutProposal();
        optionsLayoutInspector = LayoutInspectorFor(session);
        var nextLayer = session?.ActiveLayerIdOrNull();
        if (nextLayer != optionsLayer) propertiesView.Offset = default;
        optionsLayer = nextLayer;
        generativeExpandButton.IsVisible = session?.Tool == Tool.Crop;
        propertiesView.Content = null;
        var propertiesPanel = dock.Section("Properties");
        var inspectorHeight = optionsLayoutInspector switch
        {
            LayoutInspector.Group => 264,
            LayoutInspector.Child when session?.ActiveLayer?.SupportsLayoutImageFrame == true => 304,
            _ when session?.ActiveLayer?.Text?.Path != null => 342,
            _ when session?.ActiveLayer?.Text != null => 306,
            _ when session is { IsEditingMask: false, ActiveLayer.Shape.Kind: ShapeKind.Path } => 520,
            _ when session is { IsEditingMask: false, ActiveLayer.Shape.Kind: ShapeKind.Line } => 474,
            _ when session is { ActiveLayer: null, IsEditingAlphaChannel: false } => 455,
            _ => 0
        };
        dock.SetExtraHeight(propertiesPanel, Math.Max(0, inspectorHeight - propertiesPanel.State.Height));
        if (session == null) { toolOptionsHost.Child = null; return; }
        var s = session;
        StackPanel? inspector = null;
        StackPanel? appearance = null;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = s.Tool is Tool.Move or Tool.Brush or Tool.Eraser or Tool.Pen ? 8 : 14, VerticalAlignment = VerticalAlignment.Center, Classes = { "options" } };
        void Add(params Control[] controls) => row.Children.AddRange(controls);
        void AddFindObject()
        {
            var find = Flat("Find object", SelectSubject);
            find.Name = "FindObjectInSelection";
            Locale.Tip(find, "Use the bounds of your current selection to find the object and refine its edges.");
            void Refresh() => find.IsEnabled = s.Selection != null && canvas.ObjectSelectionAvailable?.Invoke() == true;
            refreshOptions += Refresh;
            Refresh();
            Add(find);
        }
        Control Title(string text)
        {
            var label = Ui.Label(Locale.T(text), weight: Avalonia.Media.FontWeight.SemiBold);
            Locale.Tip(label, Hint(s));
            return label;
        }
        if (embeddedTabs.TryGetValue(s, out var embedded))
        {
            Add(Ui.TextButton(embedded.Source.Link != null ? "Save source ↗" : "Save contents ↗", () => _ = Save(s, false), accent: true),
                Ui.TextButton("Back to parent", () => SetSession(embedded.Parent)));
        }
        // A tool that comes in a group is picked from its rail button, so the bar only names the one in use.
        ComboBox StampMode()
        {
            var mode = Ui.Combo(new[] { Tool.CloneStamp, Tool.Patch }, s.Tool,
                tool => tool == Tool.Patch ? "Patch" : "Clone", SelectTool, 105);
            mode.Name = "StampMode";
            Locale.Tip(mode, "Clone from a source point or drag a selection to a donor");
            return mode;
        }
        string Chosen() => RailButton(s.Tool).Current!.Name;

        switch (s.Tool)
        {
            case Tool.Move:
                if (s.WarpEdit is { } warp)
                {
                    Add(Title("Warp"));
                    var grids = new[] { (Columns: 3, Rows: 3), (Columns: 4, Rows: 4), (Columns: 5, Rows: 5), (warp.Mesh.Columns, warp.Mesh.Rows) }.Distinct().ToArray();
                    var grid = Ui.Combo(grids, (warp.Mesh.Columns, warp.Mesh.Rows), v => $"{v.Columns} × {v.Rows}", v => canvas.ChangeWarpGrid(v.Columns, v.Rows), 94);
                    grid.Name = "WarpGrid"; grid.IsEnabled = warp.Mesh.IsIdentity;
                    Locale.Tip(grid, "Reset the deformation to choose a different grid. The source pixels stay intact.");
                    Add(grid, Flat("Reset", canvas.ResetWarp), Flat("Undo point", () => canvas.UndoWarpPoint()),
                        Ui.TextButton("Apply", () => canvas.FinishWarp(keep: true), accent: true), Flat("Cancel", () => canvas.FinishWarp(keep: false)),
                        Ui.Label(Locale.T("Drag nodes · Shift locks direction · Enter applies · Esc cancels"), Palette.Secondary));
                    break;
                }
                Add(Title("Move"));
                if (!s.IsEditingChannel && s.ActiveLayer?.Shape != null)
                {
                    appearance = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    if (!dock.Section("Properties").State.Visible) BuildShapeFields(appearance, row);
                    (dock.Section("Properties").State.Visible ? row : appearance).Children.Add(Flat("Edit points", () =>
                    {
                        canvas.EditVectorMask=false; SelectTool(Tool.Pen);
                    }));
                }
                if (s.IsEditingChannel) { BuildTransformFields(row); break; }
                var geometry = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Classes = { "options" } };
                BuildTransformFields(geometry);
                // Auto Select and the transform handles belong to Move; numeric geometry belongs to its inspector.
                foreach (var toggle in geometry.Children.OfType<CheckBox>().ToArray())
                { geometry.Children.Remove(toggle); row.Children.Add(toggle); }
                if (geometry.Children.Count > 0) inspector = geometry;
                if (appearance?.Children.Count == 0) appearance = null;
                var warpButton = Flat("Warp", StartWarp);
                warpButton.Name = "StartWarp";
                warpButton.Classes.Add("alignment");
                warpButton.IsEnabled = s.CanWarp;
                Add(warpButton);
                BuildAlignmentFields(row);
                break;
            case Tool.Brush or Tool.Eraser or Tool.SpotHealing or Tool.CloneStamp or Tool.HealingBrush or Tool.Smear:
                Add(Title(s.Tool switch { Tool.SpotHealing => "Spot Healing", Tool.Brush => "Brush", Tool.Eraser => "Eraser", Tool.CloneStamp => "Clone Stamp", Tool.HealingBrush => "Healing Brush", _ => Chosen() }));
                if (s.Tool == Tool.CloneStamp) Add(StampMode());
                Action? refreshTip = null;
                if (s.Tool is Tool.Brush or Tool.Eraser)
                {
                    Add(BrushLibrary(s, out var updateTip));
                    refreshTip = updateTip;
                    refreshOptions += updateTip;
                }
                var size = Ui.SliderField("Size", s.Brush.Size, 1, 500, v =>
                {
                    s.Brush = s.Brush with { Size = v };
                    refreshTip?.Invoke();
                    canvas.InvalidateBrushCursor();
                });
                var hardness = Ui.SliderField("Hardness", s.Brush.Hardness * 100, 0, 100, v =>
                {
                    s.Brush = s.Brush with { Hardness = v / 100 };
                    refreshTip?.Invoke();
                    canvas.InvalidateBrushCursor();
                });
                Add(size, hardness);
                if (s.Tool is Tool.Brush or Tool.Eraser)
                {
                    var flow = Ui.SliderField("Flow", s.Brush.Flow * 100, 1, 100, v => { s.Brush = s.Brush with { Flow = v / 100 }; refreshTip?.Invoke(); }, width: 100);
                    Add(flow);
                    refreshOptions += () => flow.Value = s.Brush.Flow * 100;
                }
                Action<double>? setOpacity = null;
                if (s.Tool != Tool.SpotHealing)
                {
                    var opacity = Ui.SliderField(s.Tool == Tool.Smear ? "Strength" : "Opacity", s.Brush.Opacity * 100, 1, 100, v => { s.Brush = s.Brush with { Opacity = v / 100 }; refreshTip?.Invoke(); });
                    if (s.Tool is Tool.Brush or Tool.Eraser)
                    {
                        opacity.UseIcon(Icons.Adjust);
                        opacity.Width = 76;
                    }
                    setOpacity = v => opacity.Value = v;
                    Add(opacity);
                }
                if (s.Tool is Tool.Brush or Tool.Eraser)
                {
                    var spacing = Ui.SliderField("Spacing", s.Brush.Spacing * 100, 1, 100, v => s.Brush = s.Brush with { Spacing = v / 100 }, width: 105);
                    var smoothing = Ui.SliderField("Smoothing", s.Brush.Smoothing, 0, 100, v => s.Brush = s.Brush with { Smoothing = v });
                    Add(spacing, smoothing, BrushDynamics(s, canvas.InvalidateBrushCursor));
                    refreshOptions += () => { spacing.Value = s.Brush.Spacing * 100; smoothing.Value = s.Brush.Smoothing; };
                }
                if (s.Tool is Tool.CloneStamp or Tool.HealingBrush)
                    Add(Ui.Check("Aligned", s.CloneAligned, v => s.CloneAligned = v), Ui.Check("Sample all layers", s.SampleAllLayers, v => s.SampleAllLayers = v));
                if (s.Tool == Tool.SpotHealing) Add(Ui.Check("Sample all layers", s.SampleAllLayers, v => s.SampleAllLayers = v));
                if (s.Tool == Tool.Smear && s.SmearMode == SmearMode.Liquify)
                {
                    var workspace = Ui.TextButton("Freeze / Reconstruct…", () => _ = Liquify()); workspace.Name = "LiquifyWorkspace";
                    workspace.IsEnabled = s.CanLiquify; Add(workspace); refreshOptions += () => workspace.IsEnabled = s.CanLiquify;
                }
                refreshOptions += () =>
                {
                    size.Value = Math.Min(500, s.Brush.Size);
                    hardness.Value = s.Brush.Hardness * 100;
                    setOpacity?.Invoke(s.Brush.Opacity * 100);
                };
                break;
            case Tool.Patch:
                Add(Title("Clone Stamp"), StampMode(), Ui.Label(Locale.T("Select with a marquee or lasso, then drag the selected area to a clean donor · Esc cancels"), Palette.Secondary));
                break;
            case Tool.Pen:
                var maskToggle = Ui.Check("Edit vector mask", canvas.EditVectorMask, v => { canvas.EditVectorMask = v; canvas.InvalidateCanvas(); });
                void RefreshMaskToggle()
                {
                    maskToggle.IsEnabled = s.ActiveLayer?.VectorMask != null;
                    maskToggle.IsChecked = canvas.EditVectorMask;
                }
                refreshOptions += RefreshMaskToggle;
                RefreshMaskToggle();
                Add(BuildPenModes(), Ui.Separator(true), Flat("Finish path", () => canvas.FinishPen()), Flat("Close path", () => canvas.FinishPen(closed: true)),
                    maskToggle,
                    Flat("Path to selection", () => { if (s.ActiveLayer is { } path) s.PathToSelection(path); }));
                if (s.ActiveLayer?.Shape != null && !dock.Section("Properties").State.Visible) BuildShapeFields(row);
                break;
            case Tool.Marquee or Tool.Lasso or Tool.Wand:
                Add(Title(Chosen()));
                if (s.Tool == Tool.Wand)
                {
                    if (s.WandMode == WandMode.Wand)
                        Add(Ui.SliderField("Tolerance", s.WandTolerance, 0, 255, v => s.WandTolerance = (int)v, width: 130), Ui.Check("Contiguous", s.WandContiguous, v => s.WandContiguous = v));
                    else
                    {
                        Add(ObjectSelectionModelMenu());
                        var edge = Ui.Number(s.ObjectEdgeOffset, -10, 10, v => s.ObjectEdgeOffset = (int)v, 1, "0", 52);
                        Locale.Tip(edge, "Positive values tighten the detected outline inward; negative values loosen it outward");
                        Add(Ui.Row(5, Ui.Scrub(Ui.Label(Locale.T("Edge"), Palette.Secondary), edge), edge, Ui.Label("px", Palette.Secondary)));
                    }
                    Add(Ui.Check("Sample all layers", s.SampleAllLayers, v => s.SampleAllLayers = v));
                    if (s.WandMode == WandMode.Object)
                    {
                        Add(SelectionModifyMenu(), Flat("Deselect", s.Deselect), Ui.Label(Locale.T("Click or draw a rectangle"), Palette.Secondary));
                        break;
                    }
                }
                else Add(Ui.SliderField("Feather", s.Feather, 0, 100, v => s.Feather = v));
                AddFindObject();
                Add(Ui.Separator());
                // Modify buttons with their amounts, as in the macOS tool bar; both need a selection.
                Control Modify(string title, Func<int> get, Action<int> set, int max, Action apply)
                {
                    var button = Flat(title, apply);
                    var amount = Ui.Number(get(), 1, max, v => set((int)v), 1, "0", 50);
                    var pair = Ui.Row(3, button, amount);
                    refreshOptions += () => button.IsEnabled = amount.IsEnabled = s.Selection != null;
                    return pair;
                }
                Add(Modify("Expand", () => s.SelectionExpandAmount, v => s.SelectionExpandAmount = v, 500, () => s.ExpandSelection(s.SelectionExpandAmount)),
                    Modify("Contract", () => s.SelectionContractAmount, v => s.SelectionContractAmount = v, 500, () => s.ContractSelection(s.SelectionContractAmount)),
                    Modify("Feather", () => s.SelectionFeatherAmount, v => s.SelectionFeatherAmount = v, 250, () => s.FeatherSelection(s.SelectionFeatherAmount)));
                Add(Ui.Separator(), Flat("Select All", s.SelectAll), Flat("Deselect", s.Deselect), Flat("Inverse", s.InvertSelection));
                refreshOptions?.Invoke();
                break;
            case Tool.SelectionBrush or Tool.RemoveObject:
                Add(Title(s.Tool == Tool.RemoveObject ? "Remove Object AI" : "Selection Brush"),
                    Ui.SliderField("Size", s.SelectionBrushSize, 1, 1000, value => s.SelectionBrushSize = value),
                    Ui.SliderField("Feather", s.SelectionBrushFeather, 0, 250, value => s.SelectionBrushFeather = value),
                    Ui.Label(s.Tool == Tool.RemoveObject ? "Release to remove with AI" : "Shift adds · Alt subtracts", Palette.Secondary));
                if (s.Tool == Tool.SelectionBrush)
                {
                    Add(Ui.Combo(new[] { Composa.Selections.SelectionMode.Add, Composa.Selections.SelectionMode.Subtract }, s.SelectionBrushMode,
                        value => value.ToString(), value => s.SelectionBrushMode = value, 100));
                    AddFindObject();
                }
                Add(Ui.Separator(), SelectionModifyMenu(), Flat("Deselect", s.Deselect));
                break;
            case Tool.Bucket:
                Add(Title("Paint Bucket"), Ui.SliderField("Tolerance", s.WandTolerance, 0, 255, value => s.WandTolerance = (int)value),
                    Ui.Label(Locale.T("Fills connected pixels with the foreground color"), Palette.Secondary));
                break;
            case Tool.Gradient:
                Add(Title("Gradient"),
                    Flat("Edit stops…", () => _ = EditGradientRamp()),
                    Flat("Use foreground / background", () => s.GradientRamp = null),
                    Ui.Combo(new[] { "Linear", "Radial" }, s.GradientRadial ? "Radial" : "Linear", Locale.T, v => s.GradientRadial = v == "Radial", 90),
                    Ui.Check("Foreground to transparent", s.GradientToTransparent, v => { s.GradientToTransparent = v; s.GradientRamp = null; UpdateStatus(); }));
                var gradientOpacity = Ui.SliderField("Opacity", s.GradientOpacity * 100, 1, 100, v => s.GradientOpacity = v / 100);
                Add(gradientOpacity);
                refreshOptions = () => gradientOpacity.Value = s.GradientOpacity * 100;
                break;
            case Tool.Shape:
                if (s.ActiveLayer?.Shape != null)
                {
                    Add(Title(Chosen()));
                    if (!dock.Section("Properties").State.Visible) BuildShapeFields(row);
                }
                else
                {
                    Add(Title(Chosen()));
                    if (s.ShapeKind == ShapeKind.Line) Add(Ui.SliderField("Width", s.ShapeLineWidth, 1, 100, v => s.ShapeLineWidth = v));
                    else if (s.ShapeKind is ShapeKind.Rectangle or ShapeKind.RoundedRectangle or ShapeKind.Polygon or ShapeKind.Star) Add(Ui.SliderField("Corner radius", s.ShapeCornerRadius, 0, 400, v => s.ShapeCornerRadius = v, width: 150));
                    Add(Ui.Label(Locale.Message(s.ShapeKind == ShapeKind.Line ? "Draws in the foreground color · Shift snaps to 45°" : "Fills with the foreground color"), Palette.Secondary));
                }
                break;
            case Tool.Text or Tool.TextOnPath or Tool.VerticalText:
                Add(Title(s.Tool == Tool.TextOnPath ? "Text on Path" : s.Tool == Tool.VerticalText ? "Vertical Type" : "Type"));
                if (s.ActiveLayer?.Text == null || !dock.Section("Properties").State.Visible) BuildTextFields(row);
                else if (s.IsEditingText)
                {
                    Add(Flat("Done", () => { s.FinishText(); canvas.Focus(); RebuildOptions(); }), Flat("Cancel", () => { s.CancelText(); canvas.Focus(); RebuildOptions(); }));
                }
                else Add(Flat("Edit Text", () => BeginTextEdit(s.ActiveLayer!)));
                break;
            case Tool.PromptRegion:
                Add(Title("AI region"), Flat(canvas.ShowPromptRegions ? "Hide regions" : "Show regions", TogglePromptRegions));
                break;
            case Tool.Crop:
                Add(Title("Crop"));
                var ratio = Ui.Combo(EditorSession.CropRatios, s.CropRatio, r => r, r => { s.CropRatio = r; canvas.ChangeCropRatio(); }, 100);
                Locale.Tip(ratio, "The shape the crop box keeps while you drag it");
                Add(Ui.Row(6, Ui.Label(Locale.T("Ratio"), Palette.Secondary), ratio));
                var readout = Ui.Label(Locale.T("Drag on the canvas to choose the area to keep"), Palette.Secondary);
                var apply = Ui.TextButton("Apply", canvas.ApplyCrop, accent: true);
                var cancel = Ui.TextButton("Cancel", canvas.CancelCrop);
                Add(readout, apply, cancel, Ui.Separator(), Flat("Trim transparent edges", () => { if (!s.Trim()) ShowProblem("Nothing to trim: no edge is transparent."); else canvas.Fit(); }));
                refreshOptions = () =>
                {
                    apply.IsEnabled = cancel.IsEnabled = canvas.HasCrop;
                    generativeExpandButton.IsEnabled = CanRunAi(AiTaskKind.GenerativeExpand);
                    readout.Text = canvas.CropRect is { } crop ? $"{Math.Round(crop.Width)} × {Math.Round(crop.Height)} px" : "Drag on the canvas to choose the area to keep";
                };
                refreshOptions();
                break;
            case Tool.Eyedropper:
                Add(Title("Eyedropper"), Ui.Label(Locale.T("Samples the merged image"), Palette.Secondary));
                break;
            case Tool.Hand or Tool.Zoom:
                Add(Title(s.Tool == Tool.Hand ? "Hand" : "Zoom"), Flat("Fit", canvas.Fit), Flat("100%", () => canvas.ZoomTo(1)), Flat("200%", () => canvas.ZoomTo(2)));
                break;
        }
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Classes = { "options" } };
        foreach (var group in new[] { row, inspector, appearance })
        {
            if (group == null) continue;
            foreach (var control in group.Children.ToArray())
            {
                group.Children.Remove(control);
                wrap.Children.Add(new Border { Padding = new Thickness(0, 3, 6, 3), Child = control, VerticalAlignment = VerticalAlignment.Center });
            }
        }
        toolOptionsHost.Child = wrap;
        BuildObjectProperties();
        if (updateLayout) UpdateLayout();
    }

    /// <summary>The selected live shape's properties, alongside the tool's controls.</summary>
    private void BuildShapeFields(StackPanel row, StackPanel? quick = null, ShapeStyle? initial = null, Action<ShapeStyle>? draftChanged = null, Func<Window>? owner = null, bool includeStrokeControls = false)
    {
        var s = session!;
        var id = s.ActiveLayer?.Id;
        var style = initial ?? s.ActiveLayer!.Shape!;
        var draft = initial;
        Action? refresh = null;
        var updating = false;
        Layer? Current() => draft == null && id is { } selected ? s.Document.Find(selected) : null;
        ShapeStyle? ReadStyle() => draft ?? Current()?.Shape;
        void Change(Func<ShapeStyle, ShapeStyle> change, bool merge = true)
        {
            if (updating || canvas.IsDragging || ReadStyle() is not {} live) return;
            if (draft == null && Current() is {} layer) s.ChangeShapeStyle(layer, change(live), merge: merge);
            else { draft = change(live).Clamped(); draftChanged?.Invoke(draft); Sync(); }
        }
        Controls.ColorSwatch Swatch(string label, Func<ShapeStyle, uint> get, Func<ShapeStyle, uint, ShapeStyle> set)
        {
            var swatch = new Controls.ColorSwatch { Width = 28, CornerRadius = new CornerRadius(3), Cursor = new Cursor(StandardCursorType.Hand) };
            Locale.Tip(swatch, label + " color");
            swatch.PointerPressed += async (_, e) =>
            {
                if (!e.GetCurrentPoint(swatch).Properties.IsLeftButtonPressed || canvas.IsDragging || ReadStyle() is not {} original) return;
                bool stroke = label == "Stroke";
                var result = original;
                if (draft == null) s.Begin("Shape Properties");
                bool accepted;
                try
                {
                    accepted = await Dialogs.VectorPaintDialog.Edit(owner?.Invoke() ?? this, label, get(original),
                        stroke ? original.StrokeGradient : original.FillGradient, (color, gradient) =>
                    {
                        var changed = set(original, color);
                        result = stroke ? changed with { StrokeGradient = gradient } : changed with { FillGradient = gradient };
                        if (Current() is {} current) s.ChangeShapeStyle(current, result, preview: true);
                    });
                }
                finally { if (draft == null) s.Cancel(); }
                if (accepted) Change(_ => result);
                refresh?.Invoke();
            };
            refresh += () =>
            {
                if (ReadStyle() is not {} live) return;
                var gradient = label == "Stroke" ? live.StrokeGradient : live.FillGradient;
                var color = new SKColor(get(live));
                if (gradient == null) swatch.Background = new SolidColorBrush(color.ToDisplay(s.Document.Profile, DisplayProfile));
                else
                {
                    GradientBrush brush = gradient.Radial ? new RadialGradientBrush() : new LinearGradientBrush
                        { StartPoint=new RelativePoint(0,.5,RelativeUnit.Relative), EndPoint=new RelativePoint(1,.5,RelativeUnit.Relative) };
                    brush.Opacity=color.Alpha/255.0;
                    foreach (var stop in gradient.Ramp.Colors.Select(x=>x.Position).Concat(gradient.Ramp.Opacities.Select(x=>x.Position)).Distinct().Order())
                        brush.GradientStops.Add(new GradientStop(gradient.Ramp.Sample(stop).ToDisplay(s.Document.Profile, DisplayProfile),stop));
                    swatch.Background=brush;
                }
            };
            return swatch;
        }
        Control PaintRow(string label,bool stroke)
        {
            bool Visible(ShapeStyle st) => stroke ? st.Stroke != null && st.StrokeEnabled : st.Kind == ShapeKind.Line ? st.StrokeEnabled : st.FillEnabled;
            uint Color(ShapeStyle st) => stroke ? st.Stroke ?? 0xff000000 : st.Fill;
            ShapeStyle SetColor(ShapeStyle st,uint color) => stroke
                ? st with { Stroke=color, StrokeEnabled=Visible(st) } : st with { Fill=color };
            VectorGradient? Gradient(ShapeStyle st) => stroke ? st.StrokeGradient : st.FillGradient;
            var swatch=Swatch(stroke ? "Stroke" : style.Kind==ShapeKind.Line ? "Line" : "Fill",Color,SetColor);
            swatch.Width=18; swatch.Height=18;
            var hex=new Controls.PaintValueField(label+"Hex",65,text=>
            {
                if(!Controls.PaintValueField.TryRgb(text,out var rgb)) return;
                Change(st=>
                {
                    if(Gradient(st) is not {} gradient) return SetColor(st,(Color(st)&0xff000000)|rgb);
                    var stops=gradient.Ramp.Colors.ToArray();
                    stops[0]=stops[0] with { Color=0xff000000|rgb };
                    var next=gradient with { Ramp=gradient.Ramp with { Colors=stops } };
                    return stroke ? st with { StrokeGradient=next } : st with { FillGradient=next };
                });
            });
            var opacity=new Controls.PaintValueField(label+"Opacity",28,text=>
            {
                if(Controls.PaintValueField.TryNumber(text,100,out var value))
                    Change(st=>SetColor(st,(Color(st)&0xffffff)|((uint)Math.Round(value*255/100)<<24)));
            });
            Locale.Tip(hex,label+" HEX · first color stop for gradients"); Locale.Tip(opacity,label+" opacity");
            var eye=Ui.IconButton(Icons.Eye,label+" visibility",()=>Change(st=>stroke
                ? st with { Stroke=Color(st),StrokeEnabled=!Visible(st) } : st.Kind == ShapeKind.Line ? st with { StrokeEnabled=!st.StrokeEnabled } : st with { FillEnabled=!Visible(st) }),14);
            eye.Name=label+"Visibility"; eye.Width=24; eye.Padding=new Thickness(2);
            var field=new Border { Background=Palette.PanelRaised,CornerRadius=new CornerRadius(4),Padding=new Thickness(4,0),
                Child=Ui.Row(1,swatch,Controls.PaintLayout.Caption("#"),hex,new Border { Width=1,Height=14,Background=Palette.Edge },opacity,Ui.Label("%",Palette.Secondary,11)) };
            bool? shown=null;
            refresh+=()=>
            {
                if(ReadStyle() is not {} st) return;
                var visible=Visible(st);
                if(shown!=visible) eye.Content=Icons.Create(visible ? Icons.Eye : Icons.EyeOff,14,Palette.Secondary);
                shown=visible;
                hex.Show($"{(Gradient(st)?.Ramp.Colors[0].Color ?? Color(st))&0xffffff:X6}");
                opacity.Show(Math.Round((Color(st)>>24)*100d/255).ToString("0"));
                field.Opacity=visible ? 1 : .65;
            };
            var caption = Ui.Label(Locale.T(label), Palette.Secondary);
            if (row.Orientation == Orientation.Horizontal) return Ui.Row(4, caption, field, eye);
            var paintRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,26"), ColumnSpacing = 6 };
            caption.VerticalAlignment = VerticalAlignment.Center;
            paintRow.Children.Add(caption); AddAt(paintRow, field, 1); AddAt(paintRow, eye, 2);
            return paintRow;
        }
        var paints=quick ?? row;
        if (style.Kind!=ShapeKind.Line) paints.Children.Add(PaintRow("Fill",false));
        paints.Children.Add(PaintRow("Stroke",style.Kind!=ShapeKind.Line));
        if (includeStrokeControls && style.Kind is ShapeKind.Line or ShapeKind.Path)
        {
            refresh += AddStrokeProperties(row, style, ReadStyle, Change);
        }
        else if (style.Kind == ShapeKind.Line)
        {
            var width = Ui.SliderField("Width", style.LineWidth, 1, 500, v => Change(st => st with { LineWidth = v }), width: 115);
            row.Children.Add(width);
            refresh += () => { if (ReadStyle() is { } live) width.Value = live.LineWidth; };
        }
        else
        {
            var width = Ui.SliderField("Stroke width", style.StrokeWidth, 0, 500, v => Change(st => st with { StrokeWidth = v }), width: 115);
            row.Children.Add(width);
            refresh += () =>
            {
                if (ReadStyle() is not { } live) return;
                width.Value = live.StrokeWidth;
            };
            if (style.IsRectangle() || style.IsRadial())
            {
                var radius = Ui.SliderField("Corner radius", style.RadiusAt(0), 0, DocumentLimits.MaxSide, v => Change(st => st.WithRadius(v)), width: 140);
                radius.DragUnitsPerPixel = 1;
                row.Children.Add(radius);
                refresh += () => { if (ReadStyle() is {} live) radius.Value = live.RadiusAt(0); };
                if (style.IsRectangle())
                {
                    var corners = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), RowSpacing = 6, ColumnSpacing = 8 };
                    string[] names = ["Top left", "Top right", "Bottom right", "Bottom left"];
                    for (int i=0; i<4; i++)
                    {
                        int index=i;
                        var number=Ui.Number(style.RadiusAt(i),0,DocumentLimits.MaxSide,v => Change(st => st.WithCorner(index,v)),width:46);
                        number.Width=double.NaN;
                        var cell = Ui.Column(4, Ui.Label(Locale.T(names[i]), Palette.Secondary, 11), number);
                        Grid.SetRow(cell, i < 2 ? 0 : 1);
                        AddAt(corners, cell, i is 1 or 2 ? 1 : 0);
                        refresh += () => { if (ReadStyle() is {} live) number.Value=(decimal)live.RadiusAt(index); };
                    }
                    row.Children.Add(new Expander { Header=Locale.T("Individual corners"), Content=corners, IsExpanded=style.Corners!=null });
                }
                if (style.IsRadial())
                {
                    var count = Ui.SliderField(style.Kind == ShapeKind.Star ? "Points" : "Sides", style.Points,3,64,v=>Change(st=>st with {Points=(int)v}));
                    row.Children.Add(count); refresh += () => { if (ReadStyle() is {} live) count.Value=live.Points; };
                    if (style.Kind == ShapeKind.Star) { var ratio=Ui.SliderField("Inner radius %",style.InnerRatio*100,5,95,v=>Change(st=>st with {InnerRatio=v/100})); row.Children.Add(ratio); refresh += () => { if(ReadStyle() is {} live) ratio.Value=live.InnerRatio*100; }; }
                }
            }
        }
        if (row.Orientation == Orientation.Vertical)
            foreach (var field in row.Children.OfType<Controls.SliderField>()) { field.Width=double.NaN; field.HorizontalAlignment=HorizontalAlignment.Stretch; }
        void Sync() { updating = true; try { refresh?.Invoke(); } finally { updating = false; } }
        if (initial == null) refreshOptions += Sync;
        Sync();
    }

    private async Task CreateShapeAt(SKPoint at)
    {
        if (session is not {} target) return;
        var style = target.CurrentShapeStyle;
        var body = new StackPanel { Width = 320, Spacing = 10 };
        double width = 120, height = style.Kind == ShapeKind.Line ? 0 : 120, opacity = 100;
        var w = Ui.Number(width, 1, DocumentLimits.MaxSide, v => width=v);
        var h = Ui.Number(height, style.Kind == ShapeKind.Line ? 0 : 1, DocumentLimits.MaxSide, v => height=v);
        w.Width = h.Width = double.NaN;
        body.Children.Add(PropertyPair(Ui.Column(4,Ui.Label(Locale.T("Width (px)"),Palette.Secondary),w),
            Ui.Column(4,Ui.Label(Locale.T("Height (px)"),Palette.Secondary),h)));
        var dialog = new Dialogs.DialogWindow(ShapeStyle.DisplayName(style.Kind), body, "Create");
        BuildShapeFields(body, initial:style, draftChanged:v=>style=v, owner:()=>dialog);
        var alpha=Ui.SliderField("Opacity",100,0,100,v=>opacity=v); alpha.Width=double.NaN; alpha.HorizontalAlignment=HorizontalAlignment.Stretch;
        body.Children.Add(alpha);
        if (!await dialog.Ask(this) || session != target) return;
        if (style.Kind == ShapeKind.Line) target.AddLine(at, new SKPoint(at.X+(float)width,at.Y+(float)height),style,opacity/100);
        else target.AddShape(style,SKRect.Create(at.X,at.Y,(float)width,(float)height),opacity/100);
    }

    private void BuildTextFields(StackPanel row, bool properties = false)
    {
        var s = session!;
        var updating = false;
        void Change(Func<TextStyle, TextStyle> change)
        {
            if (!updating) s.FormatText(change);
        }
        var style = s.InspectedTextStyle;
        var families = EditorSession.FontFamilies;
        var family = families.Contains(style.FontFamily) ? style.FontFamily : families.FirstOrDefault(f => f.Contains("Sans", StringComparison.OrdinalIgnoreCase)) ?? families.FirstOrDefault() ?? style.FontFamily;
        // A long font name is cut off rather than widening the bar.
        // While typing, the family and installed style land on the selected letters only, as the color does.
        void ChangeFace(Func<TextFace, TextFace> change) { if (!updating) s.SetTextFace(change); }
        var font = new ComboBox { Width = 190, MaxWidth = 190 };
        Locale.Tip(font, "Type to find a font · use the arrow keys to preview");
        var refreshFont = EnableFontSamples(font, s, families, f => ChangeFace(face => Composa.Text.FontCatalog.Closest(f, face).Face));
        var size = Ui.Number(s.CurrentTextSize, 1, 2000, v => { if (!updating) s.SetTextSize(v); }, 1, "0.#", 64);
        size.Name = "TextSize";
        Locale.Tip(size, Locale.T("Font size of the selected characters; select text on the canvas to change a fragment"));
        var faceChoices = Composa.Text.FontCatalog.ForFamily(family);
        var stylesFamily = family;
        var faceMenu = new ComboBox { Width = 145, MaxWidth = 145, ItemsSource = faceChoices.Select(c => c.Name).ToArray() };
        Locale.Tip(faceMenu, Locale.T("Font style"));
        faceMenu.SelectionChanged += (_, _) =>
        {
            if (!updating && faceMenu.SelectedIndex >= 0 && faceMenu.SelectedIndex < faceChoices.Count)
            {
                var selected = faceChoices[faceMenu.SelectedIndex].Face;
                ChangeFace(face => face with { Bold = selected.Bold, Italic = selected.Italic, FontStyle = selected.FontStyle });
            }
        };
        var swatch = new Controls.ColorSwatch { Width = 34, Height = 22, CornerRadius = new Avalonia.CornerRadius(3), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
        Locale.Tip(swatch, Locale.T("Text color"));
        swatch.PointerPressed += async (_, _) =>
        {
            // The picker's working color shows on the canvas as it changes. Text being typed takes it as any bar change;
            // a text layer that is not open for typing shows it inside an edit that is taken back when the dialog
            // closes, and the pick then goes through Change so it undoes as one step like any other bar change.
            // While typing, the color goes on the selected letters only (or all of them when nothing is selected).
            var original = s.CurrentTextStyle;
            var editing = s.TextEdit;
            var layer = editing == null && s.ActiveLayer is { Text: not null } active ? active : null;
            if (layer != null) s.Begin("Change Text Style");
            void Preview(SKColor color)
            {
                var opaque = (uint)color | 0xFF000000;
                if (editing != null && s.TextEdit == editing) s.SetTextColor(opaque);
                else if (layer != null) s.PreviewTextStyle(layer, st => st.WithColor(opaque, 0, 0));
            }
            var picked = await Dialogs.Prompts.Color(this, "Text Color", new SKColor(s.CurrentTextColor), editing != null || layer != null ? Preview : null);
            if (layer != null) s.Cancel();
            if (picked is not { } color)
            {
                if (editing != null && s.TextEdit == editing) s.RestoreTextColors(original);
                return;
            }
            if (!updating) s.SetTextColor((uint)color | 0xFF000000);
            // The text color is the foreground color: picking one in the Type bar moves the swatch too.
            s.Foreground = color;
            UpdateColors();
            refreshOptions?.Invoke();
        };
        var pathAlignments = new Dictionary<TextPathAlignment, ToggleButton>();
        var pathAlignmentRow = new Grid
        {
            Name = "TextPathAlignment",
            ColumnDefinitions = new ColumnDefinitions(properties ? "*,*,*,*" : "Auto,Auto,Auto,Auto"),
            ColumnSpacing = 1
        };
        Avalonia.Automation.AutomationProperties.SetName(pathAlignmentRow, Locale.T("Align to Path"));
        foreach (var (mode, icon, label) in new[]
        {
            (TextPathAlignment.Ascender, Icons.PathAscender, "Ascender"),
            (TextPathAlignment.Center, Icons.PathCenter, "Center"),
            (TextPathAlignment.Baseline, Icons.PathBaseline, "Path Baseline"),
            (TextPathAlignment.Descender, Icons.PathDescender, "Descender")
        })
        {
            var button = new ToggleButton
            {
                Classes = { "tool" }, Width = properties ? double.NaN : 30, Height = 28, MinWidth = 0,
                Padding = default, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center, Content = Icons.Create(icon, 18)
            };
            var caption = Locale.T(label);
            Locale.Tip(button, caption);
            Avalonia.Automation.AutomationProperties.SetName(button, caption);
            button.Click += (_, _) =>
            {
                s.ChangeTextStyle(st => st.Path is { } path ? st with { Path = path with { Alignment = mode } } : st);
                refreshOptions?.Invoke();
            };
            pathAlignments.Add(mode, button);
            AddAt(pathAlignmentRow, button, pathAlignments.Count - 1);
        }
        var editCurve = new Button { Classes = { "tool" }, Width = 34, Height = 28, MinWidth = 0,
            Padding = default, Content = Icons.Create(Icons.Pen, 16) };
        Locale.Tip(editCurve, "Edit Text Curve");
        Avalonia.Automation.AutomationProperties.SetName(editCurve, Locale.T("Edit Text Curve"));
        editCurve.Click += (_, _) => { SelectTool(Tool.Pen); canvas.Focus(); };
        Control pathControls = properties
            ? PropertyPair(pathAlignmentRow, new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Children = { editCurve } })
            : Ui.Row(5, pathAlignmentRow, editCurve);
        var alignments = new Dictionary<TextAlignment, ToggleButton>();
        var alignRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var (alignment, icon) in new[] { (TextAlignment.Left, Icons.AlignLeft), (TextAlignment.Center, Icons.AlignCenter), (TextAlignment.Right, Icons.AlignRight), (TextAlignment.Justify, Icons.Packed("justify")) })
        {
            var button = new ToggleButton { Classes = { "tool" }, Width = 30, Height = 26, Content = Icons.Create(icon, 15), IsChecked = style.Alignment == alignment };
            Locale.Tip(button, Locale.T(style.Path != null || style.IsVertical ? alignment switch
            {
                TextAlignment.Left => "Align to start", TextAlignment.Right => "Align to end", _ => "Align center"
            } : "Align " + alignment.ToString().ToLowerInvariant()));
            if (style.Path != null || style.IsVertical) button.IsVisible = alignment != TextAlignment.Justify;
            button.Click += (_, _) => { s.FormatText(st => st with { Alignment = alignment }, paragraph: true); refreshOptions?.Invoke(); };
            alignments[alignment] = button;
            alignRow.Children.Add(button);
        }
        var tracking = Ui.Number(style.Tracking, -100, 1000, v => Change(st => st with { Tracking = v }), 1, "0.##", 58);
        Locale.Tip(tracking, Locale.T("Letter spacing · px or % of each character’s size"));
        var leading = Ui.Number(style.Leading, 0, 5000, v => Change(st => st with { Leading = v }), 1, properties ? "0.#;-0.#;'Auto'" : "0", 58);
        Locale.Tip(leading, Locale.T("Baseline to baseline · 0 is Auto (120% of font size)"));
        var done = Ui.TextButton("Done", () => { s.FinishText(); canvas.Focus(); RebuildOptions(); UpdateStatus(); }, accent: true);
        var cancel = Ui.TextButton("Cancel", () => { s.CancelText(); canvas.Focus(); RebuildOptions(); UpdateStatus(); });
        var edit = Ui.TextButton("Edit Text", () => { if (s.ActiveLayer is { Text: not null } layer) BeginTextEdit(layer); });
        foreach (var button in new[] { done, cancel, edit }) button.MinWidth = 0;
        TextBlock? trackingUnit = null, leadingUnit = null;
        ToggleButton? underline = null, strike = null;
        font.Name = "FontFamilyPicker";
        if (properties)
        {
            font.Width = faceMenu.Width = double.NaN;
            font.MaxWidth = faceMenu.MaxWidth = double.PositiveInfinity;
            font.HorizontalAlignment = faceMenu.HorizontalAlignment = HorizontalAlignment.Stretch;
            font.Height = faceMenu.Height = 30;
            font.VerticalContentAlignment = faceMenu.VerticalContentAlignment = VerticalAlignment.Center;
            font.Padding = faceMenu.Padding = new Thickness(12, 0, 30, 0);
            font.TemplateApplied += (_, e) =>
            {
                if (e.NameScope.Find<TextBox>("PART_EditableTextBox") is { } editor)
                {
                    editor.VerticalContentAlignment = VerticalAlignment.Center;
                    editor.Padding = new Thickness(12, 0, 4, 0);
                }
            };
            row.Children.Add(font);
            var sizeUnit = PropertyUnit("Font size", out _);
            sizeUnit.Background = Brushes.Transparent;
            row.Children.Add(PropertyPair(faceMenu, PropertyNumber(size, Icons.FontSize, "Font size", Ui.Scrub(sizeUnit, size))));
            var lineUnit = PropertyUnit("Line height", out leadingUnit, () => Change(st =>
            {
                bool percent = !st.Type.PercentLeading;
                double leadingValue = percent ? st.Leading * 100 / st.Size : st.LineHeight;
                return st with { Leading = st.Leading == 0 ? 0 : leadingValue, Type = st.Type with { PercentLeading = percent } };
            }));
            var letterUnit = PropertyUnit("Letter spacing", out trackingUnit, () => Change(st =>
            {
                bool percent = !st.Type.PercentTracking;
                double value = percent ? st.Tracking * 100 / st.Size : st.Tracking * st.Size / 100;
                return st with { Tracking = value, Type = st.Type with { PercentTracking = percent } };
            }));
            row.Children.Add(PropertyPair(
                Ui.Column(4, Ui.Label(Locale.T("Line height"), Palette.Secondary, 11), PropertyNumber(leading, Icons.LineHeight, "Line height", lineUnit)),
                Ui.Column(4, Ui.Label(Locale.T("Letter spacing"), Palette.Secondary, 11), PropertyNumber(tracking, Icons.LetterSpacing, "Letter spacing", letterUnit))));
            ToggleButton Decoration(string icon, string name, Func<Typography,Typography> toggle)
            {
                var button = new ToggleButton { Classes = { "tool" }, Content = icon, Height = 28, Padding = default };
                button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Center;
                Locale.Tip(button, Locale.T(name)); Avalonia.Automation.AutomationProperties.SetName(button, name);
                button.Click += (_,_) => Change(st => st with { Type = toggle(st.Type) });
                return button;
            }
            underline = Decoration("U̲", "Underline", t => t with { Underline = !s.InspectedTextStyle.Type.Underline });
            strike = Decoration("S̶", "Strikethrough", t => t with { Strike = !s.InspectedTextStyle.Type.Strike });
            var decoration = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 2 };
            decoration.Children.Add(underline); AddAt(decoration,strike,1);
            swatch.Width = double.NaN; swatch.Height = 18; swatch.Margin = new Thickness(4,0);
            swatch.VerticalAlignment = VerticalAlignment.Center;
            AddAt(decoration,swatch,2);
            var alignmentGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), ColumnSpacing = 1 };
            foreach (var button in alignRow.Children.OfType<ToggleButton>().ToArray())
            {
                alignRow.Children.Remove(button); button.Width = double.NaN; button.Height = 28; button.MinWidth = 0; button.Padding = default;
                button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Center;
                AddAt(alignmentGrid,button,alignmentGrid.Children.Count);
            }
            row.Children.Add(PropertyPair(alignmentGrid,decoration));
            row.Children.Add(pathControls);
        }
        else
        {
            row.Children.AddRange([font, faceMenu, Ui.Row(4, size, Ui.Scrub(Ui.Label("px", Palette.Secondary), size)), swatch, alignRow,
                Ui.Row(5, Ui.Scrub(Ui.Label(Locale.T("Tracking"), Palette.Secondary), tracking), tracking),
                Ui.Row(5, Ui.Scrub(Ui.Label(Locale.T("Leading"), Palette.Secondary), leading), leading), Ui.Separator()]);
            row.Children.Add(pathControls);
            if (s.IsEditingText) row.Children.AddRange([done, cancel]);
            else { edit.IsEnabled = s.ActiveLayer?.Text != null; row.Children.Add(edit); }
        }
        void RefreshText()
        {
            var current = s.InspectedTextStyle;
            updating = true;
            pathControls.IsVisible = current.Path != null;
            foreach (var (mode, button) in pathAlignments) button.IsChecked = current.Path?.Alignment == mode;
            if (leadingUnit != null) leadingUnit.Text = current.Type.PercentLeading ? "%" : "px";
            if (trackingUnit != null) trackingUnit.Text = current.Type.PercentTracking ? "%" : "px";
            if (underline != null) underline.IsChecked = current.Type.Underline;
            if (strike != null) strike.IsChecked = current.Type.Strike;
            size.Value = (decimal)s.CurrentTextSize;
            tracking.Value = (decimal)current.Tracking;
            leading.Value = (decimal)current.Leading;
            var face = s.CurrentTextFace;
            // Selected letters in more than one family: the menu says so instead of naming one.
            var uniform = s.CurrentUniformTextFamily;
            refreshFont(uniform);
            if (stylesFamily != face.FontFamily)
            {
                stylesFamily = face.FontFamily;
                faceChoices = Composa.Text.FontCatalog.ForFamily(stylesFamily);
                faceMenu.ItemsSource = faceChoices.Select(c => c.Name).ToArray();
            }
            var uniformFace = s.CurrentUniformTextFace;
            faceMenu.IsEnabled = uniform != null;
            faceMenu.PlaceholderText = uniformFace == null ? Locale.T("(Multiple)") : Composa.Text.FontCatalog.Closest(face.FontFamily, face).Name;
            var faceIndex = uniformFace == null ? -1 : faceChoices.ToList().IndexOf(Composa.Text.FontCatalog.Closest(face.FontFamily, face));
            if (faceMenu.SelectedIndex != faceIndex) faceMenu.SelectedIndex = faceIndex;
            swatch.Background = new Avalonia.Media.SolidColorBrush(new SKColor(s.CurrentTextColor).ToDisplay(s.Document.Profile, DisplayProfile));
            foreach (var (alignment, button) in alignments) button.IsChecked = current.Alignment == alignment;
            updating = false;
        }
        refreshOptions += RefreshText;
        RefreshText();
    }

    private static Button Flat(string text, Action action)
    {
        var button = Ui.TextButton(text, action);
        button.Classes.Add("flat");
        button.Padding = new Thickness(8, 0);
        button.MinWidth = 0;
        return button;
    }

    private Control SelectionModifyMenu()
    {
        var s = session!;
        var button = Ui.TextButton("Modify ▾", () => { });
        button.Name = "SelectionModify"; button.MinWidth = 0;
        button.Click += (_, _) =>
        {
            var flyout = new MenuFlyout();
            void Item(string label, Func<Task> apply)
            {
                var item = new MenuItem { Header = Locale.T(label) };
                item.Click += (_, _) => _ = apply(); flyout.Items.Add(item);
            }
            Item("Expand…", () => ModifySelection("Expand Selection", "Expand by", () => s.SelectionExpandAmount, 500, v => { s.SelectionExpandAmount = v; s.ExpandSelection(v); }));
            Item("Contract…", () => ModifySelection("Contract Selection", "Contract by", () => s.SelectionContractAmount, 500, v => { s.SelectionContractAmount = v; s.ContractSelection(v); }));
            Item("Feather…", () => ModifySelection("Feather Selection", "Feather by", () => s.SelectionFeatherAmount, 250, v => { s.SelectionFeatherAmount = v; s.FeatherSelection(v); }));
            flyout.ShowAt(button);
        };
        refreshOptions += () => button.IsEnabled = s.Selection != null;
        button.IsEnabled = s.Selection != null;
        return button;
    }

    private void BuildTransformFields(StackPanel row)
    {
        var s = session!;
        var layer = s.IsEditingChannel ? s.ChannelControlLayer : s.ActiveLayer;
        var autoSelect = Ui.Check("Auto Select", canvas.AutoSelect, v => { canvas.AutoSelect = v; RememberToolSettings(); });
        autoSelect.IsEnabled = !s.IsEditingChannel;
        Locale.Tip(autoSelect, "Click a layer's pixels to select it. Off, a drag moves the current layer from anywhere; Ctrl-click still picks.");
        row.Children.Add(autoSelect);
        row.Children.Add(Ui.Check("Controls", canvas.ShowTransformControls, v => { canvas.ShowTransformControls = v; canvas.InvalidateCanvas(); RememberToolSettings(); }));
        if (s.IndependentMaskTarget != null) row.Children.Add(Ui.Label(Locale.T("Mask"), Palette.Secondary));
        if (layer == null || layer.Pixels == null && s.IndependentMaskTarget != layer)
        {
            if (layer != null) row.Children.Add(Ui.Label(Locale.T(layer.IsGroup ? "Moves every layer in the folder" : "This layer has no pixels"), Palette.Secondary));
            return;
        }
        var updating = false;
        TransformEdit? inspectorTransform = null;
        void FinishChannelInspector(bool keep)
        {
            var edit = inspectorTransform; inspectorTransform = null;
            if (edit == null || !ReferenceEquals(s.Transform, edit)) return;
            if (keep) s.CommitTransform(); else s.CancelTransform();
        }
        NumericUpDown Field(string label, Func<LayerTransform, double> get, Func<LayerTransform, double, LayerTransform> set, double min, double max, string format)
        {
            var box = Ui.Number(get(s.EditingControlTransform(layer)), min, max, v =>
            {
                var live = s.IsEditingChannel ? s.ChannelControlLayer : s.Document.Find(layer.Id);
                if (updating || live == null) return;
                var transform = set(s.EditingControlTransform(live), v);
                if (s.IsEditingChannel) { s.PreviewChannelControlTransform(transform); inspectorTransform = s.Transform; }
                else s.SetControlTransform(live, transform);
            }, 1, format, layer.Shape != null ? 55 : 74);
            box.LostFocus += (_, _) =>
            {
                var expected = inspectorTransform;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                { if (expected != null && inspectorTransform == expected && !row.IsKeyboardFocusWithin) FinishChannelInspector(keep: true); });
            };
            box.KeyUp += (_, e) => { if (e.Key == Key.Enter) FinishChannelInspector(keep: true); };
            box.AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Escape && inspectorTransform != null) { FinishChannelInspector(keep: false); e.Handled = true; } }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
            if (label == "∠") { box.Name = "TransformRotation"; Avalonia.Automation.AutomationProperties.SetName(box, "Rotation (degrees)"); Locale.Tip(box, Locale.T("Rotation (degrees)")); }
            var scrub = Ui.Scrub(Ui.Label(Locale.T(label), Palette.Secondary), box);
            scrub.AddHandler(PointerPressedEvent, (_, e) => { if (s.IsEditingChannel && e.GetCurrentPoint(scrub).Properties.IsLeftButtonPressed) { channelInspectorDragging = true; channelInspectorPointer = e.Pointer; } }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
            void EndScrub() { if (!channelInspectorDragging) return; channelInspectorDragging = false; channelInspectorPointer = null; if (!row.IsKeyboardFocusWithin) FinishChannelInspector(keep: true); }
            scrub.AddHandler(PointerReleasedEvent, (_, _) => EndScrub(), Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
            scrub.PointerCaptureLost += (_, _) => EndScrub();
            row.Children.Add(Ui.Row(5, scrub, box));
            return box;
        }
        var x = Field("X", t => t.X, (t, v) => t with { X = v }, -100000, 100000, "0.#");
        var y = Field("Y", t => t.Y, (t, v) => t with { Y = v }, -100000, 100000, "0.#");
        var w = Field("W", t => t.Width, (t, v) => t with { Width = Math.Max(1, v), Height = Math.Max(1, t.Height * v / Math.Max(1e-6, t.Width)) }, 1, 100000, "0.#");
        var h = Field("H", t => t.Height, (t, v) => t with { Height = Math.Max(1, v) }, 1, 100000, "0.#");
        var angle = Field("∠", t => t.Rotation, (t, v) => t with { Rotation = v }, -360, 360, "0.##");
        refreshTransformFields = () =>
        {
            var live = s.IsEditingChannel ? s.ChannelControlLayer : s.Document.Find(layer.Id);
            if (live == null) return;
            updating = true;
            var control = s.EditingControlTransform(live);
            x.Value = (decimal)control.X; y.Value = (decimal)control.Y;
            w.Value = (decimal)control.Width; h.Value = (decimal)control.Height;
            angle.Value = (decimal)control.Rotation;
            updating = false;
        };
        refreshOptions += refreshTransformFields;
    }
}
