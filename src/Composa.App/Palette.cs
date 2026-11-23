using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Composa.App;

public static class Palette
{
    // Controls, icons and custom drawing pens keep these brushes for their lifetime.
    // Updating their colors also updates open dialogs and already-created panel content.
    private static readonly List<(SolidColorBrush Brush, Color Classic, Color Dark, Color Light)> colors = new();
    private static SolidColorBrush Brush(string classic, string light, string? dark = null)
    {
        var original = Color.Parse(classic);
        var brush = new SolidColorBrush(original);
        colors.Add((brush, original, dark == null ? original : Color.Parse(dark), Color.Parse(light)));
        return brush;
    }

    public static readonly SolidColorBrush Window = Brush("#222326", "#E9E9E9");
    public static readonly SolidColorBrush Panel = Brush("#292B2F", "#F4F4F4");
    public static readonly SolidColorBrush PanelRaised = Brush("#35383D", "#FAFAFA");
    public static readonly SolidColorBrush Divider = Brush("#1A1A1A", "#C9C9C9");
    public static readonly SolidColorBrush Foreground = Brush("#EEF0F2", "#242424");
    public static readonly SolidColorBrush Secondary = Brush("#AFB3B9", "#575757");
    public static readonly SolidColorBrush Accent = Brush("#3D9BFF", "#535353", "#B8B8B8");
    public static readonly SolidColorBrush Selected = Brush("#384B63", "#CCCCCC", "#18191B");
    internal static readonly SolidColorBrush RaisedIconWell = Brush("#373A3E", "#E6E6E6", "#303235");
    public static readonly SolidColorBrush Hover = Brush("#464A50", "#D9D9D9");
    public static readonly SolidColorBrush Field = Brush("#373A3E", "#E6E6E6");
    public static readonly SolidColorBrush Edge = Brush("#585D64", "#A4A4A4");
    internal static readonly SolidColorBrush Active = Brush("#3D9BFF", "#BEBEBE", "#121315");
    internal static readonly SolidColorBrush ActiveHover = Brush("#65B1FF", "#B1B1B1", "#1C1D1F");
    internal static readonly SolidColorBrush ActivePressed = Brush("#308BE7", "#A6A6A6", "#0B0C0D");
    internal static readonly SolidColorBrush OnActive = Brush("#FFFFFF", "#202020", "#F1F1F1");
    internal static readonly SolidColorBrush ActionText = Brush("#222326", "#202020", "#F1F1F1");
    internal static readonly SolidColorBrush DropFill = Brush("#343D9BFF", "#34555555", "#34B8B8B8");
    internal static readonly SolidColorBrush SliderFill = Brush("#394F69", "#CCCCCC", "#1B1C1E");
    internal static readonly SolidColorBrush Pasteboard = Brush("#1E1E1E", "#D3D3D3");
    internal static readonly SolidColorBrush SwatchOutline = Brush("#FFFFFF", "#777777");
    internal static readonly SolidColorBrush DialFace = Brush("#1F1F1F", "#E5E5E5");
    internal static readonly SolidColorBrush DialRim = Brush("#454545", "#AAAAAA");
    internal static readonly SolidColorBrush DialTick = Brush("#3A3A3A", "#BCBCBC");
    internal static readonly SolidColorBrush ActiveTab = Brush("#35383D", "#CCCCCC", "#18191B");
    internal static readonly SolidColorBrush StopSelection = Brush("#334356", "#CCCCCC", "#18191B");
    private static readonly SolidColorBrush Action = Brush("#30649B", "#BEBEBE", "#121315");
    internal static readonly SolidColorBrush ActionHover = Brush("#386FA8", "#B1B1B1", "#1C1D1F");

    internal static void Use(EditorTheme theme)
    {
        foreach (var color in colors)
            color.Brush.Color = theme switch { EditorTheme.DarkNeutral => color.Dark, EditorTheme.LightNeutral => color.Light, _ => color.Classic };
    }

