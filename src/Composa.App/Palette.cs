using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Composa.App;

public static class Palette
{
    public static readonly IBrush Window = new SolidColorBrush(Color.Parse("#222326"));
    public static readonly IBrush Panel = new SolidColorBrush(Color.Parse("#292B2F"));
    public static readonly IBrush PanelRaised = new SolidColorBrush(Color.Parse("#35383D"));
    public static readonly IBrush Divider = new SolidColorBrush(Color.Parse("#1A1A1A"));
    public static readonly IBrush Foreground = new SolidColorBrush(Color.Parse("#EEF0F2"));
    public static readonly IBrush Secondary = new SolidColorBrush(Color.Parse("#AFB3B9"));
    public static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#3D9BFF"));
    public static readonly IBrush Selected = new SolidColorBrush(Color.Parse("#384B63"));
    public static readonly IBrush Hover = new SolidColorBrush(Color.Parse("#464A50"));

    public static readonly IBrush Field = new SolidColorBrush(Color.Parse("#373A3E"));
    public static readonly IBrush Edge = new SolidColorBrush(Color.Parse("#585D64"));

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
        Set(new SolidColorBrush(Color.Parse("#30649B")), "AccentButtonBackground");
        Set(new SolidColorBrush(Color.Parse("#386FA8")), "AccentButtonBackgroundPointerOver");
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
        styles.Add(new Style(x => x.OfType<Button>().Class("alignment").Class(":disabled").Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>())
        {
            Setters = { new Setter(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, Brushes.Transparent) }
        });
        styles.Add(new Style(x => x.OfType<Button>().Class("alignment").Class(":disabled").Descendant().OfType<Viewbox>())
        {
            Setters = { new Setter(Visual.OpacityProperty, 0.48) }
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
