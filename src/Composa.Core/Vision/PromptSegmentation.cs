using Composa.Rendering;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using System.Security.Cryptography;

namespace Composa.Vision;

public sealed record ImageEmbedding(float[] Data, int Width, int Height)
{
    internal bool PreferGpu { get; init; }
    internal byte[]? InputHash { get; init; }
}

/// <summary>Encoder/prompt-decoder pipelines with aspect-correct SAM logit reconstruction.</summary>
public static unsafe class PromptSegmentation
{
    // A sparse pattern inside a recognisable subject may belong to its surface. Only
    // substitute those details when the network misses, or returns a nearly solid rectangle.
    internal static bool IsBoxSilhouette(SKBitmap mask, SKBitmap image, SKRectI box)
    {
        var bounds = Selections.SelectionMask.Bounds(mask, 128);
        if (bounds.IsEmpty) return true;
        var overlap = SKRectI.Intersect(bounds, box);
        if (overlap.Width < .78 * box.Width || overlap.Height < .78 * box.Height) return false;
        var data = (byte*)mask.GetPixels();
        // A sparse pattern on a well-framed product belongs to the product. Prefer
        // the details only when the coarse silhouette follows the box on several sides.
        int nearSides = 0;
        if (bounds.Left <= box.Left + .035 * box.Width) nearSides++;
        if (bounds.Right >= box.Right - .035 * box.Width) nearSides++;
        if (bounds.Top <= box.Top + .035 * box.Height) nearSides++;
        if (bounds.Bottom >= box.Bottom - .035 * box.Height) nearSides++;
        if (nearSides < 3) return false;
        int inside = 0;
        Span<int> corners = stackalloc int[4];
        corners.Clear();
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                int px = bounds.Left + (2 * x + 1) * bounds.Width / 64;
                int py = bounds.Top + (2 * y + 1) * bounds.Height / 64;
                if (data[(long)py * mask.RowBytes + px] < 128) continue;
                inside++;
                if ((x is >= 1 and <= 3 or >= 28 and <= 30) && (y is >= 1 and <= 3 or >= 28 and <= 30))
                    corners[(y > 16 ? 2 : 0) + (x > 16 ? 1 : 0)]++;
            }
        int squareCorners = 0;
        foreach (int count in corners) if (count >= 4) squareCorners++;
        if (inside < .75 * 32 * 32 || squareCorners < 2) return false;
        // A box-fitting contour can also be the true edge of a rectangular product.
        // Verify its outer boundary against the photo before selecting its markings.
        var rgb = new PixelBuffer(image);
        int radius = Math.Clamp(Math.Min(bounds.Width, bounds.Height) / 32, 3, 12);
        int edges = 0, visibleEdges = 0;
        void Probe(int x, int y, int dx, int dy)
        {
            while (bounds.Contains(x, y) && data[(long)y * mask.RowBytes + x] < 128) { x += dx; y += dy; }
            if (!bounds.Contains(x, y)) return;
            int ix = x + dx * radius, iy = y + dy * radius, ox = x - dx * radius, oy = y - dy * radius;
            if (!bounds.Contains(ix, iy) || ox < 0 || ox >= image.Width || oy < 0 || oy >= image.Height) return;
            var diff = rgb.Read(ix, iy) - rgb.Read(ox, oy);
            edges++;
            if (Math.Max(Math.Abs(diff.R), Math.Max(Math.Abs(diff.G), Math.Abs(diff.B))) > .12f) visibleEdges++;
        }
        for (int i = 0; i < 16; i++)
        {
            int x = bounds.Left + (2 * i + 1) * bounds.Width / 32;
            int y = bounds.Top + (2 * i + 1) * bounds.Height / 32;
            Probe(bounds.Left, y, 1, 0); Probe(bounds.Right - 1, y, -1, 0);
            Probe(x, bounds.Top, 0, 1); Probe(x, bounds.Bottom - 1, 0, -1);
        }
        return edges >= 16 && visibleEdges < .5 * edges;
    }
    // Preserve small-object detail that would otherwise disappear when the whole image is
    // resized for the encoder. Keep context beyond all four sides of a drawn box.
    internal static SKRectI SearchArea(SKRectI canvas, SKRectI? box)
    {
        if (box is not { } area || Math.Max(canvas.Width, canvas.Height) <= 1024) return canvas;
        // Keep global context when the object already spans 96 encoder pixels.
        // Aggressive zoom here can drop attached parts, such as dark feet in fur.
        var canvasSide = Math.Max(canvas.Width, canvas.Height);
        if (Math.Max(area.Width, area.Height) >= canvasSide * (96.0 / 1024)) return canvas;
        var margin = Math.Max(128, canvasSide / 6);
        area.Inflate(margin, margin);
        area = SKRectI.Intersect(area, canvas);
        return Math.Max(area.Width, area.Height) < .75 * Math.Max(canvas.Width, canvas.Height) ? area : canvas;
    }

    internal static bool TouchesCrop(SKRectI mask, SKRectI crop, SKRectI canvas) =>
        mask.IsEmpty || (crop.Left > canvas.Left && mask.Left <= 1) || (crop.Top > canvas.Top && mask.Top <= 1)
        || (crop.Right < canvas.Right && mask.Right >= crop.Width - 1) || (crop.Bottom < canvas.Bottom && mask.Bottom >= crop.Height - 1);

    // A thumbnail or product photo nested in a larger layout can itself be the
    // highest-scoring SAM object. When the gesture points inside its coloured
    // backdrop, fit the matte inside the panel and exclude its rectangular edge.
    internal static SKRectI? PromptedPanel(SKBitmap image, SKBitmap mask, SKRectI box)
    {
        var panel = Selections.SelectionMask.Bounds(mask, 128);
        if (panel.Width < 24 || panel.Height < 24 || panel.Width >= .9 * image.Width || panel.Height >= .9 * image.Height ||
            !panel.Contains(box) || (long)box.Width * box.Height > .88 * panel.Width * panel.Height) return null;
        var p = (byte*)mask.GetPixels(); var stride = mask.RowBytes;
        var count = 0;
        for (var y = 0; y < 20; y++) for (var x = 0; x < 20; x++)
            if (p[(long)(panel.Top + (2 * y + 1) * panel.Height / 40) * stride + panel.Left + (2 * x + 1) * panel.Width / 40] >= 128) count++;
        if (count < 386 || ForegroundCoverage.BackgroundAlong(new PixelBuffer(image), box) == null) return null;
        panel.Inflate(-1, -1);
        return panel;
    }

    internal static SKBitmap Place(SKBitmap mask, SKRectI area, SKRectI canvas)
    {
        if (area == canvas) return Pixels.Clone(mask);
        var result = Pixels.NewMask(canvas.Width, canvas.Height);
        using var draw = new SKCanvas(result);
        draw.DrawBitmap(mask, area.Left, area.Top);
        return result;
    }

    internal static SKBitmap RefineEdges(SKBitmap image, SKBitmap outline, SKRectI? box, CancellationToken cancellation)
    {
        var canvas = SKRectI.Create(image.Width, image.Height);
        var area = Selections.SelectionMask.Bounds(outline);
        if (area.IsEmpty) return Pixels.Clone(outline);
        var radius = Math.Clamp(Math.Max(area.Width, area.Height) / 18, 8, 32);
        // Known background outside this margin cannot contribute alpha. Give ViTMatte
        // additional RGB context on both sides of the unknown trimap band.
        area.Inflate(radius + 64, radius + 64);
        area = SKRectI.Intersect(area, canvas);
        using var crop = area == canvas ? null : Pixels.Subset(image, area);
        using var mask = area == canvas ? null : Pixels.Subset(outline, area);
        if (box is { } rect) { rect.Offset(-area.Left, -area.Top); box = rect; }
        var guide = crop ?? image;
        var silhouette = mask ?? outline;
        using var alpha = TrimapMatting.Refine(guide, silhouette, radius, cancellation: cancellation, fineDetail: true);
        ForegroundCoverage.Refine(guide, alpha, box, radius, cancellation);
        if (LostInterior(silhouette, alpha, radius, cancellation) &&
            !GraphicSelection.HasDominantInk(guide, alpha, silhouette, cancellation))
        {
            // The usual band can erase all foreground seeds on a thin slice or
            // a row of small subjects. Give matting a narrower band on retry.
            int narrow = Math.Max(2, radius / 4);
            using var anchored = TrimapMatting.Refine(guide, silhouette, narrow, cancellation: cancellation, fineDetail: true);
            ForegroundCoverage.Refine(guide, anchored, box, narrow, cancellation);
            if (!MissingSilhouette(silhouette, anchored, .80, cancellation))
                return Place(anchored, area, canvas);
        }
        return Place(alpha, area, canvas);
    }

    internal static bool LostInterior(SKBitmap outline, SKBitmap alpha, int radius, CancellationToken cancellation)
    {
        if (!MissingSilhouette(outline, alpha, .75, cancellation)) return false;
        var bounds = Selections.SelectionMask.Bounds(outline);
        int filled = 0;
        foreach (byte a in outline.GetPixelSpan()) if (a >= 128) filled++;
        // A sparse open contour can be the actual subject (for example a rim).
        // Only restore the interior of a solid silhouette.
        if (filled < .36 * bounds.Width * bounds.Height) return false;
        using var core = Selections.SelectionMask.Contract(outline, radius);
        if (core == null) return true;
        return MissingSilhouette(outline, core, .12, cancellation);
    }

    private static bool MissingSilhouette(SKBitmap outline, SKBitmap alpha, double required, CancellationToken cancellation)
    {
        var original = outline.GetPixelSpan();
        var refined = alpha.GetPixelSpan();
        int total = 0, retained = 0;
        for (int y = 0; y < outline.Height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < outline.Width; x++)
            {
                if (original[y * outline.RowBytes + x] < 128) continue;
                total++;
                if (refined[y * alpha.RowBytes + x] >= 128) retained++;
            }
        }
        return total >= 24 && retained < required * total;
    }

    public static ImageEmbedding Encode(SKBitmap image, PromptModel model, CancellationToken cancellation = default) =>
        Encode(image, model, null, cancellation);

    internal static ImageEmbedding Encode(SKBitmap image, PromptModel model, ImageEmbedding? previous, CancellationToken cancellation)
    {
        var scale = 1024.0 / Math.Max(image.Width, image.Height);
        var w = Math.Max(1, (int)Math.Round(image.Width * scale)); var h = Math.Max(1, (int)Math.Round(image.Height * scale));
        using var resized = Pixels.NewColor(w, h);
        resized.Erase(SKColors.White);
        using (var canvas = new SKCanvas(resized)) using (var input = SKImage.FromBitmap(image))
            canvas.DrawImage(input, new SKRect(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        cancellation.ThrowIfCancellationRequested();
        // Selection history can change while a sampled composition stays identical.
        // Compare exactly what the encoder sees, including document color conversion.
        var hash = SHA256.HashData(resized.GetPixelSpan());
        var preferGpu = ModelRunner.PreferGpu;
        if (previous?.InputHash is { } priorHash && previous.PreferGpu == preferGpu && previous.Width == w && previous.Height == h && hash.AsSpan().SequenceEqual(priorHash))
            return previous;
        var data = new float[w * h * 3]; var pixels = (byte*)resized.GetPixels();
        var mobile = model.Kind == PromptModelKind.MobileSam;
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
            for (var c = 0; c < 3; c++) data[mobile ? (y * w + x) * 3 + c : c * w * h + y * w + x] = pixels[(long)y * resized.RowBytes + x * 4 + c] / (mobile ? 1f : 255f);
        var tensor = new DenseTensor<float>(data, mobile ? [h, w, 3] : [1, 3, h, w]);
        var inputName = mobile ? "input_image" : "batched_images";
        var result = Run(model.Encoder, [NamedOnnxValue.CreateFromTensor(inputName, tensor)], ["image_embeddings"], cancellation)[0];
        if (!result.Dimensions.SequenceEqual(new[] { 1, 256, 64, 64 })) throw new InvalidDataException("Unexpected SAM image embedding dimensions.");
        return new(result.Data, w, h) { InputHash = hash, PreferGpu = preferGpu };
    }

    public static SKBitmap Decode(ImageEmbedding image, PromptModel model, int width, int height, SKPoint? point, SKRectI? box, CancellationToken cancellation = default)
    {
        var mobile = model.Kind == PromptModelKind.MobileSam;
        var coords = box is { } b ? new[] { (float)b.Left / width * image.Width, (float)b.Top / height * image.Height,
            (float)b.Right / width * image.Width, (float)b.Bottom / height * image.Height }
            : new[] { point!.Value.X / width * image.Width, point.Value.Y / height * image.Height, 0f, 0f };
        var labels = box != null ? new[] { 2f, 3f } : new[] { 1f, -1f };
        var embeddingTensor = new DenseTensor<float>(image.Data, [1, 256, 64, 64]);
        Output[] Predict(float[] promptCoords, float[] promptLabels)
        {
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("image_embeddings", embeddingTensor),
                NamedOnnxValue.CreateFromTensor(mobile ? "point_coords" : "batched_point_coords", new DenseTensor<float>(promptCoords, mobile ? [1, 2, 2] : [1, 1, promptLabels.Length, 2])),
                NamedOnnxValue.CreateFromTensor(mobile ? "point_labels" : "batched_point_labels", new DenseTensor<float>(promptLabels, mobile ? [1, 2] : [1, 1, promptLabels.Length]))
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
            return Run(model.Decoder, inputs, [mobile ? "low_res_masks" : "output_masks", "iou_predictions"], cancellation);
        }
        var outputs = Predict(coords, labels);
        var masks = outputs[0]; var scores = outputs[1].Data;
        var mw = masks.Dimensions[^1]; var mh = masks.Dimensions[^2];
        if (mw <= 0 || mh <= 0 || scores.Length == 0 || scores.Length > masks.Data.Length / ((long)mw * mh))
            throw new InvalidDataException("Unexpected SAM mask dimensions.");
        float sx = (float)mw / width, sy = (float)mh / height;
        if (mobile) { sx *= image.Width / 1024f; sy *= image.Height / 1024f; }
        var probe = box ?? SKRectI.Create((int)point!.Value.X, (int)point.Value.Y, 1, 1);
        var probeGrid = new SKRectI((int)Math.Floor(probe.Left * sx), (int)Math.Floor(probe.Top * sy),
            (int)Math.Ceiling(probe.Right * sx), (int)Math.Ceiling(probe.Bottom * sy));
        var candidate = PromptCandidate(masks.Data, scores, mw, mh, probeGrid, cancellation);
        if (candidate < 0) return Pixels.NewMask(width, height);
        if (box == null && !mobile)
        {
            float[]? ConfirmPoint(SKPointI grid)
            {
                var prediction = Predict([(grid.X + .5f) * image.Width / mw, (grid.Y + .5f) * image.Height / mh, 0, 0], [1, -1]);
                int i = PromptCandidate(prediction[0].Data, prediction[1].Data, mw, mh, SKRectI.Create(grid.X, grid.Y, 1, 1), cancellation);
                return i < 0 ? null : prediction[0].Data.AsSpan(i * mw * mh, mw * mh).ToArray();
            }
            (float[], float)? ConfirmBox(SKRectI r)
            {
                float xScale = (float)image.Width / mw, yScale = (float)image.Height / mh;
                var prediction = Predict([r.Left * xScale, r.Top * yScale, r.Right * xScale, r.Bottom * yScale], [2, 3]);
                int i = PromptCandidate(prediction[0].Data, prediction[1].Data, mw, mh, probeGrid, cancellation);
                return i < 0 ? null : (prediction[0].Data.AsSpan(i * mw * mh, mw * mh).ToArray(), prediction[1].Data[i]);
            }
            var click = new SKPointI(probeGrid.Left, probeGrid.Top);
            int whole = PointMaskCandidates.Resolve(masks.Data, scores, candidate, mw, mh, click, ConfirmPoint, cancellation);
            candidate = whole != candidate ? whole : PointMaskCandidates.CheckExtent(masks.Data, scores, candidate, mw, mh, click, ConfirmBox, cancellation);
        }
        // A real object at the centre is independent evidence for the contents of a
        // hollow prediction. Background seen through a handle usually extends well
        // beyond the box. Both checks reuse the original image embedding.
        float cx = (coords[0] + coords[2]) * .5f, cy = (coords[1] + coords[3]) * .5f;
        var centre = SKRectI.Create(probeGrid.MidX, probeGrid.MidY, 1, 1);
        float[]? InteriorCandidate()
        {
            var hit = Predict([cx, cy, 0, 0], [1, -1]);
            int part = PromptCandidate(hit[0].Data, hit[1].Data, mw, mh, centre, cancellation);
            if (part < 0 || !BoxMaskCandidates.IsContainedPart(hit[0].Data, part, mw, mh, probeGrid, cancellation)) return null;
            return GuidedCandidate();
        }
        float[]? GuidedCandidate()
        {
            var guided = Predict([.. coords, cx, cy], [.. labels, 1]);
            int whole = PromptCandidate(guided[0].Data, guided[1].Data, mw, mh, centre, cancellation);
            if (whole < 0 || guided[1].Data[whole] < scores[candidate]) return null;
            return guided[0].Data.AsSpan(whole * mw * mh, mw * mh).ToArray();
        }
        var plane = box != null
            ? BoxMaskCandidates.Resolve(masks.Data, scores, candidate, mw, mh, probeGrid, cancellation,
                mobile ? null : InteriorCandidate, mobile ? null : GuidedCandidate)
            : masks.Data.AsSpan(candidate * mw * mh, mw * mh).ToArray();
        return Reconstruct(plane, mw, mh,
            image.Width, image.Height, width, height, mobile, cancellation);
    }

    internal static int PromptCandidate(float[] logits, float[] scores, int width, int height, SKRectI probe, CancellationToken cancellation)
    {
        var ranked = Enumerable.Range(0, scores.Length).OrderByDescending(i => scores[i]).ToArray();
        var area = SKRectI.Intersect(SKRectI.Create(width, height), probe);
        int required = Math.Max(1, area.Width * area.Height / 10000);
        foreach (int candidate in ranked)
        {
            int count = 0, offset = candidate * width * height;
            for (int y = area.Top; y < area.Bottom; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int x = area.Left; x < area.Right; x++)
                    if (logits[offset + y * width + x] > 0 && ++count >= required) return candidate;
            }
        }
        return -1;
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
            // SAM predicts membership, not a photographic opacity matte. The guided pass snaps
            // the contour to image edges; threshold it again instead of committing its blur as coverage.
            // Otherwise deleting the selection leaves a translucent 1px strip and several soft pixels.
            *p = *p >= 128 ? (byte)255 : (byte)0;
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
        return ModelRunner.Execute(model, session => Run(session, inputs, names, cancellation), cancellation);
    }

    private static Output[] Run(InferenceSession session, IReadOnlyCollection<NamedOnnxValue> inputs, string[] names, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var options = new RunOptions(); using var stop = cancellation.Register(() => options.Terminate = true);
        try
        {
            using var outputs = session.Run(inputs, names, options);
            return outputs.Select(o => { var t = o.AsTensor<float>(); return new Output(t.ToArray(), t.Dimensions.ToArray()); }).ToArray();
        }
        catch (OnnxRuntimeException) when (cancellation.IsCancellationRequested) { throw new OperationCanceledException(cancellation); }
    }
}
