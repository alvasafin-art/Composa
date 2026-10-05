// Ported from Lolly (github.com/lolly-tools/lolly, packages/node-shell/src/ml/matte.ts at 12b26ff), MPL-2.0, used under the MIT licence by permission of Andy Fitzsimon, 2026-09-30.
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Vision;

/// <summary>
/// A subject matte from a model: the picture letterboxed into the model's square on black, normalized, run, the
/// answer activated and cut back out, then brought up to the picture's size along its edges. Lolly scales the matte
/// back plainly; Composa uses the guided filter instead, because its masks are painted on at full size.
/// </summary>
public static class SubjectMatting
{
    /// <summary>
    /// The subject of <paramref name="source"/> (premultiplied color) as an <c>Alpha8</c> matte of the same size,
    /// 255 where the model is sure of the subject. Transparent pixels are always backdrop. Safe to call off the UI
    /// thread on a committed bitmap, which is immutable.
    /// </summary>
    public static SKBitmap Matte(SKBitmap source, SubjectModel model, CancellationToken cancellation = default)
    {
        var edge = model.InputSize;
        var plan = Matting.PlanLetterbox(source.Width, source.Height, edge);
        float[] input;
        using (var square = Pixels.NewColor(edge, edge))
        {
            using (var canvas = new SKCanvas(square))
            {
                canvas.Clear(SKColors.Black);
                using var image = SKImage.FromPixels(source.PeekPixels());
                canvas.DrawImage(image, new SKRect(plan.OffsetX, plan.OffsetY, plan.OffsetX + plan.ContentWidth, plan.OffsetY + plan.ContentHeight), new SKSamplingOptions(SKCubicResampler.Mitchell));
            }
            input = Matting.PackNchwNormalized(square.GetPixelSpan(), edge, square.RowBytes, model);
        }
        cancellation.ThrowIfCancellationRequested();
        var raw = ModelRunner.Run(model, input, edge, cancellation);
        var activated = Matting.ActivateMask(raw, model.Activation);
        var coarse = Matting.UnpadMask(activated, plan);
        cancellation.ThrowIfCancellationRequested();
        return GuidedFilter.Upsample(coarse, plan.ContentWidth, plan.ContentHeight, source, cancellation);
    }
}
