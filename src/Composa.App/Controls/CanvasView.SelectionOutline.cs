using SkiaSharp;

namespace Composa.App.Controls;

public sealed partial class CanvasView
{
    private SelectionOutlineBatches? OutlineBatches(SKPath? outline)
    {
        if (outline == null) return null;
        if (!ReferenceEquals(antsBatches?.Source, outline))
            antsBatches = (outline, outline.PointCount > FullDetailPoints ? SelectionOutlineBatches.Create(outline) : null);
        return antsBatches.Value.Batches;
    }

    /// <summary>Groups complete contours from a selection mask for cheaper stroking and viewport rejection.</summary>
    internal sealed class SelectionOutlineBatches(List<(SKPath Path, SKRect Bounds)> paths)
    {
        private const int ContoursPerBatch = 64;
        private const int MaxCachedPoints = 1_000_000;

        internal static SelectionOutlineBatches? Create(SKPath outline)
        {
            if (outline.PointCount > MaxCachedPoints) return null;
            var paths = new List<(SKPath Path, SKRect Bounds)>();
            var path = new SKPath();
            var contours = 0;
            using var iterator = outline.CreateRawIterator();
            Span<SKPoint> points = stackalloc SKPoint[4];
            for (var verb = iterator.Next(points); verb != SKPathVerb.Done; verb = iterator.Next(points))
            {
                switch (verb)
                {
                    case SKPathVerb.Move:
                        if (contours == ContoursPerBatch)
                        {
                            paths.Add((path, path.Bounds));
                            path = new SKPath();
                            contours = 0;
                        }
                        contours++;
                        path.MoveTo(points[0]);
                        break;
                    case SKPathVerb.Line:
                        path.LineTo(points[1]);
                        break;
                    case SKPathVerb.Close:
                        path.Close();
                        break;
                    default:
                        foreach (var batch in paths) batch.Path.Dispose();
                        path.Dispose();
                        return null;
                }
            }
            if (paths.Count == 0) { path.Dispose(); return null; }
            paths.Add((path, path.Bounds));
            // A recorded draw operation can still hold this generation after the selection changes.
            return new SelectionOutlineBatches(paths);
        }

        internal void Draw(SKCanvas canvas, SKMatrix view, SKPaint white, SKPaint black)
        {
            var clip = canvas.LocalClipBounds;
            clip.Inflate(white.StrokeWidth, white.StrokeWidth);
            using var screen = new SKPath();
            DrawPass(white);
            DrawPass(black);

            void DrawPass(SKPaint paint)
            {
                // Keep every contour's dash start and place all black strokes over the full white pass.
                foreach (var (path, bounds) in paths)
                {
                    if (!clip.IntersectsWith(view.MapRect(bounds))) continue;
                    path.Transform(in view, screen);
                    canvas.DrawPath(screen, paint);
                }
            }
        }
    }
}
