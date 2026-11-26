using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace Composa.App;

/// <summary>Interface icons on a 24-unit grid, drawn with the current foreground.</summary>
public static partial class Icons
{
    public sealed record Icon(string? Stroke, string? Fill = null, bool Dashed = false)
    {
        internal string? Asset { get; init; }
    }

    public static readonly Icon Move = Packed("move");
    public static readonly Icon Link = Packed("link");
    public static readonly Icon LayoutColumns = Packed("layout-columns");
    public static readonly Icon LayoutRows = Packed("layout-rows");
    public static readonly Icon LayoutSquares = Packed("layout-squares");
    public static readonly Icon Magnet = Packed("magnet");
    public static readonly Icon Unlink = Packed("unlink");
    public static readonly Icon Marquee = Packed("marquee");
    public static readonly Icon MarqueeEllipse = Packed("marquee-ellipse");
    public static readonly Icon Lasso = Packed("lasso");
    public static readonly Icon PolygonLasso = Packed("polygon-lasso");
    public static readonly Icon Wand = Packed("wand");
    public static readonly Icon Crop = Packed("crop");
    public static readonly Icon RemoveObject = Packed("remove-object");
    public static readonly Icon Bucket = Packed("bucket");
    public static readonly Icon Brush = Packed("brush");
    public static readonly Icon SelectionBrush = Packed("selection-brush");
    public static readonly Icon Eraser = Packed("eraser");
    public static readonly Icon VectorBend = Packed("vector-bend");
    public static readonly Icon VectorCut = Packed("vector-cut");
    public static readonly Icon Ai = Packed("ai");
    public static readonly Icon Assistant = Packed("assistant");
    public static readonly Icon Heal = Packed("heal");
    public static readonly Icon HealingBrush = Packed("healing-brush");
    public static readonly Icon Stamp = Packed("stamp");
    public static readonly Icon Drop = Packed("drop");
    public static readonly Icon Liquify = Packed("liquify");
    public static readonly Icon Smudge = Packed("smudge");
    public static readonly Icon Dodge = Packed("dodge");
    public static readonly Icon Burn = Packed("burn");
    public static readonly Icon Gradient = Packed("gradient");
    public static readonly Icon Shape = Packed("shape");
    public static readonly Icon Text = Packed("text");
    public static readonly Icon Eyedropper = Packed("eyedropper");
    public static readonly Icon Hand = Packed("hand");
    public static readonly Icon Zoom = Packed("zoom");
    public static readonly Icon ZoomIn = Packed("zoom-in");
    public static readonly Icon ZoomOut = Packed("zoom-out");
    public static readonly Icon Eye = Packed("eye");
    public static readonly Icon Folder = Packed("folder");
    public static readonly Icon Plus = Packed("plus");
    public static readonly Icon Image = Packed("image");
    public static readonly Icon Close = Packed("close");
    public static readonly Icon Trash = Packed("trash");
    public static readonly Icon Mask = Packed("mask");
    public static readonly Icon Adjust = Packed("adjust");
    public static readonly Icon ChevronRight = Packed("chevron-right");
    public static readonly Icon ChevronDown = Packed("chevron-down");
    public static readonly Icon ChevronUp = new("M6 15 L12 9 L18 15");
    public static readonly Icon ChevronLeft = new("M15 6 L9 12 L15 18");
    public static readonly Icon Sliders = new("M4 6h16M4 12h16M4 18h16 M6 6a2 2 0 1 0 4 0a2 2 0 1 0 -4 0Z " +
        "M14 12a2 2 0 1 0 4 0a2 2 0 1 0 -4 0Z M8 18a2 2 0 1 0 4 0a2 2 0 1 0 -4 0Z");
    public static readonly Icon Cpu = new("M7 5h10a2 2 0 0 1 2 2v10a2 2 0 0 1 -2 2H7a2 2 0 0 1 -2 -2V7a2 2 0 0 1 2 -2Z " +
        "M9 9h6v6H9Z M9 1v4M15 1v4M9 19v4M15 19v4M1 9h4M1 15h4M19 9h4M19 15h4");
    public static readonly Icon Coin = Packed("coin");
    public static readonly Icon ClipArrow = Packed("clip-arrow");
    public static readonly Icon Effects = Packed("effects");
    public static readonly Icon EyeOff = Packed("eye-off");
    public static readonly Icon ObjectSelect = Packed("object-select");
    public static readonly Icon Line = Packed("line");
    public static readonly Icon Pen = Packed("pen");
    public static readonly Icon Rectangle = Packed("rectangle");
    public static readonly Icon RoundedRectangle = Packed("rounded-rectangle");
    public static readonly Icon Ellipse = Packed("ellipse");
    public static readonly Icon FontSize = Packed("font-size");
    public static readonly Icon TextTop = Packed("text-top");
    public static readonly Icon TextBottom = Packed("text-bottom");
    public static readonly Icon TextMiddle = Packed("text-middle");
    public static readonly Icon LineHeight = Packed("line-height");
    public static readonly Icon LetterSpacing = Packed("letter-spacing");
    public static readonly Icon AlignLeft = Packed("align-left");
    public static readonly Icon AlignCenter = Packed("align-center");
    public static readonly Icon AlignRight = Packed("align-right");
    private const string PathTypeLetters = "M5 16 L8.5 6 L12 16 M6.5 12 H10.5 M15 20 V10 M15 10 H17 A3 3 0 0 1 17 16 H15";
    public static readonly Icon PathAscender = new(PathTypeLetters + " M2 6 Q12 2 22 6");
    public static readonly Icon PathCenter = new(PathTypeLetters + " M2 13 Q12 9 22 13");
    public static readonly Icon PathBaseline = new(PathTypeLetters + " M2 17 Q12 15 22 17");
    public static readonly Icon PathDescender = new(PathTypeLetters + " M2 21 Q12 19 22 21");
    public static readonly Icon Ruler = Packed("ruler");
    public static readonly Icon EnhancePrompt = Packed("enhance-prompt");
    public static readonly Icon PixelAlignment = Packed("pixel-alignment");
    public static readonly Icon Gear = Packed("gear");
    public static readonly Icon Run = Packed("run");
    public static readonly Icon MoreActions = Packed("more-actions");
    public static readonly Icon Layers = Packed("layers");
    public static readonly Icon Document = Packed("document");
    public static readonly Icon Disk = Packed("disk");
    public static readonly Icon Swap = Packed("swap");

