using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using static Composa.App.Dialogs.StudioSettingsLayout;

namespace Composa.App.Dialogs;

internal sealed class EditorWelcomeWindow : Window
{
    internal sealed record Choice(EditorSetup Setup, string Language);

    private readonly Dictionary<string, Bitmap> previews = [];
    private readonly Image preview = new() { Stretch = Stretch.Uniform, Name = "WelcomePreview" };
    private Choice selection;

    internal EditorWelcomeWindow(Window owner, Choice initial)
    {
        selection = initial;
        Title = Locale.T("Initial setup");
        Icon = owner.Icon;
        Background = Palette.Window;
        if (OperatingSystem.IsWindows()) FontFamily = new FontFamily("Segoe UI Variable, Segoe UI");
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Closed += (_, _) =>
        {
            preview.Source = null;
            foreach (var image in previews.Values) image.Dispose();
        };
        var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
        var scale = screen?.Scaling ?? 1;
        Width = Math.Min(1050, (screen?.WorkingArea.Width ?? 1280) / scale - 24);
        Height = Math.Min(720, (screen?.WorkingArea.Height ?? 800) / scale - 54);
        MinWidth = Math.Min(650, Width);
        MinHeight = Math.Min(430, Height);
        RenderOptions.SetBitmapInterpolationMode(preview, BitmapInterpolationMode.HighQuality);
        preview.MaxHeight = Math.Clamp(Height - 355, 158, 370);

        var languages = Ui.Combo(new[] { "en", "ru" }, selection.Language, code => code == "ru" ? "Русский" : "English", code =>
        {
            selection = selection with { Language = code };
            Locale.Apply(code, owner, this);
            RefreshPreview();
        }, double.NaN);
        var themes = Ui.Combo(Enum.GetValues<EditorTheme>(), selection.Setup.Theme, theme => Locale.T(EditorSetup.ThemeLabel(theme)), theme =>
        {
            selection = selection with { Setup = selection.Setup with { Theme = theme } };
            (Application.Current as App)?.ApplyTheme(theme);
            RefreshPreview();
        }, double.NaN);
        languages.Name = "WelcomeLanguage";
        themes.Name = "WelcomeTheme";
        var content = Ui.Column(13, Heading("Set up your interface", 25),
            Card(Columns("*,*", Field("Language", languages), Field("Interface theme", themes)), 16),
            Options("Workspace layout", selection.Setup.Layout, EditorSetup.LayoutLabel,
                layout => layout == EditorLayoutPreset.Modern ? "Compact tools and panels" : "Classic editor panel arrangement",
                layout => selection.Setup with { Layout = layout }),
            Options("AI panel placement", selection.Setup.AiPanel, EditorSetup.AiPanelLabel,
                placement => placement == AiPanelPlacement.Sidebar ? "Docked beside the workspace" : "Appears beside the selected area",
                placement => selection.Setup with { AiPanel = placement }),
            Card(preview, 7));
        content.Margin = new Thickness(34, 25, 34, 20);
        var skip = Ui.TextButton("Skip setup", () => Close(null));
        skip.Name = "WelcomeSkip";
        var next = Ui.TextButton("Continue", () => Close(selection), accent: true);
        next.Name = "WelcomeNext";
        next.IsDefault = true;
        var footer = new Border
        {
            Background = Palette.SettingsSidebar, BorderBrush = Palette.SettingsLine, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(34, 11), MinHeight = 62,
            Child = Columns("*,Auto", Caption("You can finish setup later in Settings."), Ui.Row(8, skip, next))
        };
        var dock = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);
        dock.Children.Add(Scroll(content));
        Content = dock;
        RefreshPreview();
    }

    private Control Options<T>(string group, T current, Func<T, string> label, Func<T, string> description,
        Func<T, EditorSetup> choose) where T : struct, Enum
    {
        var cards = new List<Control>();
        foreach (var value in Enum.GetValues<T>())
        {
            var item = new RadioButton { Content = Locale.T(label(value)), Tag = value, GroupName = group, IsChecked = value.Equals(current) };
            var card = Card(Ui.Column(1, item, Caption(description(value))), 9);
            card.BorderBrush = item.IsChecked == true ? Palette.Accent : Palette.SettingsLine;
            card.Background = item.IsChecked == true ? Palette.SettingsSelection : Palette.Panel;
            card.PointerPressed += (_, args) =>
            {
                if (args.GetCurrentPoint(card).Properties.IsLeftButtonPressed) item.IsChecked = true;
            };
            item.IsCheckedChanged += (_, _) =>
            {
                card.BorderBrush = item.IsChecked == true ? Palette.Accent : Palette.SettingsLine;
                card.Background = item.IsChecked == true ? Palette.SettingsSelection : Palette.Panel;
                if (item.IsChecked != true) return;
                selection = selection with { Setup = choose(value) };
                RefreshPreview();
            };
            cards.Add(card);
        }
        return Ui.Column(7, Heading(group, 14), Columns("*,*", cards.ToArray()));
    }

    private void RefreshPreview()
    {
        var setup = selection.Setup;
        string theme = setup.Theme switch { EditorTheme.DarkNeutral => "dark", EditorTheme.LightNeutral => "light", _ => "classic" };
        string layout = setup.Layout == EditorLayoutPreset.Modern ? "modern" : "classic";
        string ai = setup.AiPanel == AiPanelPlacement.Sidebar ? "dock" : "inline";
        string path = $"{selection.Language}-{theme}-{layout}-{ai}.webp";
        if (!previews.TryGetValue(path, out var image))
        {
            using var stream = AssetLoader.Open(new Uri($"avares://composa/Assets/Welcome/{path}"));
            previews[path] = image = new Bitmap(stream);
        }
        preview.Source = image;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape) Close(null);
    }
}
