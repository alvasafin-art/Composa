using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Composa.App.Controls;

internal sealed class PaintValueField : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);
    private string model = "";
    public PaintValueField(string name, double width, Action<string> apply)
    {
        Name = name; Width = width; MinWidth = 0; MinHeight = 22; Height = 24; FontSize = 12;
        Padding = new Thickness(3, 0); VerticalContentAlignment = VerticalAlignment.Center;
        Foreground = Brushes.White; Background = Brushes.Transparent; BorderThickness = new Thickness(0);
        Avalonia.Automation.AutomationProperties.SetName(this, name);
        void Commit()
        {
            var value = Text?.Trim() ?? "";
            if (value != model) apply(value);
            Text = model;
        }
        LostFocus += (_, _) => Commit();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); SelectAll(); e.Handled = true; }
            if (e.Key == Key.Escape) { Text = model; SelectAll(); e.Handled = true; }
        };
    }
    public void Show(string value)
    {
        if (!IsFocused || Text == model) Text = value;
        model = value;
    }
    public static bool TryRgb(string value, out uint rgb)
    {
        value = value.TrimStart('#');
        if (value.Length == 3) value = string.Concat(value.Select(c => new string(c, 2)));
        rgb = 0;
        return value.Length == 6 && uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rgb);
    }
    public static bool TryNumber(string value, double max, out double number) =>
        double.TryParse(value.TrimEnd('%').Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
        && double.IsFinite(number) && number >= 0 && number <= max;
}
