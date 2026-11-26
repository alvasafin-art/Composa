using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Composa.App.Controls;

/// <summary>A color sample whose boundary is visible only when the paint blends into its surrounding chrome.</summary>
internal sealed class ColorSwatch : Control
{
    private Border? nativeSample;
    public static readonly StyledProperty<IBrush?> BackgroundProperty = Border.BackgroundProperty.AddOwner<ColorSwatch>();
    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty = Border.CornerRadiusProperty.AddOwner<ColorSwatch>();
    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public CornerRadius CornerRadius { get => GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    static ColorSwatch() => AffectsRender<ColorSwatch>(BackgroundProperty, CornerRadiusProperty);

    public override void Render(DrawingContext context)
    {
        if (Background == null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var bounds = new Rect(Bounds.Size);
        var radius = CornerRadius.TopLeft;
        if (nativeSample == null) context.DrawRectangle(Background, null, bounds, radius, radius);
        if (Bounds.Width < 2 || Bounds.Height < 2) return;
        var surround = this.GetVisualAncestors().Where(visual => visual != nativeSample).Select(visual => visual switch
        {
            Border border => border.Background,
            Panel panel => panel.Background,
            TemplatedControl control => control.Background,
            _ => null
        }).OfType<ISolidColorBrush>().FirstOrDefault(brush => brush.Color.A == 255 && brush.Opacity == 1)?.Color ?? Palette.Window.Color;
        var needsEdge = Background switch
        {
            ISolidColorBrush paint => NeedsEdge(paint.Color, surround, paint.Opacity),
            IGradientBrush ramp => ramp.GradientStops.Count > 0 && ramp.GradientStops.All(stop => NeedsEdge(stop.Color, surround, ramp.Opacity)),
            _ => false
        };
        if (!needsEdge) return;
        context.DrawRectangle(null, ContrastPen, bounds.Deflate(.5), Math.Max(0, radius - .5), Math.Max(0, radius - .5));
    }

    internal static IPen ContrastPen => new Pen(new SolidColorBrush(Palette.Edge.Color, .55), 1);

    internal static void OutlineNativeSample(Border sample)
    {
        if (sample.Child != null) return;
        sample.Child = new ColorSwatch
        {
            nativeSample = sample, IsHitTestVisible = false,
            [!BackgroundProperty] = sample.GetObservable(Border.BackgroundProperty).ToBinding(),
            [!CornerRadiusProperty] = sample.GetObservable(Border.CornerRadiusProperty).ToBinding()
        };
    }

    internal static bool NeedsEdge(Color paint, Color surround, double opacity = 1)
    {
        var alpha = paint.A / 255d * opacity;
        int red = paint.R - surround.R, green = paint.G - surround.G, blue = paint.B - surround.B;
        return (red * red + green * green + blue * blue) * alpha * alpha < 23 * 23;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Palette.Edge.PropertyChanged += ThemeChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Palette.Edge.PropertyChanged -= ThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void ThemeChanged(object? sender, AvaloniaPropertyChangedEventArgs e) => InvalidateVisual();
}
