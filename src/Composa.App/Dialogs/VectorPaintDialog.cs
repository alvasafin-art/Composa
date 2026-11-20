using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Composa.App.Controls;
using Composa.ColorManagement;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Dialogs;

internal static class VectorPaintDialog
{
    internal static async Task<bool> Edit(Window owner, string label, uint color, VectorGradient? current,
        Action<uint, VectorGradient?> preview)
    {
        var body = new StackPanel { Spacing = 12, Width = PaintLayout.Width };
        var dialog = new DialogWindow(label + " Color", body);
        var initial = new SKColor(color);
        uint other = Math.Min(initial.Red, Math.Min(initial.Green, initial.Blue)) > 215 ? 0xff222222 : 0xffffffff;
        var gradient = current ?? new VectorGradient(GradientRamp.Between(color | 0xff000000, other));
        var mode = current == null ? "Solid" : current.Radial ? "Radial" : "Linear";
        var rgb = new SKColor(color);
        var opacity = rgb.Alpha;
        void Update() => preview((uint)rgb.WithAlpha(opacity), mode == "Solid" ? null : gradient);
        var profile = Ui.WorkingProfile(owner); var display = Ui.DisplayProfile(owner);
        var stops = new VectorStopsEditor(gradient.Ramp, profile, display, value =>
        {
            gradient = gradient with { Ramp = value };
            Update();
        });
        var fields = new VectorColorFields(profile, display, value =>
        {
            if (mode == "Solid") { rgb = value; Update(); }
            else stops.SetColor(value);
        }, value =>
        {
            if (mode == "Solid") { opacity = value; Update(); }
            else stops.SetOpacity(value);
        });
        stops.SelectedColorChanged += fields.Show;
        var angle = Ui.SliderField("Angle °", gradient.Angle, -180, 180, value =>
        { gradient = gradient with { Angle = value }; Update(); }, width: double.NaN);
        var scale = Ui.SliderField("Scale %", gradient.Scale * 100, 1, 400, value =>
        { gradient = gradient with { Scale = value / 100 }; Update(); }, width: double.NaN);
        var paintOpacity = Ui.SliderField("Overall %", opacity * 100d / 255, 0, 100, value =>
        { opacity = (byte)Math.Round(value * 255 / 100); Update(); }, width: double.NaN);
        var geometryRow = PaintLayout.Columns("*,*,*", 4, angle, scale, paintOpacity);
        var geometry = Ui.Column(5, PaintLayout.Caption("Gradient settings"), geometryRow);
        var hexCaption = PaintLayout.Caption("HEX");
        void ShowMode()
        {
            stops.IsVisible = geometry.IsVisible = mode != "Solid";
            angle.IsVisible = mode == "Linear";
            geometryRow.ColumnDefinitions = new ColumnDefinitions(angle.IsVisible ? "*,*,*" : "*,*");
            Grid.SetColumn(scale, angle.IsVisible ? 1 : 0);
            Grid.SetColumn(paintOpacity, angle.IsVisible ? 2 : 1);
            paintOpacity.Value = Math.Round(opacity * 100d / 255);
            hexCaption.Text = mode == "Solid" ? "HEX" : "Selected stop · HEX";
            fields.Show(mode == "Solid" ? rgb.WithAlpha(opacity) : stops.SelectedColor);
        }
        var type = Ui.Combo(new[] { "Solid", "Linear", "Radial" }, mode, value => value, value =>
        {
            mode = value; gradient = gradient with { Radial = value == "Radial" }; ShowMode(); Update();
        }, double.NaN);
        type.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        body.Children.Add(PaintLayout.Columns("*,*", 10,
            Ui.Column(5, PaintLayout.Caption(label + " type"), type), Ui.Column(5, hexCaption, fields.HexEntry)));
        body.Children.Add(stops);
        body.Children.Add(geometry);
        body.Children.Add(fields);
        if (owner is MainWindow { Session: { } editor })
        {
            static IEnumerable<uint> PaintColors(ShapeStyle shape)
            {
                if (shape.FillGradient is { } fill)
                    foreach (var stop in fill.Ramp.Colors) yield return stop.Color;
                else yield return shape.Fill;
                if (shape.StrokeGradient is { } stroke)
                    foreach (var stop in stroke.Ramp.Colors) yield return stop.Color;
                else if (shape.Stroke is { } solid) yield return solid;
            }
            var palette = Ui.Row(4);
            foreach (var swatch in Document.Flatten(editor.Document.Layers).Where(l => l.Shape != null)
                .SelectMany(l => PaintColors(l.Shape!)).Select(c => c | 0xff000000).Distinct().Take(10))
            {
                var colorValue = new SKColor(swatch);
                var chip = new Button
                {
                    Width = 20,
                    Height = 20,
                    MinHeight = 0,
                    Padding = new Avalonia.Thickness(0),
                    Background = new Avalonia.Media.SolidColorBrush(colorValue.ToDisplay(profile, display))
                };
                ToolTip.SetTip(chip, $"#{swatch & 0xffffff:X6}");
                chip.Click += (_, _) =>
                {
                    if (mode == "Solid") { rgb = colorValue; fields.Show(rgb.WithAlpha(opacity)); Update(); }
                    else { stops.SetColor(colorValue); fields.Show(stops.SelectedColor); }
                };
                palette.Children.Add(chip);
            }
            if (palette.Children.Count > 0) body.Children.Add(Ui.Column(4, Ui.Label("Document colors", Palette.Secondary, 11), palette));
        }
        ShowMode();
        return await dialog.Ask(owner);
    }
}
