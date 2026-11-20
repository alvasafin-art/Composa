using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Composa.Editing;
using Composa.Model;
using Composa.Text;

namespace Composa.App;

public sealed partial class MainWindow
{
    private static Grid PropertyPair(Control first, Control second)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        row.Children.Add(first); AddAt(row, second, 1);
        return row;
    }

    private static Panel PropertyUnit(string name, out TextBlock label, Action? switchUnit = null)
    {
        var unit = new Panel();
        if (switchUnit != null)
        {
            var button = new Button
            {
                Classes = { "flat" }, Padding = default, MinWidth = 0, MinHeight = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch
            };
            var tip = name + " units · switch between pixels and percent of font size";
            AutomationProperties.SetName(button, tip);
            ToolTip.SetTip(button, tip);
            button.Click += (_, _) => switchUnit();
            unit.Children.Add(button);
        }
        label = Ui.Label("px", Palette.Secondary, 11);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.IsHitTestVisible = false;
        unit.Children.Add(label);
        return unit;
    }

    private static Border PropertyNumber(NumericUpDown number, Icons.Icon icon, string name, Control unit)
    {
        var field = new Grid { ColumnDefinitions = new ColumnDefinitions("16,*,20"), ColumnSpacing = 5 };
        var symbol = new Border { Background = Avalonia.Media.Brushes.Transparent, Child = Icons.Create(icon, 16, Palette.Secondary) };
        field.Children.Add(Ui.Scrub(symbol, number));
        number.Width = double.NaN;
        number.MinWidth = 0;
        number.Padding = default;
        number.Background = Avalonia.Media.Brushes.Transparent;
        number.BorderThickness = default;
        number.HorizontalAlignment = HorizontalAlignment.Stretch;
        number.VerticalContentAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(number, name);
        AddAt(field, number, 1);
        AddAt(field, unit, 2);
        return new Border
        {
            Height = 30, Padding = new Thickness(8, 0), CornerRadius = new CornerRadius(4),
            Background = Palette.Field, Child = field, ClipToBounds = true
        };
    }

    private void BuildObjectProperties()
    {
        if (session is not { } s || !dock.Section("Properties").State.Visible) return;
        var body = new StackPanel { Margin = new Thickness(12, 6, 12, 12), Spacing = 8 };
        propertiesView.Content = body;
        var layer = s.ActiveLayer;
        if (layer == null) { body.Children.Add(Ui.Label("No layer selected", Palette.Secondary)); return; }
        body.Children.Add(Ui.Label(layer.Name, weight: Avalonia.Media.FontWeight.SemiBold));
        if (s.IsEditingMask)
        {
            if (s.EditingMask && layer.Mask != null) body.Children.Add(Flat("Mask properties…", () => _ = EditMaskProperties(layer)));
            else body.Children.Add(Ui.Label("Channel", Palette.Secondary));
            return;
        }
        var updating = false;
        if (layer.Text != null)
        {
            BuildTextFields(body, properties: true);
            BuildTypography(body, s);
            return;
        }
        if (layer.Shape != null && !(s.Tool == Tool.Move && geometryOpen))
        {
            BuildShapeFields(body);
        }
        var opacity = Ui.Number(layer.Opacity * 100, 0, 100, v =>
        {
            if (!updating && s.ActiveLayer is { } live && live.Id == layer.Id && !s.IsLocked(live) && live.Opacity != v / 100)
                s.Apply("Layer Opacity", () => s.SetOpacity(live, v / 100));
        });
        body.Children.Add(Ui.Row(6, Ui.Label("Opacity %"), opacity));
        refreshOptions += () =>
        {
            if (s.ActiveLayer is not { } live) return;
            updating = true; opacity.Value = (decimal)(live.Opacity * 100); updating = false;
        };
        refreshOptions?.Invoke();
    }

    private void BuildTypography(StackPanel body, EditorSession s)
    {
        var refresh = new List<Action<Typography>>();
        var optional = new List<(Control Control, string Tag)>();
        void RegisterFeature(Control control, string tip)
        {
            var tag = tip switch
            {
                "Small capitals" => "smcp",
                "Case-sensitive forms" => "case",
                "Capital spacing" => "cpsp",
                "Lining figures" => "lnum",
                "Old-style figures" => "onum",
                "Tabular figures" => "tnum",
                "Proportional figures" => "pnum",
                "Subscript" => "subs",
                "Superscript" => "sups",
                _ => null
            };
            if (tag != null) optional.Add((control, tag));
        }
        bool updating = false;
        void Change(Func<Typography, Typography> edit)
        {
            if (!updating) s.ChangeTextStyle(st => st with { Type = edit(st.Type) });
        }
        void Choices<T>(StackPanel parent, string name, Func<Typography, T> get, Func<Typography, T, Typography> set,
            params (T Value, string Icon, string Tip)[] entries)
        {
            var row = new WrapPanel { ItemSpacing = 2 };
            foreach (var (value, icon, tip) in entries)
            {
                var b = new ToggleButton { Classes = { "tool" }, MinWidth = 31, Height = 26, Padding = new Thickness(4, 0), Content = icon };
                ToolTip.SetTip(b, tip); AutomationProperties.SetName(b, tip); RegisterFeature(b, tip);
                b.Click += (_, _) => { Change(t => set(t, value)); refreshOptions?.Invoke(); };
                refresh.Add(t => b.IsChecked = EqualityComparer<T>.Default.Equals(get(t), value));
                row.Children.Add(b);
            }
            parent.Children.Add(Ui.Column(2, Ui.Label(name, Palette.Secondary, 11), row));
        }
        void Flag(StackPanel parent, string icon, string tip, Func<Typography, bool> get, Func<Typography, bool, Typography> set)
        {
            var b = new ToggleButton { Content = icon, MinWidth = 32, Height = 26, Padding = new Thickness(4, 0) };
            ToolTip.SetTip(b, tip); AutomationProperties.SetName(b, tip); RegisterFeature(b, tip);
            b.Click += (_, _) => Change(t => set(t, !get(t)));
            refresh.Add(t => b.IsChecked = get(t)); parent.Children.Add(b);
        }
        void Number(StackPanel parent, string label, Func<Typography, double> get, Func<Typography, double, Typography> set)
        {
            var n = Ui.Number(get(s.CurrentTextStyle.Type), 0, 5000, v => Change(t => set(t, v)));
            refresh.Add(t => n.Value = (decimal)get(t)); n.Width = double.NaN; n.HorizontalAlignment = HorizontalAlignment.Stretch; parent.Children.Add(PropertyPair(Ui.Label(label), n));
        }
        void Group(string name, StackPanel content) => body.Children.Add(new Expander { Header = name, Content = content, Padding = new Thickness(0, 4), HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch });
        var paragraph = Ui.Column(5);
        Choices(paragraph, "Vertical alignment", t => t.Vertical, (t, v) => t with { Vertical = v },
            (TextVertical.Top, "↥", "Top"), (TextVertical.Center, "↕", "Center vertically"), (TextVertical.Bottom, "↧", "Bottom"));
        Choices(paragraph, "List", t => t.List, (t, v) => t with { List = v }, (TextList.None, "—", "No list"), (TextList.Bullets, "• ≡", "Bulleted list"), (TextList.Numbers, "1 ≡", "Numbered list"));
        Choices(paragraph, "Wrap", t => t.Wrap, (t, v) => t with { Wrap = v }, (TextWrap.Word, "↵", "Word wrap"), (TextWrap.Character, "A↵", "Wrap characters"), (TextWrap.None, "→", "No wrap"));
        var flags = Ui.Row(3);
        Flag(flags, "…", "Truncate with ellipsis", t => t.Ellipsis, (t, v) => t with { Ellipsis = v });
        Flag(flags, "↕A", "Trim vertical font metrics", t => t.Trim, (t, v) => t with { Trim = v });
        Flag(flags, "“←", "Hanging punctuation", t => t.HangingPunctuation, (t, v) => t with { HangingPunctuation = v });
        Flag(flags, "•↤", "Hanging list indent", t => t.HangingList, (t, v) => t with { HangingList = v });
        paragraph.Children.Add(flags);
        Number(paragraph, "Paragraph gap", t => t.ParagraphSpacing, (t, v) => t with { ParagraphSpacing = v });
        Number(paragraph, "First-line indent", t => t.Indent, (t, v) => t with { Indent = v });
        Group("Paragraph", paragraph);
        var detail = Ui.Column(5);
        Choices(detail, "Letter case", t => t.Case, (t, v) => t with { Case = v }, (TextLetterCase.Original, "—", "Original case"), (TextLetterCase.Upper, "AG", "Uppercase"), (TextLetterCase.Lower, "ag", "Lowercase"), (TextLetterCase.Title, "Ag", "Title case"), (TextLetterCase.SmallCaps, "Aɢ", "Small capitals"));
        var caps = Ui.Row(3);
        Flag(caps, "(A)", "Case-sensitive forms", t => t.CaseForms, (t, v) => t with { CaseForms = v });
        Flag(caps, "A A", "Capital spacing", t => t.CapitalSpacing, (t, v) => t with { CapitalSpacing = v });
        detail.Children.Add(caps);
        Choices(detail, "Numerals", t => t.Figures, (t, v) => t with { Figures = v }, (FigureStyle.Default, "—", "Font default"), (FigureStyle.Lining, "17", "Lining figures"), (FigureStyle.OldStyle, "₁7", "Old-style figures"), (FigureStyle.Tabular, "|1|", "Tabular figures"), (FigureStyle.Proportional, "1↔", "Proportional figures"));
        Choices(detail, "Number position", t => t.FigurePosition, (t, v) => t with { FigurePosition = v }, (FigurePosition.Baseline, "A2", "Baseline"), (FigurePosition.Subscript, "A₂", "Subscript"), (FigurePosition.Superscript, "A²", "Superscript"));
        Group("Type details", detail);
        var axes = Ui.Column(4);
        var axesGroup = new Expander { Header = "Variable font", Content = axes, HorizontalAlignment = HorizontalAlignment.Stretch };
        body.Children.Add(axesGroup);
        TextFace? displayed = null;
        refreshOptions += () =>
        {
            updating = true;
            try
            {
                var type = s.CurrentTextStyle.Type;
                foreach (var update in refresh) update(type);
                var face = s.CurrentTextFace;
                if (face != displayed)
                {
                    displayed = face; axes.Children.Clear();
                    foreach (var entry in optional) entry.Control.IsEnabled = FontCatalog.Supports(face, entry.Tag);
                    foreach (var axis in FontVariations.Axes(TextLayout.TypefaceFor(face)))
                    {
                        var field = Ui.Number(FontVariations.Value(type.Axes, axis), axis.Min, axis.Max,
                            v => Change(t => t with { Axes = FontVariations.WithValue(t.Axes, axis, v) }), .1, "0.##", 90);
                        field.Tag = axis;
                        axes.Children.Add(Ui.Row(6, Ui.Label(axis.Tag), field));
                    }
                    axesGroup.IsVisible = axes.Children.Count > 0;
                }
                foreach (var row in axes.Children.OfType<StackPanel>())
                    if (row.Children.OfType<NumericUpDown>().FirstOrDefault() is { Tag: FontVariations.Axis axis } field)
                        field.Value = (decimal)FontVariations.Value(type.Axes, axis);
            }
            finally { updating = false; }
        };
        refreshOptions?.Invoke();
    }
}
