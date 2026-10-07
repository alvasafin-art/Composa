using Composa.Rendering;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace Composa.Vision;

public sealed record ImageEmbedding(float[] Data, int Width, int Height);

/// <summary>Encoder/prompt-decoder pipelines with aspect-correct SAM logit reconstruction.</summary>
public static unsafe class PromptSegmentation
{
    public static ImageEmbedding Encode(SKBitmap image, PromptModel model, CancellationToken cancellation = default)
    {
        var scale = 1024.0 / Math.Max(image.Width, image.Height);
        var w = Math.Max(1, (int)Math.Round(image.Width * scale)); var h = Math.Max(1, (int)Math.Round(image.Height * scale));
        using var resized = Pixels.NewColor(w, h);
        resized.Erase(SKColors.White);
        using (var canvas = new SKCanvas(resized)) using (var input = SKImage.FromBitmap(image))
            canvas.DrawImage(input, new SKRect(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        var data = new float[w * h * 3]; var pixels = (byte*)resized.GetPixels();
        var mobile = model.Kind == PromptModelKind.MobileSam;
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
            for (var c = 0; c < 3; c++) data[mobile ? (y * w + x) * 3 + c : c * w * h + y * w + x] = pixels[(long)y * resized.RowBytes + x * 4 + c] / (mobile ? 1f : 255f);
        var tensor = new DenseTensor<float>(data, mobile ? [h, w, 3] : [1, 3, h, w]);
        var inputName = mobile ? "input_image" : "batched_images";
        var result = Run(model.Encoder, [NamedOnnxValue.CreateFromTensor(inputName, tensor)], ["image_embeddings"], cancellation)[0];
        if (!result.Dimensions.SequenceEqual(new[] { 1, 256, 64, 64 })) throw new InvalidDataException("Unexpected SAM image embedding dimensions.");
        return new(result.Data, w, h);
    }

    public static SKBitmap Decode(ImageEmbedding image, PromptModel model, int width, int height, SKPoint? point, SKRectI? box, CancellationToken cancellation = default)
    {
        var mobile = model.Kind == PromptModelKind.MobileSam;
        var coords = box is { } b ? new[] { (float)b.Left / width * image.Width, (float)b.Top / height * image.Height,
            (float)b.Right / width * image.Width, (float)b.Bottom / height * image.Height }
            : new[] { point!.Value.X / width * image.Width, point.Value.Y / height * image.Height, 0f, 0f };
        var labels = box != null ? new[] { 2f, 3f } : new[] { 1f, -1f };
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("image_embeddings", new DenseTensor<float>(image.Data, [1, 256, 64, 64])),
            NamedOnnxValue.CreateFromTensor(mobile ? "point_coords" : "batched_point_coords", new DenseTensor<float>(coords, mobile ? [1, 2, 2] : [1, 1, 2, 2])),
            NamedOnnxValue.CreateFromTensor(mobile ? "point_labels" : "batched_point_labels", new DenseTensor<float>(labels, mobile ? [1, 2] : [1, 1, 2]))
        };
        if (mobile)
        {
            // The exported decoder expects coordinates on the padded 1024 square, with the original aspect ratio.
            inputs.Add(NamedOnnxValue.CreateFromTensor("orig_im_size", new DenseTensor<float>(new[] { (float)image.Height, (float)image.Width }, [2])));
            inputs.Add(NamedOnnxValue.CreateFromTensor("mask_input", new DenseTensor<float>(new float[256 * 256], [1, 1, 256, 256])));
            inputs.Add(NamedOnnxValue.CreateFromTensor("has_mask_input", new DenseTensor<float>(new[] { 0f }, [1])));
        }
        else inputs.Add(NamedOnnxValue.CreateFromTensor("orig_im_size", new DenseTensor<long>(new[] { (long)image.Height, (long)image.Width }, [2])));
        // The old MobileSAM export's high-resolution "masks" freezes the dummy crop ratio.
        // Reconstruct its low-resolution logits ourselves: square resize, remove padding,
        // then resize to the photograph. EfficientSAM outputs aspect-correct image logits.
        var outputs = Run(model.Decoder, inputs, [mobile ? "low_res_masks" : "output_masks", "iou_predictions"], cancellation);
        var masks = outputs[0]; var scores = outputs[1].Data;
        var mw = masks.Dimensions[^1]; var mh = masks.Dimensions[^2];
        var candidate = 0; for (var i = 1; i < scores.Length; i++) if (scores[i] > scores[candidate]) candidate = i;
        if (mw <= 0 || mh <= 0 || candidate >= masks.Data.Length / ((long)mw * mh)) throw new InvalidDataException("Unexpected SAM mask dimensions.");
        return Reconstruct(masks.Data.AsSpan(candidate * mw * mh, mw * mh).ToArray(), mw, mh,
            image.Width, image.Height, width, height, mobile, cancellation);
    }

    internal static SKBitmap Reconstruct(float[] logits, int maskWidth, int maskHeight, int contentWidth, int contentHeight,
        int width, int height, bool padded, CancellationToken cancellation = default)
    {
        // Resize logits BEFORE thresholding. Sigmoid is confidence, not opacity: tiny
        // background probabilities must not become a faint selection over the whole canvas.
        var plane = padded ? Resize(logits, maskWidth, maskHeight, 1024, 1024) : logits;
        var pw = padded ? 1024 : maskWidth; var ph = padded ? 1024 : maskHeight;
        if (padded)
        {
            var cropped = new float[contentWidth * contentHeight];
            for (var y = 0; y < contentHeight; y++) Array.Copy(plane, y * pw, cropped, y * contentWidth, contentWidth);
            plane = cropped; pw = contentWidth; ph = contentHeight;
        }
        var result = Pixels.NewMask(width, height);
        var dst = (byte*)result.GetPixels();
        for (var y = 0; y < height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++) dst[(long)y * result.RowBytes + x] = Sample(plane, pw, ph, x, y, width, height) > 0 ? (byte)255 : (byte)0;
        }
        return result;
    }

