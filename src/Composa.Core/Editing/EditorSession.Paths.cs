using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;
namespace Composa.Editing;

public sealed partial class EditorSession
{
    public Layer AddPath(VectorPath documentPath)
    {
        documentPath = documentPath.Normalized();
        if (documentPath.Nodes.Length < 2) throw new ArgumentException("A path needs at least two finite nodes.");
        var nodes = documentPath.Nodes;
        var left = Math.Floor(nodes.Min(n => Math.Min(n.X, Math.Min(n.InX, n.OutX))) - 2);
        var top = Math.Floor(nodes.Min(n => Math.Min(n.Y, Math.Min(n.InY, n.OutY))) - 2);
        var width = Math.Max(2, (int)Math.Ceiling(nodes.Max(n => Math.Max(n.X, Math.Max(n.InX, n.OutX))) - left + 2));
        var height = Math.Max(2, (int)Math.Ceiling(nodes.Max(n => Math.Max(n.Y, Math.Max(n.InY, n.OutY))) - top + 2));
        if (!DocumentLimits.FitsSurface(width, height)) throw new InvalidOperationException("The path exceeds the surface size limit.");
        var local = documentPath with { Nodes = nodes.Select(n => new BezierNode((n.X - left) / width, (n.Y - top) / height, (n.InX - left) / width, (n.InY - top) / height, (n.OutX - left) / width, (n.OutY - top) / height) { StartsContour = n.StartsContour }).ToArray() };
        var style = new ShapeStyle(ShapeKind.Path, (uint)Foreground, 0) { Path = local, FillEnabled = documentPath.Closed, Stroke = (uint)Foreground, StrokeWidth = 2 };
        var layer = Layer.Raster(document.UniqueName("Path"), RenderWorkingShape(style, width, height), left, top); layer.Shape = style;
        Apply("New Path", () => document.InsertAboveActive(layer)); InvalidateAll(); LayersChanged?.Invoke(); return layer;
    }
    public void ConvertShapeToPath(Layer layer)
    {
        if (layer.Shape is not {} style || style.Kind == ShapeKind.Path || layer.Pixels is not {} pixels || PixelsLocked(layer)) return;
        ChangeShapeStyle(layer, WithEditedPath(style, ShapeOutline(style, pixels)));
    }

    /// <summary>The outline in layer-local coordinates, including live shapes before their first point edit.</summary>
    public static VectorPath? EditableShapePath(Layer layer) =>
        layer.Shape is { } style && layer.Pixels is { } pixels
            ? style.Kind == ShapeKind.Path ? style.Path : ShapeOutline(style, pixels)
            : null;

