using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Composa.App;

public sealed class App : Application
{
    static App()
    {
        Popup.ChildProperty.Changed.AddClassHandler<Popup>((popup, change) =>
        {
            // The popup host otherwise intercepts clicks even if its tooltip content ignores them.
            if (change.GetNewValue<Control?>() is ToolTip) popup.IsHitTestVisible = false;
        });
    }

    private FluentTheme fluent = null!;
    internal EditorTheme CurrentTheme { get; private set; }

    internal void ApplyTheme(EditorTheme selection)
    {
        if (selection == CurrentTheme) return;
        CurrentTheme = selection;
        Palette.Use(selection);
        var variant = selection == EditorTheme.LightNeutral ? ThemeVariant.Light : ThemeVariant.Dark;
        fluent.Palettes[variant].Accent = Palette.Accent.Color;
        // Use a neutral fill for selected controls while keeping focus rings and links legible.
        void State(string key, object value)
        {
            if (selection == EditorTheme.Classic) Resources.Remove(key);
            else Resources[key] = value;
        }
        foreach (var (state, fill) in new[] { ("", Palette.Active), ("PointerOver", Palette.ActiveHover), ("Pressed", Palette.ActivePressed) })
        {
            State("ToggleButtonBackgroundChecked" + state, fill);
            State("ToggleButtonForegroundChecked" + state, Palette.OnActive);
            State("ToggleButtonBorderBrushChecked" + state, Palette.Edge);
            State("CheckBoxCheckBackgroundFillChecked" + state, fill);
            State("CheckBoxCheckBackgroundStrokeChecked" + state, Palette.Edge);
            State("CheckBoxCheckGlyphForegroundChecked" + state, Palette.OnActive);
            State("AccentButtonForeground" + state, Palette.OnActive);
        }
        RequestedThemeVariant = variant;
    }

    public override void Initialize()
    {
        var settings = Settings.Load();
        Locale.Language = settings.Language;
        Composa.Vision.ModelRunner.PreferGpu = settings.UseSelectionGpu;
        Composa.Rendering.GpuAcceleration.Configure(settings.UseRasterFilterGpu, settings.UseEditorGpu);
        Palette.Use(EditorTheme.Classic);
        RequestedThemeVariant = ThemeVariant.Dark;
        // Native controls otherwise inherit Windows' accent, which can turn the blue interface purple.
        fluent = new FluentTheme { DensityStyle = DensityStyle.Compact };
        fluent.Palettes[ThemeVariant.Dark] = new ColorPaletteResources { Accent = Avalonia.Media.Color.Parse("#3D9BFF") };
        fluent.Palettes[ThemeVariant.Light] = new ColorPaletteResources { Accent = Avalonia.Media.Color.Parse("#3D9BFF") };
        Styles.Add(fluent);
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
