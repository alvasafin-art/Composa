using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Composa.Editing;
using Composa.Model;
using Composa.Selections;
using SkiaSharp;
using TextAlignment = Composa.Model.TextAlignment;

namespace Composa.App;

public sealed partial class MainWindow
{
    private bool optionsHaveShape;

    /// <summary>Rebuilds the bar under the tabs with the current tool's settings.</summary>
    private void RebuildOptions()
    {
        refreshOptions = null;
        optionsHost.Height = 40;
        optionsHaveShape = session?.ActiveLayer?.Shape != null;
        if (session == null) { toolOptionsHost.Child = null; return; }
        var s = session;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = s.Tool is Tool.Move or Tool.Brush or Tool.Pen ? 8 : 14, VerticalAlignment = VerticalAlignment.Center, Classes = { "options" } };
        void Add(params Control[] controls) => row.Children.AddRange(controls);
        Control Title(string text) => Ui.Label(text, weight: Avalonia.Media.FontWeight.SemiBold);
        if (embeddedTabs.TryGetValue(s, out var embedded))
        {
            Add(Ui.TextButton("Save contents ↗", () => _ = Save(s, false), accent: true),
                Ui.TextButton("Back to parent", () => SetSession(embedded.Parent)));
        }
        // A tool that comes in a group is picked from its rail button, so the bar only names the one in use.
        string Chosen() => toolButtons[s.Tool].Current!.Name;

