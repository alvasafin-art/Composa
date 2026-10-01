using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Composa.App.AI;

internal static class AiGenerationControls
{
    public static Border Split(Button generate, ComboBox variants)
    {
        generate.Height = variants.Height = 24;
        generate.MinHeight = variants.MinHeight = 0;
        generate.Padding = new Thickness(14, 0); variants.Padding = new Thickness(9, 0);
        generate.HorizontalContentAlignment = HorizontalAlignment.Center;
        generate.VerticalContentAlignment = VerticalAlignment.Center;
        generate.CornerRadius = new CornerRadius(6, 0, 0, 6);
        variants.CornerRadius = new CornerRadius(0, 6, 6, 0);
        generate.BorderThickness = variants.BorderThickness = new Thickness(0);
        variants.MinWidth = 0; variants.Width = 60; variants.Background = new SolidColorBrush(Color.Parse("#151515"));
        ToolTip.SetTip(variants, "Number of variants · 1, 2 or 3");
        return new Border { Name = "AiGenerateSplit", Height = 26, CornerRadius = new CornerRadius(6), ClipToBounds = true,
            BorderBrush = new SolidColorBrush(Color.Parse("#4A4A4A")), BorderThickness = new Thickness(1), Child = Ui.Row(0, generate, variants) };
    }
}