    // Use the Fluent controls' own state resources so validation, keyboard navigation
    // and disabled states still run through the standard templates.
    private static void SetControlSurfaces(IResourceDictionary resources)
    {
        void Set(object value, params string[] keys)
        {
            foreach (var key in keys) resources[key] = value;
        }

        Set(new CornerRadius(5), "ControlCornerRadius");
        Set(Field, "TextControlBackground", "ComboBoxBackground", "ComboBoxBackgroundUnfocused", "ButtonBackground", "ToggleButtonBackground");
        Set(Hover, "TextControlBackgroundPointerOver", "ComboBoxBackgroundPointerOver", "ButtonBackgroundPointerOver",
            "ToggleButtonBackgroundPointerOver", "ComboBoxItemBackgroundPointerOver", "MenuFlyoutItemBackgroundPointerOver");
        Set(PanelRaised, "TextControlBackgroundFocused", "ExpanderHeaderBackgroundPointerOver", "ExpanderHeaderBackgroundPressed");
        Set(Brushes.Transparent, "TextControlBorderBrush", "ButtonBorderBrush", "ComboBoxBorderBrush", "ComboBoxBackgroundBorderBrushUnfocused",
            "ToggleButtonBorderBrush", "ExpanderHeaderBorderBrush", "ExpanderContentBorderBrush");
        Set(Edge, "TextControlBorderBrushPointerOver", "ComboBoxBorderBrushPointerOver", "ButtonBorderBrushPointerOver",
            "FlyoutBorderThemeBrush", "ComboBoxDropDownBorderBrush", "MenuFlyoutPresenterBorderBrush");
        Set(Action, "AccentButtonBackground");
        Set(ActionHover, "AccentButtonBackgroundPointerOver");
        Set(Accent, "TextControlBorderBrushFocused", "ComboBoxBackgroundBorderBrushFocused");
        Set(Secondary, "TextControlPlaceholderForeground", "ComboBoxPlaceHolderForeground");
        Set(PanelRaised, "ComboBoxDropDownBackground", "MenuFlyoutPresenterBackground", "FlyoutPresenterBackground");
        Set(Selected, "ComboBoxItemBackgroundSelected", "ComboBoxItemBackgroundSelectedPointerOver");
        Set(Panel, "ExpanderHeaderBackground", "ExpanderContentBackground");
        Set(new Thickness(0), "ExpanderHeaderBorderThickness", "ExpanderContentDownBorderThickness", "ExpanderContentUpBorderThickness",
            "ExpanderContentLeftBorderThickness", "ExpanderContentRightBorderThickness");
        Set(new Thickness(10, 5), "ExpanderHeaderPadding");
        Set(34d, "ExpanderMinHeight");
    }