    private static VectorPath ShapeOutline(ShapeStyle style, SKBitmap pixels)
    {
        double w=pixels.Width, h=pixels.Height;
        if (style.IsRadial() || style.Corners != null) return style.ParametricOutline(w, h, style.Stroke == null ? 0 : Math.Min(style.StrokeWidth, Math.Min(w,h))/2);
        var nodes=new List<BezierNode>();
        void Corner(double x,double y)
        {
            var point=BezierNode.Corner(x/w,y/h);
            if(nodes.Count==0 || nodes[^1].X!=point.X || nodes[^1].Y!=point.Y) nodes.Add(point);
        }
        void Arc(double x1,double y1,double x2,double y2,double x,double y)
        {
            nodes[^1]=nodes[^1] with { OutX=x1/w, OutY=y1/h };
            nodes.Add(BezierNode.Corner(x/w,y/h) with { InX=x2/w, InY=y2/h });
        }
        bool line=style.Kind==ShapeKind.Line;
        if(line)
        {
            double dx=Math.Min(style.LineWidth,w)/2, dy=Math.Min(style.LineWidth,h)/2;
            Corner(style.StartX*w ?? dx, style.StartY*h ?? dy);
            Corner(style.EndX*w ?? w-dx, style.EndY*h ?? h-dy);
            if(nodes.Count==1) nodes.Add(nodes[0]);
        }
        else
        {
            double inset=style.Stroke==null ? 0 : Math.Min(style.StrokeWidth,Math.Min(w,h))/2;
            double l=inset,t=inset,r=w-inset,b=h-inset;
            double radius=style.Kind==ShapeKind.RoundedRectangle ? Math.Clamp(style.CornerRadius-inset,0,Math.Min(r-l,b-t)/2) : 0;
            double rx=style.Kind==ShapeKind.Ellipse ? (r-l)/2 : radius;
            double ry=style.Kind==ShapeKind.Ellipse ? (b-t)/2 : radius;
            if(rx==0 || ry==0) { Corner(l,t); Corner(r,t); Corner(r,b); Corner(l,b); }
            else
            {
                const double k=.5522847498307936;
                Corner(r-rx,t); Arc(r-rx+k*rx,t,r,t+ry-k*ry,r,t+ry);
                Corner(r,b-ry); Arc(r,b-ry+k*ry,r-rx+k*rx,b,r-rx,b);
                Corner(l+rx,b); Arc(l+rx-k*rx,b,l,b-ry+k*ry,l,b-ry);
                Corner(l,t+ry); Arc(l,t+ry-k*ry,l+rx-k*rx,t,l+rx,t);
                if(nodes[^1].X==nodes[0].X && nodes[^1].Y==nodes[0].Y)
                {
                    nodes[0]=nodes[0] with { InX=nodes[^1].InX,InY=nodes[^1].InY };
                    nodes.RemoveAt(nodes.Count-1);
                }
            }
        }
        return new VectorPath { Nodes=nodes.ToArray(), Closed=!line };
    }

    private static ShapeStyle WithEditedPath(ShapeStyle style, VectorPath path)
    {
        var converted=style with { Kind=ShapeKind.Path,Path=path };
        if(style.Kind==ShapeKind.Line) converted=converted with { FillEnabled=false, Stroke=style.Fill, StrokeGradient=style.FillGradient,
            FillGradient=null, StrokeWidth=style.LineWidth, StrokeEnabled=style.StrokeEnabled, RoundCaps=true };
        return converted;
    }