    internal static SKBitmap Refine(SKBitmap mask, SKBitmap guide, CancellationToken cancellation)
    {
        var scale = Math.Min(1.0, (double)GuidedFilter.WorkSize / Math.Max(mask.Width, mask.Height));
        int w = Math.Max(1, (int)Math.Round(mask.Width * scale)), h = Math.Max(1, (int)Math.Round(mask.Height * scale));
        using var small = Pixels.NewMask(w, h);
        using (var canvas = new SKCanvas(small)) using (var image = SKImage.FromBitmap(mask))
            canvas.DrawImage(image, new SKRect(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        var data = new float[w * h]; var pixels = (byte*)small.GetPixels();
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) data[y * w + x] = pixels[(long)y * small.RowBytes + x] / 255f;
        var refined = GuidedFilter.Upsample(data, w, h, guide, cancellation);
        var dst = (byte*)refined.GetPixels();
        for (var y = 0; y < refined.Height; y++) for (var x = 0; x < refined.Width; x++)
        {
            var p = dst + (long)y * refined.RowBytes + x;
            // Leave only a genuine boundary fringe, not confidence haze or ghost islands.
            if (*p < 16) *p = 0; else if (*p > 239) *p = 255;
        }
        Pixels.Invalidate(refined);
        return refined;
    }

    private static float[] Resize(float[] source, int sw, int sh, int w, int h)
    {
        if (sw == w && sh == h) return source;
        var result = new float[w * h];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) result[y * w + x] = Sample(source, sw, sh, x, y, w, h);
        return result;
    }

    private static float Sample(float[] source, int sw, int sh, int x, int y, int w, int h)
    {
        var fy = Math.Clamp((y + .5f) * sh / h - .5f, 0, sh - 1); var y0 = (int)fy; var y1 = Math.Min(sh - 1, y0 + 1); var ty = fy - y0;
        var fx = Math.Clamp((x + .5f) * sw / w - .5f, 0, sw - 1); var x0 = (int)fx; var x1 = Math.Min(sw - 1, x0 + 1); var tx = fx - x0;
        return (source[y0 * sw + x0] * (1 - tx) + source[y0 * sw + x1] * tx) * (1 - ty)
            + (source[y1 * sw + x0] * (1 - tx) + source[y1 * sw + x1] * tx) * ty;
    }

    private sealed record Output(float[] Data, int[] Dimensions);
    private static Output[] Run(OnnxModel model, IReadOnlyCollection<NamedOnnxValue> inputs, string[] names, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested(); var session = ModelRunner.Session(model);
        using var options = new RunOptions(); using var stop = cancellation.Register(() => options.Terminate = true);
        try
        {
            using var outputs = session.Run(inputs, names, options);
            return outputs.Select(o => { var t = o.AsTensor<float>(); return new Output(t.ToArray(), t.Dimensions.ToArray()); }).ToArray();
        }
        catch (OnnxRuntimeException) when (cancellation.IsCancellationRequested) { throw new OperationCanceledException(cancellation); }
    }
}