        switch (s.Tool)
        {
            case Tool.Move:
                Add(Title("Move"));
                BuildTransformFields(row);
                if (s.ActiveLayer?.Shape != null) BuildShapeFields(row);
                BuildAlignmentFields(row);
                break;
            case Tool.Brush or Tool.SpotHealing or Tool.CloneStamp or Tool.HealingBrush or Tool.Smear:
                Add(Title(s.Tool switch { Tool.SpotHealing => "Spot Healing", Tool.CloneStamp => "Clone Stamp", Tool.HealingBrush => "Healing Brush", _ => Chosen() }));
                var size = Ui.SliderField("Size", s.Brush.Size, 1, 500, v => s.Brush = s.Brush with { Size = v });
                var hardness = Ui.SliderField("Hardness", s.Brush.Hardness * 100, 0, 100, v => s.Brush = s.Brush with { Hardness = v / 100 });
                Add(size, hardness);
                Action<double>? setOpacity = null;
                if (s.Tool != Tool.SpotHealing)
                {
                    var opacity = Ui.SliderField(s.Tool == Tool.Smear ? "Strength" : "Opacity", s.Brush.Opacity * 100, 1, 100, v => s.Brush = s.Brush with { Opacity = v / 100 });
                    setOpacity = v => opacity.Value = v;
                    Add(opacity);
                }
                if (s.Tool == Tool.Brush)
                {
                    var flow = Ui.SliderField("Flow", s.Brush.Flow * 100, 1, 100, v => s.Brush = s.Brush with { Flow = v / 100 }, width: 100);
                    var spacing = Ui.SliderField("Spacing", s.Brush.Spacing * 100, 1, 100, v => s.Brush = s.Brush with { Spacing = v / 100 }, width: 105);
                    Add(flow, spacing);
                    var presets = new[] { new Composa.Painting.BrushPreset("Custom", s.Brush) }.Concat(Composa.Painting.BrushPresets.All)
                        .Concat(settings.BrushPresets.Select(p => new Composa.Painting.BrushPreset(p.Key, p.Value))).ToArray();
                    Add(Ui.Combo(presets, presets[0], p => p.Name,
                        p => { if (p.Name != "Custom") { s.Brush = p.Settings with { Size = s.Brush.Size, Opacity = s.Brush.Opacity }; RebuildOptions(); } }, 130),
                        BrushDynamicsMenu(s));
                    refreshOptions += () => { flow.Value = s.Brush.Flow * 100; spacing.Value = s.Brush.Spacing * 100; };
                }
                if (s.Tool is Tool.CloneStamp or Tool.HealingBrush)
                    Add(Ui.Check("Aligned", s.CloneAligned, v => s.CloneAligned = v), Ui.Check("Sample all layers", s.SampleAllLayers, v => s.SampleAllLayers = v));
                if (s.Tool == Tool.SpotHealing) Add(Ui.Check("Sample all layers", s.SampleAllLayers, v => s.SampleAllLayers = v));
                refreshOptions += () =>
                {
                    size.Value = Math.Min(500, s.Brush.Size);
                    hardness.Value = s.Brush.Hardness * 100;
                    setOpacity?.Invoke(s.Brush.Opacity * 100);
                };
                break;
            case Tool.Patch:
                Add(Title("Patch"), Ui.Label("Select with a marquee or lasso, then drag the selected area to a clean donor · Esc cancels", Palette.Secondary));
                break;
            case Tool.Pen:
                var penTitle = Title("Pen");
                ToolTip.SetTip(penTitle, "Shift-click curve adds a node · Delete removes selected node · Alt breaks handles · Ctrl starts a new path");
                Add(penTitle, Flat("Finish path", () => canvas.FinishPen()), Flat("Close path", () => canvas.FinishPen(closed: true)),
                    Ui.Check("Edit vector mask", canvas.EditVectorMask, v => { canvas.EditVectorMask = v; canvas.InvalidateVisual(); }),
                    Flat("Path to selection", () => { if (s.ActiveLayer is { } path) s.PathToSelection(path); }));
                if (s.ActiveLayer?.Shape?.Path != null) BuildShapeFields(row);
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
                        ToolTip.SetTip(edge, "Positive values tighten the detected outline inward; negative values loosen it outward");
                        Add(Ui.Row(5, Ui.Scrub(Ui.Label("Edge", Palette.Secondary), edge), edge, Ui.Label("px", Palette.Secondary)));
                    }
                    Add(Ui.Check("Sample all layers", s.SampleAllLayers, v => s.SampleAllLayers = v));
                }
                else Add(Ui.SliderField("Feather", s.Feather, 0, 100, v => s.Feather = v));
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
                if (s.Tool == Tool.SelectionBrush) Add(Ui.Combo(new[] { Composa.Selections.SelectionMode.Add, Composa.Selections.SelectionMode.Subtract }, s.SelectionBrushMode,
                    value => value.ToString(), value => s.SelectionBrushMode = value, 100));
                Add(Ui.Separator(), SelectionModifyMenu(), Flat("Deselect", s.Deselect));
                break;
            case Tool.ObjectSelectionAi:
                Add(Title("Object Selection AI"), ObjectSelectionModelMenu(),
                    Ui.Check("Sample all layers", s.SampleAllLayers, v => s.SampleAllLayers = v),
                    SelectionModifyMenu(), Ui.Label("Click or draw a rectangle · Shift adds · Alt subtracts", Palette.Secondary));
                break;
            case Tool.Bucket:
                Add(Title("Paint Bucket"), Ui.SliderField("Tolerance", s.WandTolerance, 0, 255, value => s.WandTolerance = (int)value),
                    Ui.Label("Fills connected pixels with the foreground color", Palette.Secondary));
                break;
            case Tool.Gradient:
                Add(Title("Gradient"),
                    Flat("Edit stops…", () => _ = EditGradientRamp()),
                    Flat("Use foreground / background", () => s.GradientRamp = null),
                    Ui.Combo(new[] { "Linear", "Radial" }, s.GradientRadial ? "Radial" : "Linear", v => v, v => s.GradientRadial = v == "Radial", 90),
                    Ui.Check("Foreground to transparent", s.GradientToTransparent, v => { s.GradientToTransparent = v; s.GradientRamp = null; UpdateStatus(); }));
                var gradientOpacity = Ui.SliderField("Opacity", s.GradientOpacity * 100, 1, 100, v => s.GradientOpacity = v / 100);
                Add(gradientOpacity);
                refreshOptions = () => gradientOpacity.Value = s.GradientOpacity * 100;
                break;
            case Tool.Shape:
                if (s.ActiveLayer?.Shape is { } shape)
                {
                    Add(Title(ShapeStyle.DisplayName(shape.Kind)));
                    BuildShapeFields(row);
                }
                else
                {
                    Add(Title(Chosen()));
                    if (s.ShapeKind == ShapeKind.Line) Add(Ui.SliderField("Width", s.ShapeLineWidth, 1, 100, v => s.ShapeLineWidth = v));
                    else if (s.ShapeKind == ShapeKind.RoundedRectangle) Add(Ui.SliderField("Corner radius", s.ShapeCornerRadius, 0, 400, v => s.ShapeCornerRadius = v, width: 150));
                    Add(Ui.Label(s.ShapeKind == ShapeKind.Line ? "Draws in the foreground color · Shift snaps to 45°" : "Fills with the foreground color", Palette.Secondary));
                }
                break;
            case Tool.Text:
                Add(Title("Type"));
                BuildTextFields(row);
                break;
            case Tool.Crop:
                Add(Title("Crop"));
                var ratio = Ui.Combo(EditorSession.CropRatios, s.CropRatio, r => r, r => { s.CropRatio = r; canvas.ChangeCropRatio(); }, 100);
                ToolTip.SetTip(ratio, "The shape the crop box keeps while you drag it");
                Add(Ui.Row(6, Ui.Label("Ratio", Palette.Secondary), ratio));
                var readout = Ui.Label("Drag on the canvas to choose the area to keep", Palette.Secondary);
                var apply = Ui.TextButton("Apply", canvas.ApplyCrop, accent: true);
                var cancel = Ui.TextButton("Cancel", canvas.CancelCrop);
                Add(readout, apply, cancel, Ui.Separator(), Flat("Trim transparent edges", () => { if (!s.Trim()) ShowProblem("Nothing to trim: no edge is transparent."); else canvas.Fit(); }));
                refreshOptions = () =>
                {
                    apply.IsEnabled = cancel.IsEnabled = canvas.HasCrop;
                    readout.Text = canvas.CropRect is { } crop ? $"{Math.Round(crop.Width)} × {Math.Round(crop.Height)} px" : "Drag on the canvas to choose the area to keep";
                };
                refreshOptions();
                break;
            case Tool.Eyedropper:
                Add(Title("Eyedropper"), Ui.Label("Samples the merged image", Palette.Secondary));
                break;
            case Tool.Hand or Tool.Zoom:
                Add(Title(s.Tool == Tool.Hand ? "Hand" : "Zoom"), Flat("Fit", canvas.Fit), Flat("100%", () => canvas.ZoomTo(1)), Flat("200%", () => canvas.ZoomTo(2)));
                break;
        }
        toolOptionsHost.Child = new ScrollViewer { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        UpdateLayout();
    }

    /// <summary>The selected live shape's properties, alongside the tool's controls.</summary>
    private void BuildShapeFields(StackPanel row)
    {
        var s = session!;
        var id = s.ActiveLayer!.Id;
        var style = s.ActiveLayer.Shape!;
        var updating = false;
        var strokeColor = style.Stroke ?? 0xFF000000;
        Layer? Current() => s.Document.Find(id);
        void Change(Func<ShapeStyle, ShapeStyle> change)
        {
            if (updating || canvas.IsDragging || Current() is not { Shape: { } live } layer) return;
            s.ChangeShapeStyle(layer, change(live), merge: true);
        }
        Border Swatch(string label, Func<ShapeStyle, uint> get, Func<ShapeStyle, uint, ShapeStyle> set)
        {
            var swatch = new Border { Width = 28, CornerRadius = new CornerRadius(3), BorderBrush = Brushes.White, BorderThickness = new Thickness(1), Cursor = new Cursor(StandardCursorType.Hand) };
            ToolTip.SetTip(swatch, label + " color");
            swatch.PointerPressed += async (_, e) =>
            {
                if (!e.GetCurrentPoint(swatch).Properties.IsLeftButtonPressed || canvas.IsDragging || Current() is not { Shape: { } original } layer) return;
                s.Begin("Shape Properties");
                SKColor? picked;
                try
                {
                    picked = await Dialogs.Prompts.Color(this, label + " Color", new SKColor(get(original)), color =>
                    {
                        if (Current() is { Shape: { } live } current) s.ChangeShapeStyle(current, set(live, (uint)color | 0xFF000000), preview: true);
                    });
                }
                finally { s.Cancel(); }
                if (picked is { } color) Change(st => set(st, (uint)color | 0xFF000000));
                refreshOptions?.Invoke();
            };
            refreshOptions += () => { if (Current()?.Shape is { } live) swatch.Background = new SolidColorBrush(new SKColor(get(live)).ToAvalonia()); };
            return swatch;
        }
        row.Children.Add(Ui.Separator());
        if (style.Kind == ShapeKind.Line)
        {
            row.Children.Add(Ui.Row(5, Ui.Label("Color", Palette.Secondary), Swatch("Line", st => st.Fill, (st, c) => st with { Fill = c })));
            var width = Ui.SliderField("Width", style.LineWidth, 1, 500, v => Change(st => st with { LineWidth = v }), width: 115);
            row.Children.Add(width);
            refreshOptions += () => { if (Current()?.Shape is { } live) width.Value = live.LineWidth; };
        }
        else
        {
            var fill = Ui.Check("Fill", style.FillEnabled, v => Change(st => st with { FillEnabled = v }));
            var stroke = Ui.Check("Stroke", style.Stroke != null, v => Change(st => st with { Stroke = v ? strokeColor : null }));
            row.Children.Add(Ui.Row(5, fill, Swatch("Fill", st => st.Fill, (st, c) => st with { Fill = c, FillEnabled = true })));
            row.Children.Add(Ui.Row(5, stroke, Swatch("Stroke", st => st.Stroke ?? strokeColor, (st, c) => st with { Stroke = c })));
            var width = Ui.SliderField("Stroke width", style.StrokeWidth, 0, 500, v => Change(st => st with { StrokeWidth = v }), width: 115);
            row.Children.Add(width);
            refreshOptions += () =>
            {
                if (Current()?.Shape is not { } live) return;
                updating = true;
                fill.IsChecked = live.FillEnabled; stroke.IsChecked = live.Stroke != null;
                if (live.Stroke is { } color) strokeColor = color;
                width.Value = live.StrokeWidth;
                updating = false;
            };
            if (style.Kind == ShapeKind.RoundedRectangle)
            {
                var radius = Ui.SliderField("Corner radius", style.CornerRadius, 0, DocumentLimits.MaxSide, v => Change(st => st with { CornerRadius = v }), width: 130);
                row.Children.Add(radius);
                refreshOptions += () => { if (Current()?.Shape is { } live) radius.Value = live.CornerRadius; };
            }
        }
        refreshOptions?.Invoke();
    }

    /// <summary>The Type bar: font, size, style, color, alignment and spacing for the text being typed (or the next text).</summary>
    private void BuildTextFields(StackPanel row)
    {
        var s = session!;
        var updating = false;
        void Change(Func<TextStyle, TextStyle> change)
        {
            if (!updating) s.ChangeTextStyle(change);
        }
        var style = s.CurrentTextStyle;
        var families = EditorSession.FontFamilies;
        var family = families.Contains(style.FontFamily) ? style.FontFamily : families.FirstOrDefault(f => f.Contains("Sans", StringComparison.OrdinalIgnoreCase)) ?? families.FirstOrDefault() ?? style.FontFamily;
        // A long font name is cut off rather than widening the bar.
        // While typing, the family and installed style land on the selected letters only, as the color does.
        void ChangeFace(Func<TextFace, TextFace> change) { if (!updating) s.SetTextFace(change); }
        var font = Ui.Combo(families, family, f => f, f => ChangeFace(face => Composa.Text.FontCatalog.Closest(f, face).Face), 190);
        font.MaxWidth = 190;
        var size = Ui.Number(s.CurrentTextSize, 1, 2000, v => { if (!updating) s.SetTextSize(v); }, 1, "0.#", 64);
        size.Name = "TextSize";
        ToolTip.SetTip(size, "Font size of the selected characters; select text on the canvas to change a fragment");
        var faceChoices = Composa.Text.FontCatalog.ForFamily(family);
        var stylesFamily = family;
        var faceMenu = new ComboBox { Width = 145, MaxWidth = 145, ItemsSource = faceChoices.Select(c => c.Name).ToArray() };
        ToolTip.SetTip(faceMenu, "Font style");
        faceMenu.SelectionChanged += (_, _) =>
        {
            if (!updating && faceMenu.SelectedIndex >= 0 && faceMenu.SelectedIndex < faceChoices.Count)
            {
                var selected = faceChoices[faceMenu.SelectedIndex].Face;
                ChangeFace(face => face with { Bold = selected.Bold, Italic = selected.Italic, FontStyle = selected.FontStyle });
            }
        };
        var swatch = new Border { Width = 34, Height = 22, CornerRadius = new Avalonia.CornerRadius(3), BorderBrush = Avalonia.Media.Brushes.White, BorderThickness = new Avalonia.Thickness(1), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
        ToolTip.SetTip(swatch, "Text color");
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
        var alignments = new Dictionary<TextAlignment, ToggleButton>();
        var alignRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var (alignment, icon) in new[] { (TextAlignment.Left, Icons.AlignLeft), (TextAlignment.Center, Icons.AlignCenter), (TextAlignment.Right, Icons.AlignRight) })
        {
            var button = new ToggleButton { Classes = { "tool" }, Width = 30, Height = 26, Content = Icons.Create(icon, 15), IsChecked = style.Alignment == alignment };
            ToolTip.SetTip(button, "Align " + alignment.ToString().ToLowerInvariant());
            button.Click += (_, _) => { Change(st => st with { Alignment = alignment }); refreshOptions?.Invoke(); };
            alignments[alignment] = button;
            alignRow.Children.Add(button);
        }
        var tracking = Ui.Number(style.Tracking, -100, 1000, v => Change(st => st with { Tracking = v }), 1, "0", 58);
        ToolTip.SetTip(tracking, "Tracking: extra space after every character, in pixels");
        var leading = Ui.Number(style.Leading, 0, 5000, v => Change(st => st with { Leading = v }), 1, "0", 58);
        ToolTip.SetTip(leading, "Leading: line height baseline to baseline, in pixels. 0 is Auto: 120% of the size");
        var done = Ui.TextButton("Done", () => { s.FinishText(); canvas.Focus(); RebuildOptions(); UpdateStatus(); }, accent: true);
        var cancel = Ui.TextButton("Cancel", () => { s.CancelText(); canvas.Focus(); RebuildOptions(); UpdateStatus(); });
        var edit = Ui.TextButton("Edit Text", () => { if (s.ActiveLayer is { Text: not null } layer) BeginTextEdit(layer); });
        foreach (var button in new[] { done, cancel, edit }) button.MinWidth = 0;
        row.Children.AddRange([font, faceMenu, Ui.Row(4, size, Ui.Scrub(Ui.Label("px", Palette.Secondary), size)), swatch, alignRow,
            Ui.Row(5, Ui.Scrub(Ui.Label("Tracking", Palette.Secondary), tracking), tracking), Ui.Row(5, Ui.Scrub(Ui.Label("Leading", Palette.Secondary), leading), leading), Ui.Separator()]);
        if (s.IsEditingText) row.Children.AddRange([done, cancel]);
        else { edit.IsEnabled = s.ActiveLayer?.Text != null; row.Children.Add(edit); }
        refreshOptions = () =>
        {
            var current = s.CurrentTextStyle;
            updating = true;
            size.Value = (decimal)s.CurrentTextSize;
            tracking.Value = (decimal)current.Tracking;
            leading.Value = (decimal)current.Leading;
            var face = s.CurrentTextFace;
            // Selected letters in more than one family: the menu says so instead of naming one.
            var uniform = s.CurrentUniformTextFamily;
            var index = uniform == null ? -1 : families.ToList().IndexOf(uniform);
            font.PlaceholderText = uniform ?? "(Multiple)";
            if (font.SelectedIndex != index) font.SelectedIndex = index;
            if (stylesFamily != face.FontFamily)
            {
                stylesFamily = face.FontFamily;
                faceChoices = Composa.Text.FontCatalog.ForFamily(stylesFamily);
                faceMenu.ItemsSource = faceChoices.Select(c => c.Name).ToArray();
            }
            var uniformFace = s.CurrentUniformTextFace;
            faceMenu.IsEnabled = uniform != null;
            faceMenu.PlaceholderText = uniformFace == null ? "(Multiple)" : Composa.Text.FontCatalog.Closest(face.FontFamily, face).Name;
            var faceIndex = uniformFace == null ? -1 : faceChoices.ToList().IndexOf(Composa.Text.FontCatalog.Closest(face.FontFamily, face));
            if (faceMenu.SelectedIndex != faceIndex) faceMenu.SelectedIndex = faceIndex;
            swatch.Background = new Avalonia.Media.SolidColorBrush(new SKColor(s.CurrentTextColor).ToAvalonia());
            foreach (var (alignment, button) in alignments) button.IsChecked = current.Alignment == alignment;
            updating = false;
        };
        refreshOptions();
    }

    private static Button Flat(string text, Action action)
    {
        var button = Ui.TextButton(text, action);
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
                var item = new MenuItem { Header = label };
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
        var layer = s.ActiveLayer;
        var autoSelect = Ui.Check("Auto Select", canvas.AutoSelect, v => { canvas.AutoSelect = v; RememberToolSettings(); });
        ToolTip.SetTip(autoSelect, "Click a layer's pixels to select it. Off, a drag moves the current layer from anywhere; Ctrl-click still picks.");
        row.Children.Add(autoSelect);
        row.Children.Add(Ui.Check("Controls", canvas.ShowTransformControls, v => { canvas.ShowTransformControls = v; canvas.InvalidateVisual(); RememberToolSettings(); }));
        if (layer?.Pixels == null)
        {
            row.Children.Add(Ui.Label(layer == null ? "No layer selected" : layer.IsGroup ? "Moves every layer in the folder" : "This layer has no pixels", Palette.Secondary));
            return;
        }
        var updating = false;
        NumericUpDown Field(string label, Func<LayerTransform, double> get, Func<LayerTransform, double, LayerTransform> set, double min, double max, string format)
        {
            var box = Ui.Number(get(layer.ControlTransform), min, max, v =>
            {
                if (updating || s.Document.Find(layer.Id) is not { } live) return;
                s.SetControlTransform(live, set(live.ControlTransform, v));
            }, 1, format, layer.Shape != null ? 55 : 74);
            row.Children.Add(Ui.Row(5, Ui.Scrub(Ui.Label(label, Palette.Secondary), box), box));
            return box;
        }
        var x = Field("X", t => t.X, (t, v) => t with { X = v }, -100000, 100000, "0.#");
        var y = Field("Y", t => t.Y, (t, v) => t with { Y = v }, -100000, 100000, "0.#");
        var w = Field("W", t => t.Width, (t, v) => t with { Width = Math.Max(1, v), Height = Math.Max(1, t.Height * v / Math.Max(1e-6, t.Width)) }, 1, 100000, "0.#");
        var h = Field("H", t => t.Height, (t, v) => t with { Height = Math.Max(1, v) }, 1, 100000, "0.#");
        var angle = Field("∠", t => t.Rotation, (t, v) => t with { Rotation = v }, -360, 360, "0.##");
        refreshOptions = () =>
        {
            if (s.Document.Find(layer.Id) is not { } live) return;
            updating = true;
            x.Value = (decimal)live.ControlTransform.X; y.Value = (decimal)live.ControlTransform.Y;
            w.Value = (decimal)live.ControlTransform.Width; h.Value = (decimal)live.ControlTransform.Height;
            angle.Value = (decimal)live.ControlTransform.Rotation;
            updating = false;
        };
    }
}
