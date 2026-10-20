using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Composa.App.Dialogs;
using Composa.ColorManagement;
using Composa.Model;

namespace Composa.App;

public sealed partial class MainWindow
{
    private async Task EditColorProfile(bool assign)
    {
        if (session is not { } target || canvas.IsDragging) return;
        var current = target.Document.Profile;
        var profiles = RgbProfile.BuiltIn.ToList();
        if (!profiles.Contains(current)) profiles.Add(current);
        var selected = current;
        var combo = new ComboBox { ItemsSource = profiles.Select(p => p.Name).ToList(), SelectedIndex = profiles.IndexOf(current), Width = 280 };
        combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) selected = profiles[combo.SelectedIndex]; };
        var dialogTitle = assign ? "Assign Color Profile" : "Convert Color Profile";
        var load = Ui.TextButton("Load ICC…", async () =>
        {
            var provider = TopLevel.GetTopLevel(combo)?.StorageProvider ?? StorageProvider;
            var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "RGB ICC Profile", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("RGB ICC Profile") { Patterns = ["*.icc", "*.icm"] }] });
            if (files.Count == 0) return;
            try
            {
                await using var input = await files[0].OpenReadAsync();
                using var bytes = new MemoryStream();
                var buffer = new byte[8192]; int count;
                while ((count = await input.ReadAsync(buffer)) > 0)
                { if (bytes.Length + count > DocumentLimits.MaxIccBytes) throw new InvalidDataException("The ICC profile is too large."); bytes.Write(buffer, 0, count); }
                var profile = RgbProfile.FromIcc(bytes.ToArray());
                var index = profiles.FindIndex(p => p.Equals(profile)); if (index < 0) { index = profiles.Count; profiles.Add(profile); }
                combo.ItemsSource = profiles.Select(p => p.Name).ToList(); combo.SelectedIndex = index; selected = profiles[index];
            }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
            { await Prompts.Alert(this, "RGB Profile", error.Message); }
        });
        var flatten = new CheckBox { Content = "Flatten image to preserve composite appearance", IsChecked = true, IsVisible = !assign };
        var currentLabel = Ui.Label("Current profile: " + current.Name, Palette.Secondary);
        currentLabel.MaxWidth = 390; currentLabel.TextTrimming = TextTrimming.CharacterEllipsis; ToolTip.SetTip(currentLabel, current.Name);
        var rows = Ui.Column(12, currentLabel, Ui.Row(8, combo, load),
            new TextBlock { Text = assign ? "Keeps RGB numbers and changes their meaning. The image's appearance may change."
                : "Converts RGB numbers to preserve color within the destination gamut. Keeping layers can change blends and adjustments.",
                MaxWidth = 390, TextWrapping = TextWrapping.Wrap }, flatten);
        if (!await new DialogWindow(dialogTitle, rows, assign ? "Assign" : "Convert").Ask(this)) return;
        try
        {
            if (assign) target.AssignColorProfile(selected); else target.ConvertColorProfile(selected, flatten.IsChecked == true);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException) { await Prompts.Alert(this, dialogTitle, error.Message); }
    }
}
