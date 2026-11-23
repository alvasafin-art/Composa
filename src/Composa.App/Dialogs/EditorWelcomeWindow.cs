using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Composa.App.Dialogs;

internal sealed class EditorWelcomeWindow : Window
{
    private const double PreviewAspect = 16.0 / 9;
    private const double Inset = 22, Gap = 14, CardInset = 8, CaptionInset = 12, CaptionGap = 13;
    private const double OptionsChromeHeight = 202;
    private readonly Window owner;
    private readonly Dictionary<string, Bitmap> previews = [];
    private EditorSetup selection;
    private int step;

    internal EditorWelcomeWindow(Window owner, EditorSetup initial)
    {
        this.owner = owner;
        selection = initial;
        Title = AppInfo.Name;
        Icon = owner.Icon;
        Background = Palette.Panel;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Closed += (_, _) =>
        {
            foreach (var preview in previews.Values) preview.Dispose();
            previews.Clear();
        };
        RenderPage();
    }

    private void RenderPage()
    {
        var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
        double scaling = screen?.Scaling ?? 1;
        double availableWidth = (screen?.WorkingArea.Width ?? 1280) / scaling;
        double availableHeight = (screen?.WorkingArea.Height ?? 800) / scaling;
        var columns = step == 1 ? 3 : 2;
        var horizontalInsets = Inset * 2 + Gap * (columns - 1) + columns * CardInset * 2;
        Width = step == 0
            ? Math.Min(480, availableWidth - 40)
            : Math.Min(Math.Min(1360, availableWidth - 40),
                (availableHeight - OptionsChromeHeight - 42) * PreviewAspect * columns + horizontalInsets);
        var previewHeight = (Width - horizontalInsets) / columns / PreviewAspect;
        Height = step == 0 ? 154 : Math.Ceiling(previewHeight + OptionsChromeHeight);
        if (IsVisible && screen != null)
        {
            var center = owner.PointToScreen(new Point(owner.ClientSize.Width / 2, owner.ClientSize.Height / 2));
            var area = screen.WorkingArea;
            var width = (int)Math.Ceiling(Width * scaling);
            var height = (int)Math.Ceiling((Height + 28) * scaling);
            Position = new PixelPoint(Math.Clamp(center.X - width / 2, area.X, Math.Max(area.X, area.Right - width)),
                Math.Clamp(center.Y - height / 2, area.Y, Math.Max(area.Y, area.Bottom - height)));
        }

        string heading = step switch
        {
            1 => "Interface theme",
            2 => "Workspace layout",
            3 => "AI panel placement",
            _ => "Welcome to Composa"
        };
        Control body = step switch
        {
            1 => Choices(selection.Theme, EditorSetup.ThemeLabel, theme => selection with { Theme = theme }, previewHeight),
            2 => Choices(selection.Layout, EditorSetup.LayoutLabel, layout => selection with { Layout = layout }, previewHeight),
            3 => Choices(selection.AiPanel, EditorSetup.AiPanelLabel, placement => selection with { AiPanel = placement }, previewHeight),
            _ => new TextBlock { Text = Locale.T("Let's set up the app."), Foreground = Palette.Secondary,
                VerticalAlignment = VerticalAlignment.Top }
        };
        var title = Ui.Label(Locale.T(heading), weight: FontWeight.SemiBold);
        title.FontSize = 21;
        var next = Ui.TextButton(step == 3 ? "Done" : "Next", Advance, accent: true);
        next.Name = "WelcomeNext";
        next.MinWidth = 90;
        next.Height = 31;
        next.IsDefault = true;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (step > 0)
        {
            var back = Ui.TextButton("Back", () => { step--; RenderPage(); });
            back.Name = "WelcomeBack";
            back.MinWidth = 84;
            back.Height = next.Height;
            footer.Children.Add(back);
        }
        footer.Children.Add(next);
        var page = new Grid { Margin = new Thickness(Inset),
            RowDefinitions = new RowDefinitions(step == 0 ? "Auto,6,*,12,Auto" : "Auto,15,*,20,Auto") };
        page.Children.Add(title);
        Grid.SetRow(body, 2);
        page.Children.Add(body);
        Grid.SetRow(footer, 4);
        page.Children.Add(footer);
        Content = page;
        if (IsVisible) next.Focus();
    }

    private Control Choices<T>(T current, Func<T, string> label, Func<T, EditorSetup> withChoice, double previewHeight)
        where T : struct, Enum
    {
        var values = Enum.GetValues<T>();
        var row = new UniformGrid { Rows = 1, Columns = values.Length, Margin = new Thickness(-Gap / 2, 0) };
        foreach (var value in values)
        {
            var image = new Image { Source = GetPreview(withChoice(value)), Height = previewHeight, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
            var radio = new RadioButton { Content = Locale.T(label(value)), GroupName = "WelcomeOptions",
                IsChecked = value.Equals(current), Tag = value, MinHeight = 30,
                Margin = new Thickness(CaptionInset, 0, CaptionInset, 4) };
            var card = new Border
            {
                Background = Palette.Window, BorderBrush = value.Equals(current) ? Palette.Accent : Palette.Edge,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
                Padding = new Thickness(CardInset - 1), Margin = new Thickness(Gap / 2, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Child = new StackPanel { Spacing = CaptionGap, Children = { image, radio } }
            };
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked == true) selection = withChoice(value);
                card.BorderBrush = radio.IsChecked == true ? Palette.Accent : Palette.Edge;
            };
            card.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) { radio.IsChecked = true; radio.Focus(); }
            };
            row.Children.Add(card);
        }
        return row;
    }

    private Bitmap GetPreview(EditorSetup setup)
    {
        string theme = setup.Theme switch { EditorTheme.DarkNeutral => "dark", EditorTheme.LightNeutral => "light", _ => "classic" };
        string layout = setup.Layout == EditorLayoutPreset.Modern ? "modern" : "classic";
        string ai = setup.AiPanel == AiPanelPlacement.Sidebar ? "dock" : "inline";
        string path = $"{Locale.Language}-{theme}-{layout}-{ai}.webp";
        if (previews.TryGetValue(path, out var preview)) return preview;
        using var stream = AssetLoader.Open(new Uri($"avares://composa/Assets/Welcome/{path}"));
        return previews[path] = new Bitmap(stream);
    }

    private void Advance()
    {
        if (step == 3) { Close(selection); return; }
        step++;
        RenderPage();
    }
}
