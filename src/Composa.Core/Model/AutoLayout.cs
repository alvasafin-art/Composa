using SkiaSharp;

namespace Composa.Model;

public enum LayoutDirection { Column, Row }
public enum LayoutAlignment { Start, Center, End }
public enum LayoutSizeMode { Content, Fixed, Fill }
public enum LayoutImageFit { Fit, Crop }

public sealed record LayoutInsets(double Top = 0, double Right = 0, double Bottom = 0, double Left = 0)
{
    internal LayoutInsets Normalized() => new(Size(Top), Size(Right), Size(Bottom), Size(Left));
    internal static double Size(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, DocumentLimits.MaxSide) : 0;
}

/// <summary>The group's frame starts at a document-space anchor. Its inner size comes from its children.</summary>
public sealed record AutoLayout(double X, double Y, LayoutDirection Direction, LayoutAlignment Alignment, double Gap,
    LayoutInsets Padding, double FillReference = 0)
{
    internal AutoLayout Normalized() => this with
    {
        X = float.IsFinite((float)X) ? X : 0, Y = float.IsFinite((float)Y) ? Y : 0,
        Direction = Enum.IsDefined(Direction) ? Direction : LayoutDirection.Column,
        Alignment = Enum.IsDefined(Alignment) ? Alignment : LayoutAlignment.Start,
        Gap = LayoutInsets.Size(Gap), Padding = Padding?.Normalized() ?? new(),
        FillReference = LayoutInsets.Size(FillReference)
    };
}

/// <summary>Fixed dimensions are frame dimensions. Fill is available on the group's cross axis only.</summary>
public sealed record AutoLayoutItem(LayoutSizeMode WidthMode, LayoutSizeMode HeightMode, double Width, double Height,
    LayoutImageFit ImageFit = LayoutImageFit.Fit)
{
    internal AutoLayoutItem Normalized() => new(
        Enum.IsDefined(WidthMode) ? WidthMode : LayoutSizeMode.Content,
        Enum.IsDefined(HeightMode) ? HeightMode : LayoutSizeMode.Content,
        Math.Max(1, LayoutInsets.Size(Width)), Math.Max(1, LayoutInsets.Size(Height)),
        Enum.IsDefined(ImageFit) ? ImageFit : LayoutImageFit.Fit);
}

/// <summary>A non-destructive image frame in document pixels, separate from the image and its masks.</summary>
public sealed record LayoutImageFrame(double X, double Y, double Width, double Height)
{
    public SKRect Bounds => SKRect.Create((float)X, (float)Y, (float)Width, (float)Height);
    internal bool IsValid => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height)
        && Width is > 0 and <= DocumentLimits.MaxSide && Height is > 0 and <= DocumentLimits.MaxSide
        && float.IsFinite((float)(X + Width)) && float.IsFinite((float)(Y + Height));
}

public sealed partial class Layer
{
    public AutoLayout? AutoLayout { get; set; }
    public AutoLayoutItem? AutoLayoutItem { get; set; }
    public LayoutImageFrame? LayoutFrame { get; set; }

    public bool SupportsLayoutImageFrame => Pixels != null && Text == null && Shape == null && !HasWarp
        && Transform.Rotation == 0 && Transform.Distort == null;

    internal void TranslateLayout(double dx, double dy)
    {
        if (AutoLayout is { } layout) AutoLayout = layout with { X = layout.X + dx, Y = layout.Y + dy };
        if (LayoutFrame is { } frame) LayoutFrame = frame with { X = frame.X + dx, Y = frame.Y + dy };
    }
}
