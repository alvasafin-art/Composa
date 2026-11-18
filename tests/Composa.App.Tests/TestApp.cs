using Avalonia;
using Avalonia.Headless;
using Composa.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestApp))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Composa.App.Tests;

public static class TestApp
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Initialize()
    {
        Settings.Persist = false;
        Composa.IO.ImageMagick.Bundled = BundledImageMagick.TryLoad;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
