using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Composa.App;

/// <summary>
/// The strip that appears when a newer version exists. Deliberately not a dialog: nothing is
/// urgent about an update, and interrupting someone's work to say so would be rude.
/// </summary>
public sealed class UpdateNotice : Border
{
    private readonly TextBlock message = Ui.Label("", Palette.OnActive);
    private readonly Button install;

    public UpdateNotice()
    {
        IsVisible = false;
        Background = Palette.Active;
        Padding = new Thickness(12, 6);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto") };
        message.VerticalAlignment = VerticalAlignment.Center;
        Add(row, message, 0);
        install = Ui.TextButton("Download changes", () => Install?.Invoke());
        install.IsVisible = false;
        Add(row, install, 1);
        Add(row, Ui.TextButton("Release notes", () => OpenReleasePage?.Invoke()), 2);
        Add(row, Ui.TextButton("Skip this version", () => { Skip?.Invoke(); Hide(); }), 3);
        // Built here rather than through Ui.IconButton, which leaves the icon its default muted
        // foreground: readable on a dark panel, nearly invisible on this one.
        var dismiss = new Button { Content = Icons.Create(Icons.Close, 11, Palette.OnActive), Classes = { "flat" } };
        ToolTip.SetTip(dismiss, "Dismiss");
        dismiss.Click += (_, _) => Hide();
        Add(row, dismiss, 4);
        Child = row;
    }

    /// <summary>Opens the notes for the offered release.</summary>
    public event Action? OpenReleasePage;
    public event Action? Install;

    /// <summary>The user does not want to hear about this particular version again.</summary>
    public event Action? Skip;

    public void Show(ReleaseVersion version, bool canInstall = false)
    {
        message.Text = $"Composa {version} is available. You are running {AppInfo.Version}.";
        IsVisible = true;
        install.IsVisible = canInstall;
        SetAction("Download changes", true);
    }

    public void SetAction(string label, bool enabled)
    {
        install.Content = label;
        install.IsEnabled = enabled;
    }

    public void Hide() => IsVisible = false;

    private static void Add(Grid grid, Control control, int column)
    {
        control.Margin = new Thickness(column == 0 ? 0 : 8, 0, 0, 0);
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }

    /// <summary>Opens a URL with whatever the desktop uses for the job. A failure here is not worth reporting.</summary>
    public static void OpenInBrowser(string url)
    {
        try { using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or System.IO.IOException or InvalidOperationException) { }
    }

    /// <summary>Runs the check off the UI thread and brings the answer back onto it.</summary>
    public static void RunInBackground(UpdateCheck check, Action<UpdateResult> then) =>
        _ = Task.Run(async () =>
        {
            var result = await check.Run(manual: false);
            await Dispatcher.UIThread.InvokeAsync(() => then(result));
        });
}