    public void PreviewVectorPath(Layer layer, VectorPath path, bool mask)
    {
        if (!IsInteracting || PixelsLocked(layer) || layer.Pixels == null) return;
        if (mask) { if (layer.VectorMask == null) layer.VectorMaskFollowsWarp = !layer.HasWarp; layer.VectorMask = path; }
        else if (layer.Shape is { } style) { layer.Shape = WithEditedPath(style, path); layer.Pixels = RenderWorkingShape(layer.Shape, layer.Pixels.Width, layer.Pixels.Height); }
        InvalidateAll(); LayersChanged?.Invoke();
    }
    public void CompletePathEdit(Layer layer)
    {
        if (layer.Shape?.Path is not { Nodes.Length: >= 2 } geometry || layer.Pixels == null || PixelsLocked(layer)) return;
        var old = layer.Pixels;
        using var outline = geometry.Build(old.Width, old.Height);
        var bounds = outline.TightBounds;
        if (outline.IsEmpty) return;
        var pad = layer.Shape.Stroke == null ? 0 : layer.Shape.StrokeWidth / 2;
        int left=Math.Min(0,(int)Math.Floor(bounds.Left-pad)), top=Math.Min(0,(int)Math.Floor(bounds.Top-pad));
        int right=Math.Max(old.Width,(int)Math.Ceiling(bounds.Right+pad)), bottom=Math.Max(old.Height,(int)Math.Ceiling(bounds.Bottom+pad));
        if (left==0 && top==0 && right==old.Width && bottom==old.Height) return;
        var w=right-left; var h=bottom-top;
        if (!DocumentLimits.FitsSurface(w, h)) throw new InvalidOperationException("The edited path exceeds the surface size limit.");
        VectorPath Remap(VectorPath path) => path with { Nodes = path.Nodes.Select(n => new BezierNode((n.X * old.Width - left) / w, (n.Y * old.Height - top) / h,
            (n.InX * old.Width - left) / w, (n.InY * old.Height - top) / h, (n.OutX * old.Width - left) / w, (n.OutY * old.Height - top) / h) { StartsContour = n.StartsContour }).ToArray() };
        var matrix = layer.Matrix;
        var maskPlacement = layer.Mask != null ? layer.MaskMatrix : (SKMatrix?)null;
        if (layer.VectorMask != null) layer.VectorMask = Remap(layer.VectorMask);
        layer.Shape = layer.Shape with { Path = Remap(geometry) };
        layer.Pixels = RenderWorkingShape(layer.Shape, w, h);
        layer.Transform = LayerTransform.FromMatrix(SKMatrix.CreateTranslation(left,top).PostConcat(matrix),w,h);
        if (maskPlacement is {} placement) layer.PlaceMask(placement);
        InvalidateAll(); LayersChanged?.Invoke();
    }
    public void PathToSelection(Layer layer)
    {
        var geometry = layer.VectorMask ?? EditableShapePath(layer);
        if (geometry == null || layer.Pixels == null) return;
        using var path = geometry.Build(layer.Pixels.Width, layer.Pixels.Height); path.Transform(layer.Matrix);
        using var mask = SelectionMask.FromPath(document.Width, document.Height, path); Select(mask, SelectionMode.Replace, "Path to Selection");
    }
    public void ApplyPathAsVectorMask(Layer source, Layer target)
    {
        var geometry = source.Shape?.Path;
        if (geometry == null || !geometry.Closed || source.Pixels == null || target.Pixels == null || PixelsLocked(target) || !target.Matrix.TryInvert(out var inverse)) return;
        var matrix = SKMatrix.Concat(inverse, source.Matrix);
        BezierNode Convert(BezierNode n)
        {
            var anchor = matrix.MapPoint((float)n.X * source.Pixels.Width, (float)n.Y * source.Pixels.Height);
            var incoming = matrix.MapPoint((float)n.InX * source.Pixels.Width, (float)n.InY * source.Pixels.Height);
            var outgoing = matrix.MapPoint((float)n.OutX * source.Pixels.Width, (float)n.OutY * source.Pixels.Height);
            return new(anchor.X / target.Pixels.Width, anchor.Y / target.Pixels.Height, incoming.X / target.Pixels.Width, incoming.Y / target.Pixels.Height, outgoing.X / target.Pixels.Width, outgoing.Y / target.Pixels.Height) { StartsContour = n.StartsContour };
        }
        Apply("Vector Mask", () => { target.VectorMask = geometry with { Nodes = geometry.Nodes.Select(Convert).ToArray() }; target.VectorMaskFollowsWarp = !target.HasWarp; }); InvalidateAll(); LayersChanged?.Invoke();
    }
    public void RemoveVectorMask(Layer layer)
    {
        if (layer.VectorMask == null || PixelsLocked(layer)) return;
        Apply("Delete Vector Mask", () => layer.VectorMask = null); InvalidateAll(); LayersChanged?.Invoke();
    }
    public void RefinedSelectionToMask(SKBitmap selection)
    {
        if (ActiveLayer is not { Pixels: { } pixels } layer || PixelsLocked(layer) || !layer.Matrix.TryInvert(out var inverse)) return;
        var mask = SelectionMask.Remap(selection, pixels.Width, pixels.Height, inverse) ?? Pixels.NewMask(pixels.Width, pixels.Height);
        var candidate = document.Clone(); candidate.Find(layer.Id)!.Mask = mask;
        if (candidate.RasterPixels() > DocumentLimits.DocumentPixelBudget) { mask.Dispose(); throw new InvalidOperationException("The refined mask exceeds the document raster memory budget."); }
        Apply("Refine Layer Mask", () =>
        { layer.Mask = mask; layer.MaskEnabled = true; layer.MaskLinked = true; layer.MaskFollowsWarp = !layer.HasWarp; layer.MaskTransform = null; layer.MaskDefault = 0; layer.MaskDensity = 1; layer.MaskFeather = 0; });
        InvalidateAll(); LayersChanged?.Invoke();
    }
}
