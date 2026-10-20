using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Composa.Filters;

namespace Composa.App.Dialogs;

public static partial class AdjustmentDialogs
{
    private static Control ColorLookupEditor(Window owner, ColorLookupAdjustment lookup, Action<Adjustment> update)
    {
        var title = Ui.Label("", Palette.Secondary); title.Name = "LookupTitle"; title.Width = FieldWidth; title.TextTrimming = TextTrimming.CharacterEllipsis;
        var details = Ui.Label("", Palette.Secondary); details.Name = "LookupDetails";
        var error = new TextBlock { Name = "LookupError", MaxWidth = FieldWidth, TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 10 }; var detached = false; var lifetime = new CancellationTokenSource(); var token = lifetime.Token;
        panel.DetachedFromVisualTree += (_, _) => { detached = true; lifetime.Cancel(); lifetime.Dispose(); };
        void Refresh()
        { title.Text = lookup.Table?.Title ?? "No table loaded"; ToolTip.SetTip(title, title.Text); details.Text = lookup.Table?.Description ?? "Load a .cube color preset"; }
        var load = new Button { Content = "Load .cube…", Name = "LoadLookup" };
        load.Click += async (_, _) =>
        {
            load.IsEnabled = false; error.Text = "";
            try
            {
                var provider = TopLevel.GetTopLevel(panel)?.StorageProvider ?? owner.StorageProvider;
                var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Color Lookup", AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("Cube color lookup") { Patterns = ["*.cube"] }] });
                if (files.Count == 0 || detached) return;
                await using var input = await files[0].OpenReadAsync();
                if (input.CanSeek && input.Length > Composa.Model.DocumentLimits.MaxLutTextBytes) throw new InvalidDataException("The Cube file is too large.");
                using var reader = new StreamReader(input, leaveOpen: true);
                var table = await Task.Run(() => CubeLut.Read(reader, Path.GetFileNameWithoutExtension(files[0].Name), token), token);
                if (detached) return;
                update(lookup with { Table = table }); lookup = lookup with { Table = table }; Refresh();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
            { if (!detached) error.Text = e.Message; }
            finally { if (!detached) load.IsEnabled = true; }
        };
        var clear = Ui.TextButton("Clear", () => { update(lookup = lookup with { Table = null }); Refresh(); }); clear.Name = "ClearLookup";
        var space = Ui.Combo(Enum.GetValues<LookupRgbSpace>(), lookup.Space, s => s switch
        { LookupRgbSpace.Srgb => "sRGB", LookupRgbSpace.WorkingRgb => "Document RGB", LookupRgbSpace.DisplayP3 => "Display P3", _ => "RGB (1998)" }, s => update(lookup = lookup with { Space = s }), FieldWidth); space.Name = "LookupSpace";
        var strength = Ui.SliderField("Strength", lookup.Strength, 0, 100, v => update(lookup = lookup with { Strength = v }), .1, "0.0", FieldWidth, reset: 100); strength.Name = "LookupStrength";
        foreach (var child in new Control[] { Ui.Row(8, load, clear), title, details, Ui.Label("Table RGB", Palette.Secondary), space, strength,
            new TextBlock { Text = "Use sRGB for most photo presets. Choose the color space the table was made for. Camera Log presets need matching source encoding. The table is saved inside your project.", MaxWidth = FieldWidth, TextWrapping = TextWrapping.Wrap }, error }) panel.Children.Add(child);
        Refresh(); return panel;
    }
}
