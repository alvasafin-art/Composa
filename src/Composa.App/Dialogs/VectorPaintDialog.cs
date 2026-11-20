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
        Control solid;
        if (profile.Equals(RgbProfile.Srgb) && display.Equals(RgbProfile.Srgb))
        {
            var picker = new ColorView
            {
                Color = rgb.WithAlpha(255).ToAvalonia(),
                IsAlphaEnabled = false,
                IsAlphaVisible = false,
                IsColorPaletteVisible = true,
                IsColorModelVisible = true,
                IsHexInputVisible = true,
                ColorSpectrumComponents = ColorSpectrumComponents.SaturationValue
            };
            picker.ColorChanged += (_, e) => { rgb = e.NewColor.ToSkia(); Update(); };
            solid = picker;
        }
        else
        {
            var picker = new WorkingColorPicker(rgb.WithAlpha(255), profile, display);
            picker.Changed += value => { rgb = value; Update(); };
            solid = picker;
        }
        var stops = new GradientEditor(dialog, gradient.Ramp, value => { gradient = gradient with { Ramp = value }; Update(); }, owner);
        var angle = Ui.AngleField("Angle", gradient.Angle, -180, 180, value => { gradient = gradient with { Angle = value }; Update(); }, 180);
        var gradientFields = Ui.Column(8, angle, Ui.SliderField("Scale (%)", gradient.Scale * 100, 1, 400,
            value => { gradient = gradient with { Scale = value / 100 }; Update(); }, width: 180),
            new ScrollViewer { Content = stops, MaxHeight = 330 });
        void ShowMode()
        {
            solid.IsVisible = mode == "Solid";
            gradientFields.IsVisible = !solid.IsVisible;
            angle.IsVisible = mode == "Linear";
        }
        body.Children.Add(Ui.Combo(new[] { "Solid", "Linear", "Radial" }, mode, value => value, value =>
        {
            mode = value; gradient = gradient with { Radial = value == "Radial" }; ShowMode(); Update();
        }, 160));
        body.Children.Add(Ui.SliderField("Opacity (%)", opacity * 100.0 / 255, 0, 100,
            value => { opacity = (byte)Math.Round(value * 255 / 100); Update(); }, width: 180));
        body.Children.Add(solid); body.Children.Add(gradientFields); ShowMode();
        return await dialog.Ask(owner);
    }
}
