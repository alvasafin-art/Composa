using Composa.Rendering;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace Composa.Vision;

public sealed record ImageEmbedding(float[] Data, int Width, int Height);

/// <summary>Full encoder/prompt-decoder pipelines for the pinned MobileSAM and EfficientSAM Ti exports.</summary>
public static unsafe class PromptSegmentation
{
    public static ImageEmbedding Encode(SKBitmap image, PromptModel model, CancellationToken cancellation = default)
    {
        var scale = 1024.0 / Math.Max(image.Width, image.Height);
        var w = Math.Max(1, (int)Math.Round(image.Width * scale)); var h = Math.Max(1, (int)Math.Round(image.Height * scale));
        using var resized = Pixels.NewColor(w, h);
        using (var canvas = new SKCanvas(resized)) canvas.DrawBitmap(image, new SKRect(0, 0, w, h));
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
        var outputs = Run(model.Decoder, inputs, [mobile ? "masks" : "output_masks", "iou_predictions"], cancellation);
        var masks = outputs[0]; var scores = outputs[1].Data;
        var mw = masks.Dimensions[^1]; var mh = masks.Dimensions[^2];
        var candidate = 0; for (var i = 1; i < scores.Length; i++) if (scores[i] > scores[candidate]) candidate = i;
        if (mw <= 0 || mh <= 0 || candidate >= masks.Data.Length / ((long)mw * mh)) throw new InvalidDataException("Unexpected SAM mask dimensions.");
        using var coarse = Pixels.NewMask(mw, mh); var dst = (byte*)coarse.GetPixels();
        for (var y = 0; y < mh; y++) for (var x = 0; x < mw; x++)
        {
            var logit = masks.Data[candidate * mw * mh + y * mw + x];
            dst[(long)y * coarse.RowBytes + x] = (byte)Math.Clamp(255 / (1 + Math.Exp(-Math.Clamp(logit, -20, 20))), 0, 255);
        }
        var result = Pixels.NewMask(width, height);
        using (var canvas = new SKCanvas(result)) canvas.DrawBitmap(coarse, new SKRect(0, 0, width, height));
        return result;
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
