using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Composa.ColorManagement;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Controls;

/// <summary>A working-RGB picker whose spectrum is converted for display, while numeric fields keep document RGB.</summary>
public sealed class WorkingColorPicker : StackPanel
{
    private readonly Spectrum square, hueStrip;
    private readonly SliderField red, green, blue;
    private readonly TextBox hex;
    private float hue, saturation, value;
    private SKColor color;
    public SKColor Color => color;
    public event Action<SKColor>? Changed;

    public WorkingColorPicker(SKColor initial, RgbProfile profile)
    {
        Spacing = 10; color = initial.WithAlpha(255); color.ToHsv(out hue, out saturation, out value);
        square = new Spectrum(profile, false, Pick); hueStrip = new Spectrum(profile, true, Pick);
        Children.Add(Ui.Row(10, square, hueStrip));
        SliderField Channel(string name, byte start, Action<byte> set)
        {
            var field = new SliderField("", start, 0, 255) { Width = 250 };
            field.Changed += n => set((byte)Math.Round(n));
            Children.Add(Ui.Row(10, new TextBlock { Text = name, Width = 34 }, field)); return field;
        }
        red = Channel("Red", color.Red, c => Set(new(c, color.Green, color.Blue)));
        green = Channel("Green", color.Green, c => Set(new(color.Red, c, color.Blue)));
        blue = Channel("Blue", color.Blue, c => Set(new(color.Red, color.Green, c)));
        hex = new TextBox { Text = $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}", Width = 120, MaxLength = 7 };
        void ReadHex()
        {
            var text = hex.Text?.Trim().TrimStart('#');
            if (text?.Length == 6 && uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var number)) Set(new SKColor(number | 0xFF000000));
        }
        hex.LostFocus += (_, _) => ReadHex();
        hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ReadHex(); e.Handled = true; } };
        Children.Add(Ui.Row(10, new TextBlock { Text = "Hex", Width = 34 }, hex));
        Children.Add(Ui.Label(profile.Name + " · document RGB", Palette.Secondary, 11));
        Refresh();
    }

    private void Set(SKColor next)
    {
        color = next.WithAlpha(255); color.ToHsv(out var h, out var s, out var v);
        if (s > 0 && v > 0) hue = h;
        if (v > 0) saturation = s;
        value = v; Refresh(); Changed?.Invoke(color);
    }
    private void Pick(bool strip, double x, double y)
    {
        if (strip) hue = (float)(y * 359.999);
        else { saturation = (float)(x * 100); value = (float)((1 - y) * 100); }
        color = SKColor.FromHsv(hue, saturation, value); Refresh(); Changed?.Invoke(color);
    }
    private void Refresh()
    {
        square.Update(hue, saturation, value); hueStrip.Update(hue, saturation, value);
        red.Value = color.Red; green.Value = color.Green; blue.Value = color.Blue;
        hex.Text = $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
    }

    private sealed class Spectrum : Control
    {
        private readonly RgbProfile profile;
        private readonly bool strip;
        private readonly Action<bool, double, double> pick;
        private Bitmap? bitmap;
        private float paintedHue = -1, hue, saturation, value;
        public Spectrum(RgbProfile profile, bool strip, Action<bool, double, double> pick)
        {
            this.profile = profile; this.strip = strip; this.pick = pick;
            Width = strip ? 24 : 256; Height = 192; Focusable = true;
            Cursor = new Cursor(StandardCursorType.Cross);
            Avalonia.Automation.AutomationProperties.SetName(this, strip ? "Hue" : "Saturation and brightness");
        }
        public void Update(float hue, float saturation, float value)
        {
            this.hue = hue; this.saturation = saturation; this.value = value;
            if (bitmap == null || !strip && paintedHue != hue)
            {
                using var pixels = Pixels.NewWorkingColor((int)Width, (int)Height, ColorDepth.Eight, profile);
                var bytes = pixels.GetPixelSpan();
                for (var y = 0; y < pixels.Height; y++) for (var x = 0; x < pixels.Width; x++)
                {
                    var c = strip ? SKColor.FromHsv(y * 360f / (pixels.Height - 1), 100, 100)
                        : SKColor.FromHsv(hue, x * 100f / (pixels.Width - 1), (1 - y / (pixels.Height - 1f)) * 100);
                    var at = y * pixels.RowBytes + x * 4; bytes[at] = c.Red; bytes[at + 1] = c.Green; bytes[at + 2] = c.Blue; bytes[at + 3] = 255;
                }
                var next = Ui.ToAvaloniaBitmap(pixels, 256); var old = bitmap; bitmap = next; paintedHue = hue; old?.Dispose();
            }
            InvalidateVisual();
        }
        public override void Render(DrawingContext context)
        {
            if (bitmap == null) return;
            context.DrawImage(bitmap, new Rect(Bounds.Size));
            var point = strip ? new Point(Bounds.Width / 2, hue / 360 * Bounds.Height)
                : new Point(saturation / 100 * Bounds.Width, (1 - value / 100) * Bounds.Height);
            context.DrawEllipse(null, new Pen(Brushes.Black, 3), point, 5, 5);
            context.DrawEllipse(null, new Pen(Brushes.White, 1), point, 5, 5);
        }
        private void Pick(Point p) => pick(strip, Math.Clamp(p.X / Bounds.Width, 0, 1), Math.Clamp(p.Y / Bounds.Height, 0, 1));
        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e); if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            Focus(); e.Pointer.Capture(this); Pick(e.GetPosition(this)); e.Handled = true;
        }
        protected override void OnPointerMoved(PointerEventArgs e)
        { base.OnPointerMoved(e); if (e.Pointer.Captured == this) Pick(e.GetPosition(this)); }
        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        { base.OnPointerReleased(e); if (e.Pointer.Captured == this) { Pick(e.GetPosition(this)); e.Pointer.Capture(null); e.Handled = true; } }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
            if (strip) pick(true, 0, Math.Clamp(hue / 360 + (e.Key is Key.Down or Key.Right ? 1d : -1d) / 360, 0, 1));
            else pick(false, Math.Clamp(saturation / 100 + (e.Key == Key.Right ? .01 : e.Key == Key.Left ? -.01 : 0), 0, 1),
                Math.Clamp(1 - value / 100 + (e.Key == Key.Down ? .01 : e.Key == Key.Up ? -.01 : 0), 0, 1));
            e.Handled = true;
        }
    }
}