    public static readonly Icon Patch = Packed("patch");

    public static Control Create(Icon icon, double size = 18, IBrush? brush = null)
    {
        if (icon.Asset is { } asset)
            return new Viewbox { Width = size, Height = size, Child = new PackIcon(asset, brush), IsHitTestVisible = false };

        var panel = new Panel { Width = 24, Height = 24 };
        if (icon.Fill != null)
        {
            var fill = new Path { Data = Geometry.Parse(icon.Fill) };
            Bind(fill, Avalonia.Controls.Shapes.Shape.FillProperty, brush);
            panel.Children.Add(fill);
        }
        if (icon.Stroke != null)
        {
            var stroke = new Path { Data = Geometry.Parse(icon.Stroke), StrokeThickness = 1.6, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round };
            if (icon.Dashed) stroke.StrokeDashArray = [2.2, 2.2];
            Bind(stroke, Avalonia.Controls.Shapes.Shape.StrokeProperty, brush);
            panel.Children.Add(stroke);
        }
        return new Viewbox { Width = size, Height = size, Child = panel, IsHitTestVisible = false };
    }

    private static void Bind(Path path, AvaloniaProperty<IBrush?> property, IBrush? brush)
    {
        if (brush != null) path.SetValue(property, brush);
        else path.Bind(property, path.GetResourceObservable("ComposaForeground"));
    }
}
