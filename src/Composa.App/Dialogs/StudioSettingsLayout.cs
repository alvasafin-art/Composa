using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Composa.App.Dialogs;

internal static class StudioSettingsLayout
{
    internal static SettingsStatus Status(string text, bool ready = false, bool warning = false) => new(text, ready, warning);

    internal static Border Card(Control content, double padding = 14) => new()
    {
        Background = Palette.Panel, BorderBrush = Palette.SettingsLine, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8), Padding = new Thickness(padding), Child = content
    };

    internal static TextBlock Caption(string text) => new()
    {
        Text = Locale.T(text), FontSize = 11, Foreground = Palette.Secondary, TextWrapping = TextWrapping.Wrap
    };

    internal static TextBlock Heading(string text, double size = 14) => new()
    {
        Text = Locale.T(text), FontSize = size, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap
    };

    internal static Control Field(string label, Control field)
    {
        Avalonia.Automation.AutomationProperties.SetName(field, Locale.T(label));
        if (field is ComboBox combo && double.IsNaN(combo.Width)) combo.HorizontalAlignment = HorizontalAlignment.Stretch;
        return Ui.Column(4, Caption(label), field);
    }

    internal static TextBlock SetupLabel(string text, bool translate = true) => new()
    {
        Text = translate ? Locale.T(text) : text, FontSize = 10, Foreground = Palette.Secondary,
        VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap
    };

    internal static T SetupInput<T>(T input) where T : TemplatedControl
    {
        input.Width = double.NaN;
        input.Height = 34;
        input.MinHeight = 34;
        input.HorizontalAlignment = HorizontalAlignment.Stretch;
        input.Background = Palette.Field;
        input.BorderBrush = Palette.SettingsFieldLine;
        input.BorderThickness = new Thickness(1);
        input.CornerRadius = new CornerRadius(5);
        input.FontSize = 11;
        input.Padding = new Thickness(10, 0);
        if (input is TextBox text) text.VerticalContentAlignment = VerticalAlignment.Center;
        if (input is ComboBox combo) combo.VerticalContentAlignment = VerticalAlignment.Center;
        return input;
    }

    internal static Button SetupLink(string text, Action action, double fontSize = 11)
    {
        var link = Ui.TextButton(text, action);
        link.Classes.Add("flat");
        link.FontSize = fontSize;
        link.MinWidth = link.MinHeight = 0;
        link.Padding = new Thickness(0);
        link.Foreground = Palette.SettingsLink;
        link.VerticalAlignment = VerticalAlignment.Center;
        return link;
    }

    internal static StackPanel SetupField(string label, Control field, Control? action = null, TextBlock? caption = null)
    {
        caption ??= SetupLabel(label);
        Avalonia.Automation.AutomationProperties.SetName(field, caption.Text);
        Control heading = action == null ? caption : Columns("*,Auto", caption, action);
        heading.MinHeight = 15;
        return Ui.Column(4, heading, field);
    }

    internal static Grid SetupColumns(params Control[] children)
    {
        var grid = Columns("*,*", children);
        grid.ColumnSpacing = 14;
        return grid;
    }

    internal static Control SetupSection(string name, Control content, Border indicator)
    {
        var card = Card(content);
        card.Padding = new Thickness(16, 11);
        return Ui.Column(7, Columns("*,19", Heading(name, 14), indicator), card);
    }

    internal static Grid Columns(string widths, params Control[] children)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(widths), ColumnSpacing = 10 };
        for (var i = 0; i < children.Length; i++)
        {
            Grid.SetColumn(children[i], i);
            grid.Children.Add(children[i]);
        }
        return grid;
    }

    internal static Grid MasterDetail(Control master, Control detail, double wideWidth)
    {
        var grid = Columns("215,*", master, detail);
        grid.ColumnSpacing = 18;
        grid.SizeChanged += (_, args) =>
        {
            var width = args.NewSize.Width >= 825 ? wideWidth : 215;
            if (grid.ColumnDefinitions[0].Width.Value != width)
                grid.ColumnDefinitions[0].Width = new GridLength(width);
        };
        return grid;
    }

    internal static Border InsetRow(Control row, IBrush? background = null, double vertical = 7) => new()
    {
        Background = background, Padding = new Thickness(15, vertical),
        BorderBrush = Palette.SettingsLine, BorderThickness = new Thickness(0, 1, 0, 0), Child = row
    };

    internal static ScrollViewer Scroll(Control control) => new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = control
    };

    internal static Button RowButton(Control content, Action open)
    {
        var button = new Button
        {
            Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent,
            Padding = new Thickness(12, 8), MinHeight = 38
        };
        button.Classes.Add("flat");
        button.Click += (_, _) => open();
        return button;
    }
}

