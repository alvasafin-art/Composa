using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
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
        row.RowDefinitions = new RowDefinitions("Auto,Auto");
        row.Children.Add(first); AddAt(row, second, 1);
        row.SizeChanged += (_, e) =>
        {
            bool stack = e.NewSize.Width < 215;
            if (stack == (Grid.GetRow(second) == 1)) return;
            row.RowSpacing = stack ? 8 : 0;
            Grid.SetColumnSpan(first, stack ? 2 : 1);
            Grid.SetColumn(second, stack ? 0 : 1);
            Grid.SetColumnSpan(second, stack ? 2 : 1);
            Grid.SetRow(second, stack ? 1 : 0);
        };
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

    private readonly HashSet<string> expandedTypeGroups = new();

    private StackPanel TypeGroup(string name, Control content)
    {
        var arrow = Icons.Create(Icons.ChevronDown, 20, Palette.Secondary);
        var title = Ui.Label(name, weight: Avalonia.Media.FontWeight.SemiBold);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,20") };
        header.Children.Add(title);
        AddAt(header, arrow, 1);
        var button = new Button
        {
            Classes = { "flat" }, Padding = new Thickness(0, 5, 8, 5), Content = header,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(button, name);
        void Refresh()
        {
            content.IsVisible = expandedTypeGroups.Contains(name);
            arrow.RenderTransform = new Avalonia.Media.RotateTransform(content.IsVisible ? 180 : 0);
        }
        button.Click += (_, _) => { if (!expandedTypeGroups.Remove(name)) expandedTypeGroups.Add(name); Refresh(); };
        Refresh();
        var rule = new Border { Height = 1, Background = Palette.Field, IsHitTestVisible = false };
        return Ui.Column(6, rule, button, content);
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
        Control Choices<T>(string name, Func<Typography, T> get, Func<Typography, T, Typography> set,
            params (T Value, string Icon, string Tip)[] entries)
        {
            var row = new Grid { ColumnSpacing = 2 };
            foreach (var (value, icon, tip) in entries)
            {
                var b = new ToggleButton
                {
                    Classes = { "tool" }, Width = double.NaN, Height = 26, MinWidth = 0, Padding = new Thickness(3, 0), Content = icon,
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center
                };
                ToolTip.SetTip(b, tip); AutomationProperties.SetName(b, tip); RegisterFeature(b, tip);
                b.Click += (_, _) => { Change(t => set(t, value)); refreshOptions?.Invoke(); };
                refresh.Add(t => b.IsChecked = EqualityComparer<T>.Default.Equals(get(t), value));
                row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                AddAt(row, b, row.Children.Count);
            }
            if (name == "Wrap")
            {
                row.SizeChanged += (_, e) =>
                {
                    bool compact = e.NewSize.Width < 210;
                    if (compact == (row.RowDefinitions.Count != 0)) return;
                    row.RowDefinitions.Clear();
                    row.RowSpacing = compact ? 2 : 0;
                    for (int i = 0; i < row.Children.Count; i++)
                    {
                        if (compact) row.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                        Grid.SetRow(row.Children[i], compact ? i : 0);
                        Grid.SetColumn(row.Children[i], compact ? 0 : i);
                        Grid.SetColumnSpan(row.Children[i], compact ? row.Children.Count : 1);
                    }
                };
            }
            var group = new Border { Background = Palette.Field, CornerRadius = new CornerRadius(6), Padding = new Thickness(3), Child = row };
            return Ui.Column(5, Ui.Label(name, Palette.Secondary, 11), group);
        }

        Control Flag(string label, string tip, Func<Typography, bool> get, Func<Typography, bool, Typography> set)
        {
            var box = new CheckBox { Content = label, MinHeight = 28, HorizontalAlignment = HorizontalAlignment.Stretch };
            box.Template = new FuncControlTemplate<CheckBox>((toggle, _) =>
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("22,*"), ColumnSpacing = 8,
                    Background = Avalonia.Media.Brushes.Transparent };
                var caption = Ui.Label(label, size: 12);
                caption.VerticalAlignment = VerticalAlignment.Center;
                caption.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
                AddAt(row, caption, 1);
                var thumb = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5) };
                var track = new Border { Height = 13, CornerRadius = new CornerRadius(7), Padding = new Thickness(2),
                    VerticalAlignment = VerticalAlignment.Center, Child = thumb };
                row.Children.Add(track);
                void Draw()
                {
                    bool on = toggle.IsChecked == true;
                    track.Background = on ? Palette.Accent : Palette.Edge;
                    thumb.Background = on ? Avalonia.Media.Brushes.White : Palette.Secondary;
                    thumb.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                    row.Opacity = toggle.IsEnabled ? 1 : .48;
                }
                Draw();
                toggle.PropertyChanged += (_, e) =>
                {
                    if (e.Property == CheckBox.IsCheckedProperty || e.Property == IsEnabledProperty) Draw();
                };
                return row;
            });
            ToolTip.SetTip(box, tip); AutomationProperties.SetName(box, tip); RegisterFeature(box, tip);
            box.IsCheckedChanged += (_, _) => Change(t => set(t, box.IsChecked == true));
            refresh.Add(t => box.IsChecked = get(t));
            return box;
        }

        Control Number(string label, Func<Typography, double> get, Func<Typography, double, Typography> set)
        {
            var field = Ui.Number(get(s.CurrentTextStyle.Type), 0, 5000, v => Change(t => set(t, v)), format: "0.##");
            field.Width = double.NaN; field.Height = 30;
            field.Padding = new Thickness(8, 0);
            field.VerticalContentAlignment = VerticalAlignment.Center;
            field.HorizontalAlignment = HorizontalAlignment.Stretch;
            AutomationProperties.SetName(field, label);
            refresh.Add(t => field.Value = (decimal)get(t));
            return Ui.Column(4, Ui.Label(label, Palette.Secondary, 11), field);
        }
        var vertical = Choices("Vertical alignment", t => t.Vertical, (t, v) => t with { Vertical = v },
            (TextVertical.Top, "↥", "Top"), (TextVertical.Center, "↕", "Center vertically"), (TextVertical.Bottom, "↧", "Bottom"));
        var list = Choices("List", t => t.List, (t, v) => t with { List = v },
            (TextList.None, "—", "No list"), (TextList.Bullets, "• ≡", "Bulleted list"), (TextList.Numbers, "1 ≡", "Numbered list"));
        var wrap = Choices("Wrap", t => t.Wrap, (t, v) => t with { Wrap = v },
            (TextWrap.Word, "Words", "Word wrap"), (TextWrap.Character, "Characters", "Wrap characters"), (TextWrap.None, "No wrap", "No wrap"));
        var flowFlags = PropertyPair(Flag("Ellipsis", "Truncate with ellipsis", t => t.Ellipsis, (t, v) => t with { Ellipsis = v }),
            Flag("Trim height", "Trim vertical font metrics", t => t.Trim, (t, v) => t with { Trim = v }));
        var indentFlags = PropertyPair(Flag("Punctuation", "Hanging punctuation", t => t.HangingPunctuation, (t, v) => t with { HangingPunctuation = v }),
            Flag("List markers", "Hanging list indent", t => t.HangingList, (t, v) => t with { HangingList = v }));
        var paragraph = Ui.Column(12, PropertyPair(vertical, list), Ui.Column(4, wrap, flowFlags),
            PropertyPair(Number("Paragraph gap", t => t.ParagraphSpacing, (t, v) => t with { ParagraphSpacing = v }),
                Number("First-line indent", t => t.Indent, (t, v) => t with { Indent = v })),
            Ui.Column(3, Ui.Label("Hanging indent", Palette.Secondary, 11), indentFlags));
        var casing = Choices("Letter case", t => t.Case, (t, v) => t with { Case = v },
            (TextLetterCase.Original, "—", "Original case"), (TextLetterCase.Upper, "AG", "Uppercase"), (TextLetterCase.Lower, "ag", "Lowercase"),
            (TextLetterCase.Title, "Ag", "Title case"), (TextLetterCase.SmallCaps, "Aɢ", "Small capitals"));
        var figures = Choices("Numerals", t => t.Figures, (t, v) => t with { Figures = v }, (FigureStyle.Default, "—", "Font default"),
            (FigureStyle.Lining, "17", "Lining figures"), (FigureStyle.OldStyle, "₁7", "Old-style figures"),
            (FigureStyle.Tabular, "|1|", "Tabular figures"), (FigureStyle.Proportional, "1↔", "Proportional figures"));
        var position = Choices("Number position", t => t.FigurePosition, (t, v) => t with { FigurePosition = v },
            (FigurePosition.Baseline, "A2", "Baseline"), (FigurePosition.Subscript, "A₂", "Subscript"), (FigurePosition.Superscript, "A²", "Superscript"));
        var numeralRow = PropertyPair(figures, position);
        numeralRow.ColumnDefinitions = new ColumnDefinitions("5*,3*");
        var detail = Ui.Column(12, Ui.Column(4, casing,
            PropertyPair(Flag("Case forms", "Case-sensitive forms", t => t.CaseForms, (t, v) => t with { CaseForms = v }),
                Flag("Capital spacing", "Capital spacing", t => t.CapitalSpacing, (t, v) => t with { CapitalSpacing = v }))), numeralRow);
        body.Children.Add(TypeGroup("Type details", detail));
        body.Children.Add(TypeGroup("Paragraph", paragraph));
        var axes = Ui.Column(6);
        var axesGroup = TypeGroup("Variable font", axes);
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
                        field.VerticalContentAlignment = VerticalAlignment.Center;
                        field.Padding = new Thickness(8, 0);
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
