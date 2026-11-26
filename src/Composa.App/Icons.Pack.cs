using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Platform;
using Path = Avalonia.Controls.Shapes.Path;

namespace Composa.App;

public enum IconStyle { Outline, Filled }

public static partial class Icons
{
    internal const string StyleResource = "ComposaIconStyle";

    internal static Icon Packed(string name) => new(null) { Asset = name };

    private static readonly Dictionary<(string Name, IconStyle Style), Glyph[]> glyphs = [];
    private static readonly XNamespace svg = "http://www.w3.org/2000/svg";
    private static readonly Regex groupTransform = new(@"^translate\(([^ ]+) ([^)]+)\) scale\(([^)]+)\)$");

    private sealed record Glyph(Geometry Geometry, Matrix Transform, bool Fill, double Stroke, double StrokeOpacity, IBrush? Mask);

    private static Glyph[] ReadGlyphs(string name, IconStyle style)
    {
        var folder = style == IconStyle.Filled ? "filled" : "outline";
        using var input = AssetLoader.Open(new Uri($"avares://composa/Assets/Icons/{folder}/{name}.svg"));
        var document = XElement.Load(input);
        var gradients = document.Descendants(svg + "linearGradient").ToDictionary(e => (string)e.Attribute("id")!, e =>
            new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = [.. e.Elements(svg + "stop").Select(stop => new GradientStop(
                    Color.FromArgb((byte)Math.Round(255 * Number(stop, "stop-opacity", 1)), 255, 255, 255), Number(stop, "offset")))]
            });

        return document.Descendants().Where(e => e.Name == svg + "path" || e.Name == svg + "rect").Select(e =>
        {
            var group = (string?)e.Parent?.Attribute("transform");
            var transform = Matrix.Identity;
            if (group != null)
            {
                var parts = groupTransform.Match(group).Groups;
                var scale = double.Parse(parts[3].Value, CultureInfo.InvariantCulture);
                transform = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(
                    double.Parse(parts[1].Value, CultureInfo.InvariantCulture), double.Parse(parts[2].Value, CultureInfo.InvariantCulture));
            }

            var fill = (string?)e.Attribute("fill");
            IBrush? mask = fill?.StartsWith("url(#", StringComparison.Ordinal) == true ? gradients[fill[5..^1]] : null;
            var radius = Number(e, "rx");
            var geometry = e.Name == svg + "rect"
                ? new RectangleGeometry(new Rect(Number(e, "x"), Number(e, "y"), Number(e, "width"), Number(e, "height")), radius, radius)
                : Geometry.Parse(((string?)e.Attribute("fill-rule") == "evenodd" ? "F0 " : "F1 ") + (string)e.Attribute("d")!);
            return new Glyph(geometry, transform, fill != "none", Number(e, "stroke-width"), Number(e, "stroke-opacity", 1), mask);
        }).ToArray();
    }

    private static double Number(XElement element, string attribute, double fallback = 0) =>
        (double?)element.Attribute(attribute) ?? fallback;

    private static Glyph[] GetGlyphs(string name, IconStyle style)
    {
        var key = (name, style);
        if (!glyphs.TryGetValue(key, out var shapes)) glyphs[key] = shapes = ReadGlyphs(name, style);
        return shapes;
    }

    internal static Control CreateRailIcon(Icon icon, double size) => icon.Asset is { } asset
        ? new Viewbox { Width = size, Height = size, Child = new RailGlyph(asset), IsHitTestVisible = false }
        : Create(icon, size);

    // Draw a rail icon as one retained visual. Hovering the button need not
    // revisit a control and its resource bindings for every SVG path.
    private sealed class RailGlyph : Control
    {
        private static readonly StyledProperty<IconStyle> VariantProperty = AvaloniaProperty.Register<RailGlyph, IconStyle>("Variant");
        private static readonly StyledProperty<IBrush?> BrushProperty = AvaloniaProperty.Register<RailGlyph, IBrush?>("Brush");
        private readonly string name;

        static RailGlyph() => AffectsRender<RailGlyph>(VariantProperty, BrushProperty);

        public RailGlyph(string name)
        {
            this.name = name;
            Width = Height = 24;
            ClipToBounds = true;
            Bind(VariantProperty, this.GetResourceObservable(StyleResource));
            Bind(BrushProperty, this.GetResourceObservable("ComposaForeground"));
        }

        public override void Render(DrawingContext context)
        {
            var brush = GetValue(BrushProperty);
            foreach (var glyph in GetGlyphs(name, GetValue(VariantProperty)))
            {
                using var transform = context.PushTransform(glyph.Transform);
                if (glyph.Fill)
                {
                    if (glyph.Mask is { } mask)
                    {
                        using var opacityMask = context.PushOpacityMask(mask, new Rect(Bounds.Size));
                        context.DrawGeometry(brush, null, glyph.Geometry);
                    }
                    else context.DrawGeometry(brush, null, glyph.Geometry);
                }
                if (glyph.Stroke <= 0) continue;
                using var opacity = context.PushOpacity(glyph.StrokeOpacity);
                context.DrawGeometry(null, new Pen(brush, glyph.Stroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round, miterLimit: 10), glyph.Geometry);
            }
        }
    }

    private sealed class PackIcon : Panel
    {
        private static readonly StyledProperty<IconStyle> VariantProperty =
            AvaloniaProperty.Register<PackIcon, IconStyle>("Variant");

        private readonly string name;
        private readonly IBrush? brush;

        public PackIcon(string name, IBrush? brush)
        {
            this.name = name;
            this.brush = brush;
            Width = Height = 24;
            ClipToBounds = true;
            Draw();
            Bind(VariantProperty, this.GetResourceObservable(StyleResource));
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == VariantProperty) Draw();
        }

        private void Draw()
        {
            Children.Clear();
            foreach (var glyph in GetGlyphs(name, GetValue(VariantProperty)))
            {
                if (glyph.Fill)
                {
                    var fill = Shape(glyph);
                    fill.OpacityMask = glyph.Mask;
                    Icons.Bind(fill, Avalonia.Controls.Shapes.Shape.FillProperty, brush);
                    Children.Add(fill);
                }
                if (glyph.Stroke > 0)
                {
                    var stroke = Shape(glyph);
                    stroke.Opacity = glyph.StrokeOpacity;
                    stroke.StrokeThickness = glyph.Stroke;
                    stroke.StrokeLineCap = PenLineCap.Round;
                    stroke.StrokeJoin = PenLineJoin.Round;
                    Icons.Bind(stroke, Avalonia.Controls.Shapes.Shape.StrokeProperty, brush);
                    Children.Add(stroke);
                }
            }
        }

        private static Path Shape(Glyph glyph) => new()
        {
            Data = glyph.Geometry,
            RenderTransform = new MatrixTransform(glyph.Transform),
            RenderTransformOrigin = RelativePoint.TopLeft
        };
    }
}
