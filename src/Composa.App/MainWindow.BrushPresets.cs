using Avalonia.Controls;
using Composa.App.Dialogs;
using Composa.Editing;
using Avalonia;
using Avalonia.Media;
using Avalonia.Layout;
using Avalonia.Controls.Primitives;
using Composa.Painting;
namespace Composa.App;

public sealed partial class MainWindow
{
    private Button BrushDynamicsMenu(EditorSession target)
    {
        var button = new Button { Content = Locale.T("Dynamics ▾"), Padding = new Thickness(8, 0) };
        button.Click += (_, _) =>
        {
            var body = Ui.Column(10,
                Ui.Check("Pen pressure controls size", target.Brush.PressureSize, v => target.Brush = target.Brush with { PressureSize = v }),
                Ui.Check("Pen pressure controls flow", target.Brush.PressureFlow, v => target.Brush = target.Brush with { PressureFlow = v }),
                Ui.Check("Full brush outline", target.Brush.FullCursor, v => { target.Brush = target.Brush with { FullCursor = v }; canvas.InvalidateCanvas(); }),
                Ui.SliderField("Smoothing", target.Brush.Smoothing, 0, 100, v => target.Brush = target.Brush with { Smoothing = v }, width: 230));
            body.Margin = new Thickness(12); var flyout = new Flyout { Content = body }; flyout.ShowAt(button);
        };
        ToolTip.SetTip(button, Locale.T("Pen pressure, smoothing, cursor outline and saved brushes")); return button;
    }
    private static Control TipSample(BrushSettings brush, double size)
    {
        var fill = new RadialGradientBrush();
        var ink = Palette.Foreground.Color;
        fill.GradientStops.Add(new GradientStop(ink, 0));
        fill.GradientStops.Add(new GradientStop(ink, Math.Clamp(brush.Hardness, 0, .99)));
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(0, ink.R, ink.G, ink.B), 1));
        return new Avalonia.Controls.Shapes.Ellipse { Width = size, Height = size, Fill = fill };
    }

    private Button BrushLibrary(EditorSession target)
    {
        bool eraser = target.Tool == Tool.Eraser;
        var custom = eraser ? settings.EraserPresets : settings.BrushPresets;
        var title = eraser ? "Eraser presets" : "Brush presets";
        var button = new Button { Name = "BrushPresetPicker", Content = Ui.Row(6, TipSample(target.Brush, 17), Ui.Label(Locale.T("Presets")), Icons.Create(Icons.ChevronDown, 12)) };
        ToolTip.SetTip(button, Locale.T(title));
        var flyout = new Flyout();
        button.Flyout = flyout;
        flyout.Opening += (_, _) =>
        {
            var list = Ui.Column(4);
            void Entry(BrushPreset preset, bool saved)
            {
                var applied = saved ? preset.Settings : preset.Settings with { Size = target.Brush.Size, Opacity = target.Brush.Opacity };
                var selected = applied == target.Brush;
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("32,*,Auto"), ColumnSpacing = 8 };
                grid.Children.Add(TipSample(preset.Settings, 27));
                AddAt(grid, Ui.Label(saved ? preset.Name : Locale.T(preset.Name)), 1);
                AddAt(grid, Ui.Label(selected ? "✓" : $"{preset.Settings.Hardness:P0}", Palette.Secondary, 11), 2);
                var entry = new Button { Classes = { "flat" }, Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 4),
                    Background = selected ? Palette.Selected : Brushes.Transparent };
                entry.Click += (_, _) => { target.Brush = applied; RememberToolSettings(); flyout.Hide(); RebuildOptions(); canvas.InvalidateCanvas(); };
                list.Children.Add(entry);
            }
            var factory = BrushPresets.All;
            var tips = eraser ? factory.Where(p => p.Name is "Hard round" or "Soft eraser") : factory.Where(p => p.Name != "Soft eraser");
            foreach (var preset in tips.OrderBy(p => p.Name == "Hard round" ? 0 : p.Name.StartsWith("Soft ", StringComparison.Ordinal) ? 1 : 2))
                Entry(preset, false);
            if (custom.Count > 0) list.Children.Add(Ui.Label(Locale.T("My presets"), Palette.Secondary, 11));
            foreach (var pair in custom.OrderBy(p => p.Key, StringComparer.CurrentCultureIgnoreCase))
                Entry(new BrushPreset(pair.Key, pair.Value), true);
            var save = Ui.TextButton("Save current tip…", () => { flyout.Hide(); _ = SaveBrushPreset(target.Brush, eraser); });
            flyout.Content = Ui.Column(8, Ui.Label(Locale.T(title), weight: FontWeight.SemiBold),
                new ScrollViewer { Content = list, MaxHeight = 300 }, save);
            ((Control)flyout.Content).Width = 228;
        };
        return button;
    }

    private async Task SaveBrushPreset(BrushSettings brush, bool eraser)
    {
        var presets = eraser ? settings.EraserPresets : settings.BrushPresets;
        var name = new TextBox { Text = Locale.T(eraser ? "My eraser" : "My brush"), Width = 240, MaxLength = 100 };
        var error = Ui.Label("", Palette.Secondary, 12);
        var dialog = new DialogWindow(eraser ? "Save eraser preset" : "Save brush preset", Ui.Column(5, Ui.Label(Locale.T("Name")), name, error));
        dialog.ValidateAccept = () =>
        {
            var value = name.Text?.Trim();
            error.Text = string.IsNullOrEmpty(value) ? Locale.T("Enter a name") :
                (presets.Keys.Concat(BrushPresets.All.Select(p => p.Name)).Contains(value, StringComparer.OrdinalIgnoreCase) ||
                 BrushPresets.All.Any(p => string.Equals(Locale.T(p.Name), value, StringComparison.OrdinalIgnoreCase)))
                    ? Locale.T("This name is already in use") : "";
            return error.Text.Length == 0;
        };
        if (!await dialog.Ask(this)) return;
        presets[name.Text!.Trim()] = brush;
        settings.Save();
        RebuildOptions();
    }
}
