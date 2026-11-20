using Avalonia.Controls;
using Avalonia.Layout;
using Composa.ColorManagement;
using SkiaSharp;

namespace Composa.App.Controls;

internal sealed class VectorColorFields : StackPanel
{
    private readonly WorkingColorPicker.Spectrum square, hueStrip;
    private readonly PaintValueField hex;
    private readonly List<PaintValueField> channels = [];
    private readonly StackPanel channelRow;
    private readonly Action<SKColor> changed;
    private SKColor color;
    private float hue, saturation, brightness;
    private bool cmyk;
    private double[] ink = new double[4];
    public VectorColorFields(RgbProfile profile, RgbProfile display, Action<SKColor> changed)
    {
        this.changed = changed; Spacing = 5;
        square = new(profile, display, false, Pick) { Width = 312, Height = 125 };
        hueStrip = new(profile, display, true, Pick, true) { Width = 312, Height = 13 };
        Children.Add(square); Children.Add(hueStrip);
        hex = new("PaintHex", 80, text => { if (PaintValueField.TryRgb(text, out var rgb)) Set(new SKColor(rgb | 0xff000000), true); });
        var space = Ui.Combo(new[] { "RGB", "CMYK" }, "RGB", x => x, x => { cmyk = x == "CMYK"; BuildChannels(); }, 80);
        space.Name = "PaintChannels";
        ToolTip.SetTip(space, "RGB uses the document profile. CMYK values use a mathematical RGB conversion without a printing profile.");
        Children.Add(Ui.Row(6, Ui.Label("HEX", Palette.Secondary), hex, space));
        channelRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        Children.Add(channelRow); BuildChannels();
    }
    private void BuildChannels()
    {
        channels.Clear(); channelRow.Children.Clear();
        string[] names = cmyk ? ["C", "M", "Y", "K"] : ["R", "G", "B"];
        for (int i = 0; i < names.Length; i++)
        {
            int index = i;
            var field = new PaintValueField("Paint" + names[i], cmyk ? 44 : 54, text =>
            {
                if (!PaintValueField.TryNumber(text, cmyk ? 100 : 255, out var value)) return;
                if (cmyk)
                {
                    ink[index] = value;
                    byte Rgb(int j) => (byte)Math.Round(255 * (1 - ink[j] / 100) * (1 - ink[3] / 100));
                    Set(new SKColor(Rgb(0), Rgb(1), Rgb(2)), true, keepInk: true);
                }
                else
                {
                    var values = new[] { color.Red, color.Green, color.Blue }; values[index] = (byte)Math.Round(value);
                    Set(new SKColor(values[0], values[1], values[2]), true);
                }
            });
            channels.Add(field); channelRow.Children.Add(Ui.Row(2, Ui.Label(names[i], Palette.Secondary, 11), field));
        }
        Refresh();
    }
    public void Show(SKColor next) => Set(next, false);
    private void Set(SKColor next, bool notify, bool keepInk = false)
    {
        next = next.WithAlpha(255);
        bool different = next != color; color = next;
        if (different)
        {
            next.ToHsv(out var h, out var s, out var v);
            if (s > 0 && v > 0) hue = h;
            if (v > 0) saturation = s;
            brightness = v;
        }
        if (!keepInk && different)
        {
            double max = Math.Max(next.Red, Math.Max(next.Green, next.Blue));
            ink = [max==0?0:100*(1-next.Red/max),max==0?0:100*(1-next.Green/max),
                max==0?0:100*(1-next.Blue/max),100*(1-max/255)];
        }
        Refresh(); if (notify) changed(color);
    }
    private void Pick(bool strip, double x, double y)
    {
        if (strip) hue = (float)(y * 359.999);
        else { saturation = (float)(x * 100); brightness = (float)((1 - y) * 100); }
        Set(SKColor.FromHsv(hue, saturation, brightness), true);
    }
    private void Refresh()
    {
        square.Update(hue, saturation, brightness); hueStrip.Update(hue, saturation, brightness);
        hex.Show($"{(uint)color & 0xffffff:X6}");
        var values = cmyk ? ink : new double[] { color.Red, color.Green, color.Blue };
        for (int i = 0; i < channels.Count; i++) channels[i].Show(values[i].ToString(cmyk ? "0.#" : "0", System.Globalization.CultureInfo.InvariantCulture));
    }
}
