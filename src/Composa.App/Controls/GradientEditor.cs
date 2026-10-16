using Avalonia.Controls;
using Avalonia.Media;
using Avalonia;
using Composa.App.Dialogs;
using Composa.Model;
using Composa.ColorManagement;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Controls;

public sealed class GradientEditor : StackPanel
{
    private GradientRamp ramp;
    private readonly Window owner;
    private readonly Action<GradientRamp> changed;
    private readonly RampPreview preview;
    public GradientEditor(Window owner, GradientRamp ramp, Action<GradientRamp> changed)
    {
        this.owner = owner; this.ramp = ramp.Normalized(); this.changed = changed;
        preview = new(Ui.WorkingProfile(owner)); Spacing = 8; Build();
    }
    private void Set(GradientRamp next) { ramp = next; preview.Ramp = next.Normalized(); preview.InvalidateVisual(); changed(next.Normalized()); }
    private void Build()
    {
        Children.Clear();
        var preset = GradientRamp.Presets.FirstOrDefault(p => p.Value == ramp).Key ?? "Custom";
        Children.Add(Ui.Combo(new[] { "Custom" }.Concat(GradientRamp.Presets.Keys).ToArray(), preset, s => s, s => { if (s != "Custom") { Set(GradientRamp.Presets[s]); Build(); } }, 180));
        preview.Ramp = ramp.Normalized(); Children.Add(preview);
        Children.Add(Ui.Label("Color stops · positions in %", Palette.Secondary));
        for (var index = 0; index < ramp.Colors.Length; index++)
        {
            var at = index; var stop = ramp.Colors[at];
            var swatch = new Button { Content = "Color", Width = 80, Background = new SolidColorBrush(new SKColor(stop.Color).ToDisplay(Ui.WorkingProfile(owner))) };
            swatch.Click += async (_, _) =>
            {
                if (await Prompts.Color(owner, "Gradient stop", new SKColor(ramp.Colors[at].Color)) is { } color)
                { var stops = ramp.Colors.ToArray(); stops[at] = stops[at] with { Color = (uint)color }; Set(ramp with { Colors = stops }); swatch.Background = new SolidColorBrush(color.ToDisplay(Ui.WorkingProfile(owner))); }
            };
            var position = Ui.Number(stop.Position * 100, 0, 100, v => { var stops = ramp.Colors.ToArray(); stops[at] = stops[at] with { Position = v / 100 }; Set(ramp with { Colors = stops }); }, width: 70);
            Children.Add(Ui.Row(8, swatch, position, Ui.TextButton("Remove", () => { if (ramp.Colors.Length > 2) { Set(ramp with { Colors = ramp.Colors.Where((_, i) => i != at).ToArray() }); Build(); } })));
        }
        Children.Add(Ui.TextButton("Add color stop", () => { if (ramp.Colors.Length < 64) { Set(ramp with { Colors = ramp.Colors.Append(new GradientColorStop(.5, (uint)ramp.Normalized().Sample(.5))).ToArray() }); Build(); } }));
        Children.Add(Ui.Label("Opacity stops · positions in %", Palette.Secondary));
        for (var index = 0; index < ramp.Opacities.Length; index++)
        {
            var at = index; var stop = ramp.Opacities[at];
            var value = Ui.SliderField("Opacity", stop.Opacity * 100, 0, 100, v => { var stops = ramp.Opacities.ToArray(); stops[at] = stops[at] with { Opacity = v / 100 }; Set(ramp with { Opacities = stops }); }, width: 120);
            var position = Ui.Number(stop.Position * 100, 0, 100, v => { var stops = ramp.Opacities.ToArray(); stops[at] = stops[at] with { Position = v / 100 }; Set(ramp with { Opacities = stops }); }, width: 70);
            Children.Add(Ui.Row(8, value, position, Ui.TextButton("Remove", () => { if (ramp.Opacities.Length > 2) { Set(ramp with { Opacities = ramp.Opacities.Where((_, i) => i != at).ToArray() }); Build(); } })));
        }
        Children.Add(Ui.TextButton("Add opacity stop", () => { if (ramp.Opacities.Length < 64) { Set(ramp with { Opacities = ramp.Opacities.Append(new GradientOpacityStop(.5, .5)).ToArray() }); Build(); } }));
    }
    private sealed class RampPreview : Control
    {
        private readonly RgbProfile profile;
        private Avalonia.Media.Imaging.Bitmap? bitmap;
        public GradientRamp Ramp
        {
            set
            {
                using var image = Pixels.NewWorkingColor(286, 1, ColorDepth.Eight, profile);
                var bytes = image.GetPixelSpan();
                for (var x = 0; x < image.Width; x++)
                {
                    var color = value.Sample(x / (image.Width - 1d)); var at = x * 4;
                    bytes[at] = (byte)(color.Red * color.Alpha / 255); bytes[at + 1] = (byte)(color.Green * color.Alpha / 255);
                    bytes[at + 2] = (byte)(color.Blue * color.Alpha / 255); bytes[at + 3] = color.Alpha;
                }
                var next = Ui.ToAvaloniaBitmap(image, 286); var old = bitmap; bitmap = next; old?.Dispose();
            }
        }
        public RampPreview(RgbProfile profile) { this.profile = profile; Height = 26; Width = 286; }
        public override void Render(DrawingContext context)
        {
            var dark = new SolidColorBrush(Color.Parse("#555555")); var light = new SolidColorBrush(Color.Parse("#888888"));
            for (var y = 0; y < Bounds.Height; y += 8)
                for (var x = 0; x < Bounds.Width; x += 8) context.DrawRectangle(((x / 8 + y / 8) % 2 == 0) ? dark : light, null, new Rect(x, y, Math.Min(8, Bounds.Width - x), Math.Min(8, Bounds.Height - y)));
            if (bitmap != null) context.DrawImage(bitmap, new Rect(Bounds.Size));
        }
    }
}