    public static Styles Styles()
    {
        var styles = new Styles();
        SetControlSurfaces(styles.Resources);
        styles.Resources["ComposaForeground"] = Foreground;
        styles.Add(new Style(x => x.OfType<Window>()) { Setters = { new Setter(TemplatedControl.BackgroundProperty, Window), new Setter(TemplatedControl.FontSizeProperty, 12.5) } });
        styles.Add(new Style(x => x.OfType<TextBlock>()) { Setters = { new Setter(Layoutable.VerticalAlignmentProperty, VerticalAlignment.Center) } });
        // Animate the existing content so the action remains recognizable throughout the request.
        var busy = new Style(x => x.OfType<Button>().Class("ai-busy").Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>());
        var pulse = new Avalonia.Animation.Animation { Duration = TimeSpan.FromMilliseconds(900),
            IterationCount = Avalonia.Animation.IterationCount.Infinite, PlaybackDirection = Avalonia.Animation.PlaybackDirection.Alternate };
        pulse.Children.Add(new Avalonia.Animation.KeyFrame { Cue = new Avalonia.Animation.Cue(0), Setters = { new Setter(Visual.OpacityProperty, 1d) } });
        pulse.Children.Add(new Avalonia.Animation.KeyFrame { Cue = new Avalonia.Animation.Cue(1), Setters = { new Setter(Visual.OpacityProperty, .42d) } });
        busy.Animations.Add(pulse);
        styles.Add(busy);
        // The options bar under the tabs: every control takes the slider fields' height, so combos, number boxes, buttons and fields
        // share one centre line instead of each keeping the height its theme gives it.
        var optionsHeight = Controls.SliderField.DefaultHeight;
        foreach (var type in new[] { typeof(ComboBox), typeof(NumericUpDown), typeof(TextBox), typeof(Button) })
            styles.Add(new Style(x => x.OfType<StackPanel>().Class("options").Descendant().Is(type))
            {
                Setters = { new Setter(Layoutable.HeightProperty, optionsHeight), new Setter(Layoutable.MinHeightProperty, 0.0) }
            });
        styles.Add(new Style(x => x.OfType<StackPanel>().Class("options").Descendant().OfType<NumericUpDown>())
        {
            Setters = { new Setter(NumericUpDown.VerticalContentAlignmentProperty, VerticalAlignment.Center) }
        });
        styles.Add(new Style(x => x.OfType<StackPanel>().Class("options").Descendant().OfType<TextBox>())
        {
            Setters =
            {
                new Setter(Layoutable.MinHeightProperty, 0.0), new Setter(TemplatedControl.PaddingProperty, new Thickness(6, 0)),
                new Setter(TextBox.VerticalContentAlignmentProperty, VerticalAlignment.Center)
            }
        });
        styles.Add(new Style(x => x.OfType<StackPanel>().Class("options").Descendant().OfType<Button>())
        {
            Setters = { new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 0)), new Setter(ContentControl.VerticalContentAlignmentProperty, VerticalAlignment.Center) }
        });
        styles.Add(new Style(x => x.OfType<Button>().Class("flat"))
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent), new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(6)), new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(6))
            }
        });
        styles.Add(new Style(x => x.OfType<Button>().Class("dock-tab").Class(":pointerover")
            .Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>())
        {
            Setters = { new Setter(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, Hover) }
        });
        styles.Add(new Style(x => x.OfType<Button>().Class("dock-selected")
            .Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>())
        {
            Setters = { new Setter(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, ActiveTab) }
        });
        styles.Add(new Style(x => x.OfType<Button>().Class("alignment").Class(":disabled").Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>())
        {
            Setters = { new Setter(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, Brushes.Transparent) }
        });
        styles.Add(new Style(x => x.OfType<Button>().Class("alignment").Class(":disabled").Descendant().OfType<Viewbox>())
        {
            Setters = { new Setter(Visual.OpacityProperty, 0.48) }
        });
        styles.Add(new Style(x => x.OfType<Button>().Class("alignment").Class(":disabled").Descendant().OfType<TextBlock>())
        {
            Setters = { new Setter(Visual.OpacityProperty, 0.48) }
        });
        styles.Add(new Style(x => x.OfType<ComboBoxItem>().Class("font-preview").Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>())
        {
            Setters = { new Setter(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, Selected) }
        });
        styles.Add(new Style(x => x.OfType<ToggleButton>().Class("layout-toggle").Class(":checked").Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>())
        {
            Setters =
            {
                new Setter(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, Selected),
                new Setter(Avalonia.Controls.Presenters.ContentPresenter.BorderThicknessProperty, new Thickness(0))
            }
        });
        styles.Add(new Style(x => x.OfType<ToggleButton>().Class("tool").Class(":disabled").Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>())
        {
            Setters = { new Setter(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, Brushes.Transparent) }
        });
        styles.Add(new Style(x => x.OfType<ToggleButton>().Class("tool").Class(":disabled").Descendant().OfType<Viewbox>())
        {
            Setters = { new Setter(Visual.OpacityProperty, 0.48) }
        });
        styles.Add(new Style(x => x.OfType<ToggleButton>().Class("tool"))
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent), new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                new Setter(Layoutable.WidthProperty, 36.0), new Setter(Layoutable.HeightProperty, 33.0), new Setter(TemplatedControl.PaddingProperty, new Thickness(0)),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(7)),
                new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Center), new Setter(ContentControl.VerticalContentAlignmentProperty, VerticalAlignment.Center)
            }
        });
        return styles;
    }
}
