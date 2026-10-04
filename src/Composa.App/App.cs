using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Composa.App;

public sealed class App : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        // Native controls otherwise inherit Windows' accent, which can turn the blue interface purple.
        var theme = new FluentTheme { DensityStyle = DensityStyle.Compact };
        theme.Palettes[ThemeVariant.Dark] = new ColorPaletteResources { Accent = Avalonia.Media.Color.Parse("#3D9BFF") };
        theme.Palettes[ThemeVariant.Light] = new ColorPaletteResources { Accent = Avalonia.Media.Color.Parse("#3D9BFF") };
        Styles.Add(theme);
        Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://composa/")) { Source = new Uri("avares://Avalonia.Controls.ColorPicker/Themes/Fluent/Fluent.xaml") });
        Styles.Add(Palette.Styles());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            _ = window.OpenPaths(desktop.Args ?? []);
            // An exception in an input handler would otherwise take the whole app down, and unsaved work with it.
            // The failure is reported and logged; the document stays open so it can be saved.
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Console.Error.WriteLine(e.Exception);
                e.Handled = true;
                window.ReportFailure(e.Exception);
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
