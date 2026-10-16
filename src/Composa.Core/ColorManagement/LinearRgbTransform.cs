using SkiaSharp;

namespace Composa.ColorManagement;

/// <summary>Unclipped linear RGB basis change through ICC's D50 PCS. Lighting gains must be
/// transferred in this space, not multiplied into unrelated encoded RGB channel numbers.</summary>
internal sealed class LinearRgbTransform
{
    private readonly double[]? matrix;
    internal LinearRgbTransform(RgbProfile from, RgbProfile to)
    {
        if (RgbProfile.SameSpace(from.Space, to.Space)) return;
        if (!from.Space.ToColorSpaceXyz(out var source) || !to.Space.ToColorSpaceXyz(out var target))
            throw new InvalidOperationException("The RGB profile has no supported linear color basis for scene lighting.");
        var a = source.Values.Select(v => (double)v).ToArray(); var b = target.Values.Select(v => (double)v).ToArray();
        var inverse = Invert(b); matrix = new double[9];
        for (var row = 0; row < 3; row++) for (var col = 0; col < 3; col++)
            for (var k = 0; k < 3; k++) matrix[row * 3 + col] += inverse[row * 3 + k] * a[k * 3 + col];
    }
    private static double[] Invert(double[] m)
    {
        var det = m[0] * (m[4] * m[8] - m[5] * m[7]) - m[1] * (m[3] * m[8] - m[5] * m[6]) + m[2] * (m[3] * m[7] - m[4] * m[6]);
        if (!double.IsFinite(det) || Math.Abs(det) < 1e-12) throw new InvalidOperationException("The RGB profile has a singular color matrix.");
        return new[] { m[4]*m[8]-m[5]*m[7], m[2]*m[7]-m[1]*m[8], m[1]*m[5]-m[2]*m[4],
            m[5]*m[6]-m[3]*m[8], m[0]*m[8]-m[2]*m[6], m[2]*m[3]-m[0]*m[5],
            m[3]*m[7]-m[4]*m[6], m[1]*m[6]-m[0]*m[7], m[0]*m[4]-m[1]*m[3] }.Select(v => v / det).ToArray();
    }
    internal (double R, double G, double B) Map(double r, double g, double b) => matrix is { } m
        ? (m[0]*r+m[1]*g+m[2]*b, m[3]*r+m[4]*g+m[5]*b, m[6]*r+m[7]*g+m[8]*b) : (r, g, b);
}
