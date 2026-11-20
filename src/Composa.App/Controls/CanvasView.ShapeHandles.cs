using Avalonia;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Controls;

public sealed partial class CanvasView
{
    public event Action<SKPoint>? CreateShapeRequested;
    private Layer? adjustingShape;
    private ShapeStyle? originalShape;
    private SKPoint startLocal;
    private int shapeParameter;
    private bool separateCorner;

    // The first four handles are corner radii. A radial shape also has a count handle
    // on the right, and the star has a handle for the depth of its notches.
    private IEnumerable<(int Index, SKPoint Local)> ShapeHandles(Layer layer)
    {
        if (layer.Shape is not {} st || layer.Pixels is not {} image || (!st.IsRectangle() && !st.IsRadial())) yield break;
        float w=image.Width, h=image.Height;
        var offset = (float)Math.Min(Math.Min(w,h)*.2, 14 / Math.Max(.01, UnitsPerPixel * Math.Abs(layer.Transform.Width/w)));
        for (int i=0; i<(st.IsRectangle()?4:1); i++)
        {
            var d=(float)Math.Min(Math.Min(w,h)*.45,offset+st.RadiusAt(i)*.55);
            yield return (i, st.IsRectangle() ? new(i is 1 or 2?w-d:d, i>=2?h-d:d) : new(w/2,d));
        }
        if (st.IsRadial()) yield return (4,new(w-offset,h/2));
        if (st.Kind==ShapeKind.Star) yield return (5,new(w/2,(float)(h/2+st.InnerRatio*h*.45)));
    }
    private bool ShowShapeParameters() => session?.ActiveLayer is {} layer && session.Document.SelectedLayerIds.Count==1 &&
        !session.IsEditingMask && session.WarpEdit==null && !session.PixelsLocked(layer) &&
        (session.Tool==Tool.Shape || session.Tool==Tool.Move && ShowTransformControls);
    private int HitShapeParameter(Point screen)
    {
        if (!ShowShapeParameters() || session?.ActiveLayer is not {} layer) return -1;
        foreach (var (index, local) in ShapeHandles(layer))
            if (Distance(ToScreen(layer.Matrix.MapPoint(local)), screen) <= 6) return index;
        return -1;
    }

    private int hoveredParameter = -1;
    private void UpdateShapeHint(Point position)
    {
        int index = drag == Drag.None ? HitShapeParameter(position) : -1;
        if (hoveredParameter == index) return;
        hoveredParameter = index;
        string? hint = index switch
        {
            4 => "Drag left or right to change the count",
            5 => "Drag to adjust the inner radius",
            >= 0 when session?.ActiveLayer?.Shape?.IsRectangle() == true => "Drag to round corners · Alt changes one corner",
            >= 0 => "Drag to round corners",
            _ => null
        };
        Locale.Tip(this, hint);
    }

    private bool BeginShapeParameter(Point screen, bool alt)
    {
        int index = HitShapeParameter(screen);
        if (index < 0 || session?.ActiveLayer is not { Shape: {} style } layer || !layer.Matrix.TryInvert(out var inverse)) return false;
        adjustingShape = layer;
        originalShape = style;
        startLocal = inverse.MapPoint(pressDocument);
        shapeParameter = index;
        separateCorner = alt || style.Corners != null;
        session.Begin("Shape Properties");
        drag = Drag.ShapeParameter;
        return true;
    }

    private void UpdateShapeParameter()
    {
        if (session == null || adjustingShape is not { Pixels: {} pixels } layer ||
            originalShape is not {} original || !layer.Matrix.TryInvert(out var inverse)) return;

        ShapeStyle next = original;
        if (Distance(pressScreen, cursorScreen) >= 2)
        {
            var delta = inverse.MapPoint(currentDocument) - startLocal;
            if (shapeParameter == 4)
            {
                int steps = (int)Math.Round((cursorScreen.X - pressScreen.X) / 8);
                next = original with { Points = Math.Clamp(original.Points + steps, 3, 64) };
            }
            else if (shapeParameter == 5)
            {
                double ratio = original.InnerRatio + delta.Y / (pixels.Height * .45);
                next = original with { InnerRatio = Math.Clamp(ratio, .05, .95) };
            }
            else
            {
                double inward = delta.Y;
                if (original.IsRectangle())
                {
                    double horizontal = shapeParameter is 1 or 2 ? -delta.X : delta.X;
                    double vertical = shapeParameter >= 2 ? -delta.Y : delta.Y;
                    inward = (horizontal + vertical) / 2;
                }
                double radius = Math.Clamp(original.RadiusAt(shapeParameter) + inward / .55, 0, Math.Min(pixels.Width, pixels.Height) / 2d);
                next = original.IsRectangle() && separateCorner ? original.WithCorner(shapeParameter, radius) : original.WithRadius(radius);
            }
        }
        if (next != layer.Shape) session.ChangeShapeStyle(layer, next, preview: true);
    }
}
