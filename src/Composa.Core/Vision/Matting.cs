// Ported from Lolly (github.com/lolly-tools/lolly, packages/node-shell/src/ml/matte-math.ts and tensor.ts at 12b26ff), MPL-2.0, used under the MIT licence by permission of Andy Fitzsimon, 2026-09-30.
namespace Composa.Vision;

/// <summary>Where a picture sits inside a model's square input: scaled to fit, centered, the rest black.</summary>
public readonly record struct LetterboxPlan(int Edge, double Scale, int OffsetX, int OffsetY, int ContentWidth, int ContentHeight);

/// <summary>
/// The arithmetic every segmentation model shares: the letterbox geometry, the per-model normalization into NCHW
/// planes, the activation that turns the head's output into a 0 to 1 matte, and the crop back out of the square. Kept
/// apart from the runner because the offsets are the part that goes wrong silently.
/// </summary>
public static class Matting
{
    /// <summary>Fits a width by height picture into a square of <paramref name="edge"/> pixels, keeping its proportions, centered.</summary>
    public static LetterboxPlan PlanLetterbox(int width, int height, int edge)
    {
        var scale = Math.Min((double)edge / width, (double)edge / height);
        var contentW = Math.Max(1, (int)Math.Round(width * scale));
        var contentH = Math.Max(1, (int)Math.Round(height * scale));
        return new LetterboxPlan(edge, scale, (edge - contentW) / 2, (edge - contentH) / 2, contentW, contentH);
    }

    /// <summary>
    /// RGBA bytes at edge by edge to a [1,3,edge,edge] float tensor: each channel (value / 255 - mean) / std, all of
    /// red first, then green, then blue; alpha is dropped. <paramref name="rowBytes"/> is the pixel row stride.
    /// </summary>
    public static float[] PackNchwNormalized(ReadOnlySpan<byte> rgba, int edge, int rowBytes, SubjectModel model) =>
        PackNchw(rgba, edge, edge, rowBytes, model.Mean, model.Std);

    /// <summary>
    /// RGBA bytes at width by height to a [1,3,height,width] float tensor, each channel (value / 255 - mean) / std.
    /// The upscalers take the raw 0 to 1 scale: a mean of 0 and a std of 1.
    /// </summary>
    public static float[] PackNchw(ReadOnlySpan<byte> rgba, int width, int height, int rowBytes, float[] mean, float[] std)
    {
        var plane = width * height;
        var output = new float[plane * 3];
        float mr = mean[0], mg = mean[1], mb = mean[2];
        float sr = std[0], sg = std[1], sb = std[2];
        for (var y = 0; y < height; y++)
        {
            var row = rgba.Slice(y * rowBytes, width * 4);
            var at = y * width;
            for (var x = 0; x < width; x++, at++)
            {
                var p = x * 4;
                output[at] = (row[p] / 255f - mr) / sr;
                output[plane + at] = (row[p + 1] / 255f - mg) / sg;
                output[2 * plane + at] = (row[p + 2] / 255f - mb) / sb;
            }
        }
        return output;
    }

    /// <summary>The single-channel output as a 0 to 1 matte, by the model's activation. A flat output is all backdrop.</summary>
    public static float[] ActivateMask(ReadOnlySpan<float> raw, MaskActivation activation)
    {
        var output = new float[raw.Length];
        if (activation == MaskActivation.Sigmoid)
        {
            for (var i = 0; i < raw.Length; i++) output[i] = 1f / (1f + MathF.Exp(-raw[i]));
            return output;
        }
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        foreach (var v in raw) { if (v < min) min = v; if (v > max) max = v; }
        var span = max - min;
        if (!(span > 1e-6f)) return output;
        for (var i = 0; i < raw.Length; i++) output[i] = (raw[i] - min) / span;
        return output;
    }

    /// <summary>The matte of the picture alone, cut out of the square by the plan, clamped to 0 to 1.</summary>
    public static float[] UnpadMask(ReadOnlySpan<float> square, LetterboxPlan plan)
    {
        var output = new float[plan.ContentWidth * plan.ContentHeight];
        for (var y = 0; y < plan.ContentHeight; y++)
        {
            var from = (plan.OffsetY + y) * plan.Edge + plan.OffsetX;
            var to = y * plan.ContentWidth;
            for (var x = 0; x < plan.ContentWidth; x++) output[to + x] = Math.Clamp(square[from + x], 0f, 1f);
        }
        return output;
    }
}
