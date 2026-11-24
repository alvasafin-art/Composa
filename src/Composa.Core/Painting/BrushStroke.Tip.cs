using Composa.Rendering;
using SkiaSharp;

namespace Composa.Painting;

public sealed unsafe partial class BrushStroke
{
    /// <summary>A single dab at full pressure. Fits the brush to the sample if necessary.</summary>
    public static SKBitmap RenderTip(BrushSettings settings, int side)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(side, 5);
        using var blank = Pixels.NewColor(side, side);
        using var sample = new BrushStroke(blank, settings with { Size = Math.Clamp(settings.Size, 1, side - 4) },
            BrushMode.Paint, SKColors.White);
        var center = new SKPoint(side / 2f, side / 2f);
        var area = sample.StampBounds(center);
        // A preview uses the actual coverage and opacity without waking up the GPU.
        if (sample.AccumulateCoverage(area, center, sample.DabFlow())) sample.RecomputeBand(area);
        return sample.Working;
    }

    // Converts target pixels to brush coordinates, where a rotated oval is a circle.
    // An affine layer/mask transform is included so the angle is relative to the canvas.
    private readonly struct EllipseTip
    {
        private readonly float xx, xy, yx, yy;
        public readonly float ExtentX, ExtentY, MaxSlope;

        public EllipseTip(BrushSettings settings, SKMatrix toDocument, float scale)
        {
            var angle = (float)(settings.TipAngle * Math.PI / 180);
            var (sin, cos) = MathF.SinCos(angle);
            var minorScale = scale * (float)settings.TipRoundness;
            xx = (cos * toDocument.ScaleX - sin * toDocument.SkewY) / scale;
            xy = (cos * toDocument.SkewX - sin * toDocument.ScaleY) / scale;
            yx = (sin * toDocument.ScaleX + cos * toDocument.SkewY) / minorScale;
            yy = (sin * toDocument.SkewX + cos * toDocument.ScaleY) / minorScale;
            var determinant = MathF.Abs(xx * yy - xy * yx);
            ExtentX = MathF.Sqrt(yy * yy + xy * xy) / determinant;
            ExtentY = MathF.Sqrt(yx * yx + xx * xx) / determinant;
            MaxSlope = MathF.Sqrt(xx * xx + xy * xy + yx * yx + yy * yy);
        }

        public void Project(float dx, float dy, out float u, out float v)
        {
            u = xx * dx + xy * dy;
            v = yx * dx + yy * dy;
        }

        public float Distance(float dx, float dy)
        {
            Project(dx, dy, out var u, out var v);
            return MathF.Sqrt(u * u + v * v);
        }

        public float Slope(float u, float v, float distance)
        {
            if (distance < 1e-6f) return MaxSlope;
            float x = xx * u + yx * v, y = xy * u + yy * v;
            return MathF.Sqrt(x * x + y * y) / distance;
        }
    }

    private float OvalCoverage(EllipseTip tip, float dx, float dy, float radius)
    {
        tip.Project(dx, dy, out var u, out var v);
        var squared = u * u + v * v;
        var outer = radius + .75f * tip.MaxSlope;
        if (squared >= outer * outer) return 0;
        var inner = radius - .75f * tip.MaxSlope;
        if (inner > 0 && squared <= inner * inner)
            return hardness >= .995f ? 1 : SoftCoverage(squared, radius);

        var distance = MathF.Sqrt(squared);
        var slope = tip.Slope(u, v, distance);
        // Integrate the profile across a pixel in the local normal direction.
        // With center sampling alone, narrow soft dabs can miss every pixel.
        var start = distance - .5f * slope;
        var end = distance + .5f * slope;
        if (hardness >= .995f) return Math.Clamp((MathF.Min(end, radius) - MathF.Max(start, -radius)) / slope, 0, 1);
        return Math.Clamp((IntegratedSoftCoverage(end, radius) - IntegratedSoftCoverage(start, radius)) / slope, 0, 1);
    }

    private float IntegratedSoftCoverage(float distance, float radius)
    {
        var inner = radius * hardness;
        var length = MathF.Min(MathF.Abs(distance), radius);
        if (length <= inner) return MathF.CopySign(length, distance);
        var fade = radius - inner;
        var t = (length - inner) / fade;
        var area = inner + .5f * fade * (t + MathF.Sin(MathF.PI * t) / MathF.PI);
        return MathF.CopySign(area, distance);
    }
}