internal sealed class SettingsStatus : Border
{
    internal SettingsStatus(string text, bool ready, bool warning)
    {
        Width = Height = 19;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Center;
        CornerRadius = new CornerRadius(10);
        Background = ready ? Palette.SettingsSuccess : warning ? Palette.SettingsWarningLine : Palette.Field;
        Child = new TextBlock
        {
            Text = ready ? "✓" : warning ? "!" : "·", FontSize = 11, FontWeight = FontWeight.SemiBold,
            Foreground = ready ? Palette.SettingsSuccessText : warning ? Palette.SettingsWarningText : Palette.Secondary,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        Locale.Tip(this, text);
        Avalonia.Automation.AutomationProperties.SetName(this, Locale.T(text));
    }
}

internal sealed class StudioSettingsPages : ContentControl
{
    private readonly IReadOnlyList<Control> pages;
    private int selected;

    internal StudioSettingsPages(IReadOnlyList<Control> pages, int initial)
    {
        this.pages = pages;
        Name = "StudioSettingsPages";
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        selected = initial;
        Content = pages[initial];
    }

    internal event Action? PageChanged;
    internal int SelectedIndex
    {
        get => selected;
        set
        {
            if (selected == value) return;
            selected = value;
            Content = pages[value];
            PageChanged?.Invoke();
        }
    }
}

internal sealed class StudioSettingsWindow : DialogWindow
{
    internal StudioSettingsWindow(Window owner, Control workspace, TextBlock message, TextBlock scope, bool firstRun)
        : base(firstRun ? "Initial setup" : "Settings", workspace, firstRun ? "Check and continue" : "Save settings")
    {
        ((Panel)Content!).Children.Clear();
        SizeToContent = SizeToContent.Manual;
        CanResize = true;
        Icon = owner.Icon;
        Title = AppInfo.Name + " — " + Title;
        Background = Palette.Window;
        if (OperatingSystem.IsWindows()) FontFamily = new FontFamily("Segoe UI Variable, Segoe UI");
        Buttons.Margin = new Thickness(0);
        Buttons.Children.OfType<Button>().Last().IsDefault = false;
        foreach (var button in Buttons.Children.OfType<Button>()) button.MinHeight = 32;
        if (firstRun) SetCancelLabel(Locale.T("Skip setup"));
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        footer.Children.Add(Ui.Column(3, message, scope));
        Grid.SetColumn(Buttons, 1);
        footer.Children.Add(Buttons);
        var footerFrame = new Border
        {
            Background = Palette.SettingsSidebar, BorderBrush = Palette.SettingsLine,
            BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(34, 11), MinHeight = 62, Child = footer
        };
        var layout = new DockPanel();
        DockPanel.SetDock(footerFrame, Dock.Bottom);
        layout.Children.Add(footerFrame);
        layout.Children.Add(workspace);
        Content = layout;
        var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
        var scale = screen?.Scaling ?? 1;
        Width = Math.Min(firstRun ? 1158 : 1250, (screen?.WorkingArea.Width ?? 1280) / scale - 18);
        Height = Math.Min(780, (screen?.WorkingArea.Height ?? 800) / scale - 54);
        MinWidth = Math.Min(820, Width);
        MinHeight = Math.Min(460, Height);
    }
}
