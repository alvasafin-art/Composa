using Avalonia;

namespace Composa.App;

internal static class Program
{
    internal static string? FailedUpdateVersion { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        var restartedByUpdater = false;
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).OnRestarted(_ => restartedByUpdater = true).Run();
        // The stdio bridge an MCP client launches: no window, just bytes carried to the running application.
        // With --launch it also starts the application when nothing answers.
        if (args is ["--mcp", ..]) return Mcp.McpBridge.RunAsync(Mcp.McpPipe.Name, launch: args.Contains("--launch")).GetAwaiter().GetResult();
        if (args is ["--exchange", ..]) return Mcp.McpBridge.RunAsync(Mcp.McpPipe.ExchangeName, launch: false).GetAwaiter().GetResult();
        if (DeltaUpdates.ForInstallation() is { } updater)
        {
            if (updater.UpdatePendingRestart is { } pending)
            {
                // The old version can be reopened by Velopack after a failed apply.
                // Only a later editor launch may consume the one offline retry.
                if (restartedByUpdater) FailedUpdateVersion = pending.Version.ToString();
                else if (updater.RetryOnLaunch(pending, args)) return 0;
            }
            else updater.CancelOfflineRetry();
        }
#if BUNDLED_IMAGEMAGICK
        IO.ImageMagick.Bundled = BundledImageMagick.TryLoad;
#endif
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
