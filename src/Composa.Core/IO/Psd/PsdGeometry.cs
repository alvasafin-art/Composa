using Composa.Model;
using SkiaSharp;

namespace Composa.IO.Psd;

internal static class PsdGeometry
{
    /// <summary>An affine placement in the editor's scale, rotation, reflection and optional corner model.</summary>
    public static LayerTransform Place(SKMatrix matrix, int width, int height)
    {
        var sx = Math.Sqrt(matrix.ScaleX * matrix.ScaleX + matrix.SkewY * matrix.SkewY);
        var sy = Math.Sqrt(matrix.SkewX * matrix.SkewX + matrix.ScaleY * matrix.ScaleY);
        var center = matrix.MapPoint(width / 2f, height / 2f);
        var angle = Math.Atan2(matrix.SkewY, matrix.ScaleX) * 180 / Math.PI;
        var t = new LayerTransform { X = center.X - width * sx / 2, Y = center.Y - height * sy / 2, Width = width * sx, Height = height * sy, Rotation = angle,
            FlipVertical = matrix.ScaleX * matrix.ScaleY - matrix.SkewX * matrix.SkewY < 0 };
        if (Math.Abs(matrix.ScaleX * matrix.SkewX + matrix.SkewY * matrix.ScaleY) < 1e-5 * sx * sy) return t;
        // Unrotate the actual quad about its centre; these offsets also express a shear without losing the text.
        var inverseRotation = SKMatrix.CreateRotationDegrees((float)-angle, center.X, center.Y);
        var original = new[] { new SKPoint(0, 0), new SKPoint(width, 0), new SKPoint(width, height), new SKPoint(0, height) };
        var baseCorners = new[] { new SKPoint(0, 0), new SKPoint((float)t.Width, 0), new SKPoint((float)t.Width, (float)t.Height), new SKPoint(0, (float)t.Height) };
        var offsets = new float[8];
        for (var i = 0; i < 4; i++)
        {
            var point = inverseRotation.MapPoint(matrix.MapPoint(original[i]));
            offsets[i * 2] = point.X - (float)t.X - baseCorners[i].X;
            offsets[i * 2 + 1] = point.Y - (float)t.Y - baseCorners[i].Y;
        }
        return t with { FlipVertical = false, Distort = offsets };
    }
}
