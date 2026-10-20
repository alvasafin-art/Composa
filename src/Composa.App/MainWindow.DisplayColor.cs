using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Composa.App.Dialogs;
using Composa.App.DisplayColor;
using Composa.ColorManagement;

namespace Composa.App;

public enum DisplayColorMode { Automatic, Srgb, Custom }

public sealed partial class MainWindow
{
    public RgbProfile DisplayProfile { get; private set; } = RgbProfile.Srgb;
    private string displayDescription = "sRGB";
    private readonly DispatcherTimer displayColorTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private int displayColorRevision;

    private void InitializeDisplayColor()
    {
        displayColorTimer.Tick += async (_, _) => { displayColorTimer.Stop(); await RefreshDisplayColor(); };
        Opened += async (_, _) => await RefreshDisplayColor();
        PositionChanged += (_, _) => { displayColorTimer.Stop(); displayColorTimer.Start(); };
        Activated += (_, _) => { displayColorTimer.Stop(); displayColorTimer.Start(); };
        Closed += (_, _) => { displayColorTimer.Stop(); displayColorRevision++; };
    }

    internal async Task RefreshDisplayColor()
    {
        var revision = ++displayColorRevision;
        var x = Position.X; var y = Position.Y; var width = (int)(ClientSize.Width * RenderScaling); var height = (int)(ClientSize.Height * RenderScaling);
        var mode = settings.DisplayColorMode; var path = settings.DisplayProfilePath;
        var native = TryGetPlatformHandle() is { Handle: not 0 };
        DisplayProfileInfo info;
        try
        {
            info = mode == DisplayColorMode.Custom && !string.IsNullOrWhiteSpace(path)
                ? await Task.Run(() => { var profile = SystemDisplayProfile.Load(path); return new DisplayProfileInfo(profile, "Custom display · " + profile.Name, path); })
                : mode == DisplayColorMode.Srgb || !native ? new(RgbProfile.Srgb, "Display · sRGB")
                : await Task.Run(() => SystemDisplayProfile.Read(x, y, width, height));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { info = new(RgbProfile.Srgb, "Display ICC unavailable · using sRGB"); }
        if (revision != displayColorRevision) return;
        ApplyDisplayProfile(info.Profile, info.Description);
    }

    internal void ApplyDisplayProfile(RgbProfile profile, string description)
    {
        displayDescription = description;
        if (DisplayProfile.Equals(profile)) return;
        DisplayProfile = profile; canvas.DisplayProfile = profile; layers.DisplayProfile = profile; channels.DisplayProfile = profile; navigator.DisplayProfile = profile;
        foreach (var reference in aiReferences) reference.UpdateDisplay(profile);
        RefreshAiReferenceUi();
        UpdateColors(); RebuildOptions();
    }

    private async Task EditDisplayColor()
    {
        var mode = settings.DisplayColorMode; var path = settings.DisplayProfilePath;
        var choices = new[] { "System display profile", "sRGB / system-managed display", "Custom display ICC" };
        var combo = new ComboBox { ItemsSource = choices, SelectedIndex = Enum.IsDefined(mode) ? (int)mode : 0, Width = 300 };
        var fileLabel = Ui.Label(path == null ? "No custom profile selected" : Path.GetFileName(path), Palette.Secondary);
        fileLabel.MaxWidth = 250; fileLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        Action validate = () => { };
        var load = Ui.TextButton("Choose display ICC…", async () =>
        {
            var provider = TopLevel.GetTopLevel(combo)?.StorageProvider ?? StorageProvider;
            var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Display RGB ICC Profile", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("RGB ICC Profile") { Patterns = ["*.icc", "*.icm"] }] });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } selected) return;
            try { _ = await Task.Run(() => SystemDisplayProfile.Load(selected)); path = selected; fileLabel.Text = Path.GetFileName(path); combo.SelectedIndex = 2; validate(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { await Prompts.Alert(this, "Display Profile", error.Message); }
        });
        var description = Ui.Label(displayDescription, Palette.Secondary); description.MaxWidth = 420; description.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(description, displayDescription);
        var dialog = new DialogWindow("Display Color", Ui.Column(12, description, combo, Ui.Row(10, load, fileLabel),
            new TextBlock { MaxWidth = 420, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, TextWrapping = TextWrapping.Wrap, Text = "Display conversion affects previews only. Exports keep the document's profile. On Windows use System; with automatic system color management enabled, keep System or sRGB. Otherwise choose the ICC supplied for your calibrated display." }));
        validate = () => dialog.CanAccept = combo.SelectedIndex != 2 || path != null && File.Exists(path);
        combo.SelectionChanged += (_, _) => validate(); validate();
        if (!await dialog.Ask(this)) return;
        settings.DisplayColorMode = (DisplayColorMode)combo.SelectedIndex; settings.DisplayProfilePath = path; settings.Save();
        await RefreshDisplayColor();
    }
}
