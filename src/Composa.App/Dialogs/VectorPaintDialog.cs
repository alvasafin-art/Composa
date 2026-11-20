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
        var body = new StackPanel { Spacing = 8, Width = 360 };
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
        });
        stops.SelectedColorChanged += value => fields.Show(value);
        var angle = Ui.AngleField("Angle", gradient.Angle, -180, 180, value =>
        { gradient = gradient with { Angle = value }; Update(); }, 150);
        var scale = Ui.SliderField("Scale (%)", gradient.Scale * 100, 1, 400, value =>
        { gradient = gradient with { Scale = value / 100 }; Update(); }, width: 140);
        var geometry = Ui.Row(8, angle, scale);
        void ShowMode()
        {
            stops.IsVisible = geometry.IsVisible = mode != "Solid";
            angle.IsVisible = mode == "Linear";
            fields.Show(mode == "Solid" ? rgb : stops.SelectedColor);
        }
        var type = Ui.Combo(new[] { "Solid", "Linear", "Radial" }, mode, value => value, value =>
        {
            mode = value; gradient = gradient with { Radial = value == "Radial" }; ShowMode(); Update();
        }, 125);
        body.Width = 312;
        body.Children.Add(Ui.Row(8, type, Ui.SliderField("Opacity (%)", opacity * 100d / 255, 0, 100,
            value => { opacity = (byte)Math.Round(value * 255 / 100); Update(); }, width: 148)));
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
                chip.Click += (_, _) => { fields.Show(colorValue); if (mode == "Solid") { rgb = colorValue; Update(); } else stops.SetColor(colorValue); };
                palette.Children.Add(chip);
            }
            if (palette.Children.Count > 0) body.Children.Add(Ui.Column(4, Ui.Label("Document colors", Palette.Secondary, 11), palette));
        }
        ShowMode();
        return await dialog.Ask(owner);
    }
}
