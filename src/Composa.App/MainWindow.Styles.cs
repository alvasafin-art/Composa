using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.Model;
using Composa.Text;
using SkiaSharp;

namespace Composa.App;

public sealed partial class MainWindow
{
    private sealed record StyleChoice(Guid? Id, string Name, LayerStyleDefinition? Definition);
    private static readonly (StyleFields Field, string Caption)[] RevertibleStyleFields =
    [
        (StyleFields.Typeface, "Typeface"), (StyleFields.Size, "Font size"), (StyleFields.Color, "Text Color"),
        (StyleFields.Tracking, "Letter spacing"), (StyleFields.Leading, "Line height"), (StyleFields.ParagraphSpacing, "Paragraph spacing"),
        (StyleFields.Opacity, "Opacity"), (StyleFields.Fill, "Fill"), (StyleFields.FillVisible, "Show fill"),
        (StyleFields.Stroke, "Stroke"), (StyleFields.StrokeVisible, "Show stroke"), (StyleFields.StrokeWidth, "Stroke width"),
        .. Enum.GetValues<LayerEffectKind>().Select(kind => (AppearanceStyleDefinition.EffectField(kind), LayerEffects.DisplayName(kind)))
    ];

    private void AddStylePicker(StackPanel body, EditorSession target, Func<Layer?> current, LayerStyleKind kind)
    {
        if (current() is not { } original || !EditorSession.SupportsStyle(original, kind)) return;
        var caption = kind == LayerStyleKind.Text ? "Text style" : "Appearance style";
        var picker = new ComboBox
        {
            Name = kind == LayerStyleKind.Text ? "SharedTextStylePicker" : "SharedAppearanceStylePicker",
            ItemTemplate = new FuncDataTemplate<StyleChoice>((item, _) => BuildStyleChoice(target, item), supportsRecycling: false),
            MinWidth = 0
        };
        AutomationProperties.SetName(picker, Locale.T(caption));
        var menu = new ContextMenu();
        var more = Ui.IconButton(Icons.MoreActions, caption, () => { });
        more.Name = kind == LayerStyleKind.Text ? "SharedTextStyleActions" : "SharedAppearanceStyleActions";
        more.ContextMenu = menu;
        more.Classes.Remove("flat");
        more.Width = more.Height = 30;
        more.MinWidth = more.MinHeight = 0;
        more.CornerRadius = new CornerRadius(5);
        more.VerticalAlignment = VerticalAlignment.Bottom;
        more.Click += (_, _) =>
        {
            if (current() is not { } layer) return;
            menu.Items.Clear();
            var style = target.LinkedStyle(layer, kind);
            var canEdit = target.CanEditLayerStyle(layer, kind);
            if (style != null && canEdit)
            {
                Add("Edit Only This Object", () => Dispatcher.UIThread.Post(() =>
                {
                    var name = kind == LayerStyleKind.Text ? "TextSize" : "LayerPropertyOpacity";
                    body.GetVisualDescendants().OfType<NumericUpDown>().FirstOrDefault(number => number.Name == name)?.Focus();
                }));
                Add("Edit Style…", () => _ = EditSharedStyle(target, layer, style));
                var overrides = target.StyleOverrides(layer, kind);
                if (overrides != 0)
                {
                    var restore = Add("Revert to Style Value", null);
                    foreach (var (field, label) in RevertibleStyleFields)
                        if (overrides.HasFlag(field))
                        {
                            var item = new MenuItem { Header = Locale.T(label) };
                            item.Click += (_, _) => { if (current() is { } live) target.ResetStyleFields(live, kind, field); };
                            restore.Items.Add(item);
                        }
                    restore.Items.Add(new Separator());
                    var all = new MenuItem { Header = Locale.T("Revert All Overrides") };
                    all.Click += (_, _) => { if (current() is { } live) target.ResetStyleFields(live, kind, overrides); };
                    restore.Items.Add(all);
                }
                Add("Unlink Style", () => { if (current() is { } live) target.UnlinkLayerStyle(live, kind); });
            }
            if (canEdit) Add("Save as Style…", () => _ = SaveStyle(target, layer, kind));
            if (style != null) Add("Delete Style…", () => _ = DeleteSharedStyle(target, style));
            var others = target.Document.Styles.Values.Where(value => value.Kind == kind && value.Id != style?.Id)
                .OrderBy(value => value.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            if (others.Length > 0)
            {
                var saved = Add("Delete Saved Style", null);
                foreach (var definition in others)
                {
                    var item = new MenuItem { Header = definition.Name };
                    item.Click += (_, _) => _ = DeleteSharedStyle(target, definition);
                    saved.Items.Add(item);
                }
            }
            menu.Open(more);
            MenuItem Add(string label, Action? action)
            {
                var item = new MenuItem { Header = Locale.T(label) };
                if (action != null) item.Click += (_, _) => action();
                menu.Items.Add(item);
                return item;
            }
        };
        var status = Ui.Label(Locale.T("Local Style Overrides"), Palette.Secondary, 11);
        status.Name = kind == LayerStyleKind.Text ? "TextStyleOverrides" : "AppearanceStyleOverrides";
        var row = CanvasLayoutEditor.Row("*,Auto");
        CanvasLayoutEditor.Put(row, CanvasLayoutEditor.Field(caption, picker), 0);
        CanvasLayoutEditor.Put(row, more, 1);
        body.Children.Add(row);
        body.Children.Add(status);

        var syncing = false;
        StyleChoice[] choices = [];
        picker.SelectionChanged += (_, _) =>
        {
            if (syncing || current() is not { } layer || picker.SelectedItem is not StyleChoice choice) return;
            if (choice.Id is { } id) target.ApplyLayerStyle(layer, id);
            else target.UnlinkLayerStyle(layer, kind);
        };
        void Refresh()
        {
            if (current() is not { } layer) return;
            var active = target.LinkedStyle(layer, kind);
            var applicable = kind == LayerStyleKind.Text ? StyleFields.Text : AppearanceStyleDefinition.From(layer).Fields;
            var available = target.Document.Styles.Values.Where(style => style.Kind == kind && ((style.Fields & ~applicable) == 0 || style.Id == active?.Id))
                .OrderBy(style => style.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(style => new StyleChoice(style.Id, style.Name, style)).Prepend(new(null, Locale.T("No Style"), null)).ToArray();
            syncing = true;
            try
            {
                if (!choices.SequenceEqual(available))
                {
                    choices = available;
                    picker.ItemsSource = choices;
                }
                picker.SelectedIndex = Array.FindIndex(choices, choice => choice.Id == active?.Id);
                picker.IsEnabled = target.CanEditLayerStyle(layer, kind);
                more.IsEnabled = picker.IsEnabled || target.Document.Styles.Values.Any(style => style.Kind == kind);
                var overrides = target.StyleOverrides(layer, kind);
                status.IsVisible = overrides != 0;
                ToolTip.SetTip(status, overrides == 0 ? null : string.Join(", ", RevertibleStyleFields
                    .Where(entry => overrides.HasFlag(entry.Field)).Select(entry => Locale.T(entry.Caption))));
            }
            finally { syncing = false; }
        }
        refreshOptions += Refresh;
        Refresh();
    }

    private Control BuildStyleChoice(EditorSession target, StyleChoice? item)
    {
        item ??= new(null, Locale.T("No Style"), null);
        var label = Ui.Label(item.Name);
        label.MaxLines = 1;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        var sample = new Border
        {
            Width = 30, Height = 18, CornerRadius = new CornerRadius(3), ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center
        };
        if (item.Definition?.Text is { } text)
        {
            var weight = text.Face.FontStyle?.Weight ?? (text.Face.Bold ? 700 : 400);
            var ink = StyleBrush(target, text.Color);
            sample.Background = SampleBackground(ink.Color);
            sample.Child = new TextBlock
            {
                Text = "Aa", FontFamily = new FontFamily(text.Face.FontFamily), FontSize = 14,
                FontWeight = (FontWeight)weight, FontStyle = text.Face.Italic || text.Face.FontStyle?.Slant is SKFontStyleSlant.Italic or SKFontStyleSlant.Oblique ? FontStyle.Italic : FontStyle.Normal,
                Foreground = ink, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        else if (item.Definition?.Appearance is { } appearance)
        {
            var paint = appearance.Paint;
            var swatch = paint?.FillGradient?.Ramp.Sample(.5) is { } sampleColor ? (uint)sampleColor
                : paint?.Fill ?? appearance.Effects?.ColorOverlay?.Color ?? 0xff80858c;
            sample.Background = paint?.FillEnabled == false ? Brushes.Transparent : StyleBrush(target, swatch);
            sample.BorderBrush = paint is { StrokeEnabled: true, Stroke: { } stroke } ? StyleBrush(target, stroke) : Palette.SwatchOutline;
            sample.BorderThickness = new Thickness(paint is { StrokeEnabled: true, Stroke: not null } ? Math.Clamp(paint.StrokeWidth, 1, 3) : 1);
            sample.Opacity = appearance.Opacity;
        }
        else sample.Child = Ui.Label("—", Palette.Secondary);
        var row = CanvasLayoutEditor.Row("30,*", 7);
        CanvasLayoutEditor.Put(row, sample, 0);
        CanvasLayoutEditor.Put(row, label, 1);
        return row;
    }

    private SolidColorBrush StyleBrush(EditorSession target, uint color) =>
        new(new SKColor(color).ToDisplay(target.Document.Profile, Ui.DisplayProfile(this)));

    private static SolidColorBrush SampleBackground(Color ink) =>
        new((ink.R * 299 + ink.G * 587 + ink.B * 114) < 144000 ? Color.FromRgb(245, 244, 240) : Color.FromRgb(37, 39, 43));

    private async Task DeleteSharedStyle(EditorSession target, LayerStyleDefinition style)
    {
        if (session != target || IsDragging || target.Document.Styles.GetValueOrDefault(style.Id) != style) return;
        var message = $"Delete “{style.Name}” from this project?";
        if (target.StyleLinkCount(style) > 0) message += "\nLinked objects will keep their current appearance.";
        if (await Prompts.Confirm(this, "Delete Style", message, "Delete") && session == target && !IsDragging
            && target.Document.Styles.GetValueOrDefault(style.Id) == style)
            target.DeleteLayerStyle(style.Id);
    }

    private async Task SaveStyle(EditorSession target, Layer layer, LayerStyleKind kind)
    {
        if (session != target || IsDragging) return;
        target.FinishText();
        if (target.ActiveLayer is not { } live || live.Id != layer.Id || !target.CanEditLayerStyle(live, kind)) return;
        layer = live;
        if (target.Document.Styles.Count >= DocumentLimits.MaxLayerStyles)
        {
            ShowProblem("This document has reached its layer style limit.");
            return;
        }
        var name = new TextBox { Name = "NewLayerStyleName", MaxLength = LayerStyleDefinition.MaxNameLength };
        var body = Ui.Column(8, CanvasLayoutEditor.Field("Style Name", name));
        body.Width = 340;
        var dialog = new DialogWindow("Save as Style…", body, "Save") { CanAccept = false };
        name.TextChanged += (_, _) => dialog.CanAccept = target.IsStyleNameAvailable(kind, name.Text);
        dialog.Opened += (_, _) => name.Focus();
        if (await dialog.Ask(this) && session == target && target.ActiveLayer == layer && name.Text is { } value)
            target.CreateLayerStyle(layer, kind, value);
    }

    private async Task EditSharedStyle(EditorSession target, Layer layer, LayerStyleDefinition style)
    {
        if (session != target || IsDragging) return;
        target.FinishText();
        if (target.ActiveLayer is not { } live || live.Id != layer.Id || target.LinkedStyle(live, style.Kind) != style) return;
        if (!target.CanUpdateLayerStyle(style))
        {
            ShowProblem("Unlock the linked layers before editing their style.");
            return;
        }
        var revision = target.Revision;
        var draft = style;
        var name = new TextBox { Text = style.Name, Name = "SharedStyleName", MaxLength = LayerStyleDefinition.MaxNameLength };
        var count = Ui.Label(Locale.T("Linked Objects") + " · " + target.StyleLinkCount(style), Palette.Secondary);
        count.Name = "SharedStyleLinkedCount";
        var body = Ui.Column(10, count, CanvasLayoutEditor.Field("Style Name", name));
        body.Width = 368;
        var dialog = new DialogWindow("Edit Style…", body, "Apply");
        if (style.Text is { } text)
            BuildTextStyleEditor(body, dialog, target, text, value => draft = draft with { Text = value });
        else if (style.Appearance is { } appearance)
            BuildAppearanceStyleEditor(body, dialog, target, appearance, value => draft = draft with { Appearance = value });
        name.TextChanged += (_, _) => dialog.CanAccept = target.IsStyleNameAvailable(style.Kind, name.Text, style.Id);
        if (await dialog.Ask(this) && session == target && target.Revision == revision && name.Text is { } value)
            target.UpdateLayerStyle(draft with { Name = value });
    }

    private void BuildTextStyleEditor(StackPanel body, Window dialog, EditorSession target,
        TextStyleDefinition draft, Action<TextStyleDefinition> changed)
    {
        var familyNames = EditorSession.FontFamilies.Append(draft.Face.FontFamily).Distinct().Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        var faceMenu = new ComboBox { Name = "SharedStyleTypeface", Width = double.NaN };
        var faceChoices = FontCatalog.ForFamily(draft.Face.FontFamily);
        var sample = Ui.Label("Aa 690 ₽");
        sample.TextTrimming = TextTrimming.CharacterEllipsis;
        var sampleFrame = new Border { Background = Palette.Field, Padding = new Thickness(10, 5), Height = 64, CornerRadius = new CornerRadius(5), Child = sample };
        var swatch = LayoutButton("Text Color", () => { });
        swatch.Click += (_, _) => _ = PickColor();
        async Task PickColor()
        {
            if (await Prompts.Color(dialog, "Text Color", new SKColor(draft.Color), profile: target.Document.Profile) is { } color)
                Change(draft with { Color = (uint)color });
        }
        void UpdateSample()
        {
            sample.FontFamily = new FontFamily(draft.Face.FontFamily);
            sample.FontWeight = (FontWeight)(draft.Face.FontStyle?.Weight ?? (draft.Face.Bold ? 700 : 400));
            sample.FontStyle = draft.Face.Italic || draft.Face.FontStyle?.Slant is SKFontStyleSlant.Italic or SKFontStyleSlant.Oblique ? FontStyle.Italic : FontStyle.Normal;
            sample.FontSize = Math.Clamp(draft.Size, 12, 43);
            var ink = StyleBrush(target, draft.Color);
            sample.Foreground = ink;
            sampleFrame.Background = SampleBackground(ink.Color);
            swatch.BorderBrush = ink;
            swatch.BorderThickness = new Thickness(1);
        }
        void Change(TextStyleDefinition value) { draft = value; changed(value); UpdateSample(); }
        void PopulateFaces()
        {
            faceChoices = FontCatalog.ForFamily(draft.Face.FontFamily);
            faceMenu.ItemsSource = faceChoices.Select(face => face.Name).ToArray();
            faceMenu.SelectedIndex = faceChoices.ToList().FindIndex(choice => choice.Face == FontCatalog.Closest(draft.Face.FontFamily, draft.Face).Face);
        }
        bool syncing = false;
        PopulateFaces();
        faceMenu.SelectionChanged += (_, _) =>
        {
            if (!syncing && faceMenu.SelectedIndex >= 0 && faceMenu.SelectedIndex < faceChoices.Count)
                Change(draft with { Face = faceChoices[faceMenu.SelectedIndex].Face });
        };
        var family = Ui.Combo(familyNames, draft.Face.FontFamily, label => label, value =>
        {
            Change(draft with { Face = FontCatalog.Closest(value, draft.Face).Face });
            syncing = true;
            PopulateFaces();
            syncing = false;
        }, double.NaN);
        var size = CanvasLayoutEditor.Number("Font size", draft.Size, 1, 2000, value => Change(draft with { Size = value }), format: "0.##");
        size.Name = "SharedStyleFontSize";
        var tracking = CanvasLayoutEditor.Number("Letter spacing", draft.Tracking, -100, 1000, value => Change(draft with { Tracking = value }), format: "0.##");
        var leading = CanvasLayoutEditor.Number("Line height", draft.Leading, 0, 5000, value => Change(draft with { Leading = value }), format: "0.##");
        var paragraph = CanvasLayoutEditor.Number("Paragraph spacing", draft.ParagraphSpacing, 0, 5000, value => Change(draft with { ParagraphSpacing = value }), format: "0.##");
        body.Children.AddRange([
            sampleFrame, CanvasLayoutEditor.Field("Font Family", family),
            CanvasLayoutEditor.Pair(CanvasLayoutEditor.Field("Font style", faceMenu), CanvasLayoutEditor.Field("Font size", size, "px")),
            CanvasLayoutEditor.Pair(CanvasLayoutEditor.Field("Letter spacing", tracking, draft.PercentTracking ? "%" : "px"),
                CanvasLayoutEditor.Field("Line height", leading, draft.PercentLeading ? "%" : "px")),
            CanvasLayoutEditor.Pair(CanvasLayoutEditor.Field("Paragraph spacing", paragraph, "px"),
                new Border { Child = swatch, VerticalAlignment = VerticalAlignment.Bottom })
        ]);
        UpdateSample();
    }

    private void BuildAppearanceStyleEditor(StackPanel body, Window dialog, EditorSession target,
        AppearanceStyleDefinition draft, Action<AppearanceStyleDefinition> changed)
    {
        void Change(AppearanceStyleDefinition value) { draft = value; changed(value); }
        var opacity = CanvasLayoutEditor.Number("Opacity", draft.Opacity * 100, 0, 100, value => Change(draft with { Opacity = value / 100 }));
        body.Children.Add(CanvasLayoutEditor.Field("Opacity", opacity, "%"));
        if (draft.Paint is { } paint)
        {
            void Paint(ShapePaintDefinition value) => Change(draft with { Paint = value });
            Button Brush(bool stroke)
            {
                var label = stroke ? "Stroke" : "Fill";
                var button = LayoutButton(label, () => { });
                void Preview()
                {
                    var state = draft.Paint!;
                    var color = stroke ? state.Stroke ?? 0xff000000 : state.Fill;
                    button.BorderBrush = StyleBrush(target, color);
                    button.BorderThickness = new Thickness(1);
                }
                button.Click += async (_, _) =>
                {
                    var state = draft.Paint!;
                    var presented = state;
                    var accepted = await VectorPaintDialog.Edit(dialog, label, stroke ? state.Stroke ?? 0xff000000 : state.Fill,
                        stroke ? state.StrokeGradient : state.FillGradient, (color, gradient) =>
                        {
                            presented = stroke ? state with { Stroke = color, StrokeGradient = gradient } : state with { Fill = color, FillGradient = gradient };
                        });
                    if (accepted) { Paint(presented); Preview(); }
                };
                Preview();
                return button;
            }
            body.Children.Add(CanvasLayoutEditor.Pair(Brush(false), Brush(true)));
            body.Children.Add(CanvasLayoutEditor.Pair(
                Ui.Check("Show fill", paint.FillEnabled, value => Paint(draft.Paint! with { FillEnabled = value })),
                Ui.Check("Show stroke", paint.StrokeEnabled, value => Paint(draft.Paint! with
                {
                    StrokeEnabled = value, Stroke = value ? draft.Paint!.Stroke ?? 0xff000000 : draft.Paint!.Stroke
                }))));
            var width = CanvasLayoutEditor.Number("Stroke width", paint.StrokeWidth, 0, 500,
                value => Paint(draft.Paint! with { StrokeWidth = value }), format: "0.##");
            width.Name = "SharedStyleStrokeWidth";
            body.Children.Add(CanvasLayoutEditor.Field("Stroke width", width, "px"));
        }
        if (draft.Effects == null) return;
        var selected = LayerEffectKind.Stroke;
        var effects = Enum.GetValues<LayerEffectKind>();
        var selector = Ui.Combo(effects, selected, LayerEffects.DisplayName, value => { selected = value; }, double.NaN);
        body.Children.Add(CanvasLayoutEditor.Field("Effect", selector));
        var state = Ui.Label("", Palette.Secondary, 11);
        void SyncEffects() => state.Text = string.Join(" · ", draft.Effects!.Kinds.Select(kind =>
            Locale.T(LayerEffects.DisplayName(kind)) + (draft.Effects.IsEnabled(kind) ? "" : " (" + Locale.T("Hidden Effect") + ")")));
        void ChangeEffects(LayerEffects next) { Change(draft with { Effects = next }); SyncEffects(); }
        var buttons = CanvasLayoutEditor.Row("*,*,*");
        CanvasLayoutEditor.Put(buttons, LayoutButton("Add or Edit…", () => _ = EditEffect()), 0);
        CanvasLayoutEditor.Put(buttons, LayoutButton("Show or Hide", () =>
        {
            if (draft.Effects!.Contains(selected)) ChangeEffects(draft.Effects.WithEnabled(selected, !draft.Effects.IsEnabled(selected)));
        }), 1);
        CanvasLayoutEditor.Put(buttons, LayoutButton("Remove", () => ChangeEffects(draft.Effects!.Without(selected))), 2);
        body.Children.Add(buttons);
        body.Children.Add(state);
        state.TextWrapping = TextWrapping.Wrap;
        SyncEffects();
        async Task EditEffect()
        {
            var kind = selected;
            var baseline = draft.Effects!.Contains(kind) ? draft.Effects
                : EditorSession.AddDefaultStyleEffect(draft.Effects, kind, (uint)target.Foreground, (uint)target.Background | 0xff000000);
            if (await EffectsDialog.EditSettings(dialog, target.Document.Profile, baseline, kind) is { } value) ChangeEffects(value);
        }
    }
}
