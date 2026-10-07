using Composa.Model;
using Composa.Selections;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Painting;

/// <summary>
/// Content-aware fill, used by the Spot Healing Brush and Edit &gt; Content-Aware Fill. The hole is filled with the
/// nearby matching texture. Exact whole-patch matches take a fast path; complex holes use coherent small
/// exemplars grown from their boundary. Both paths are deterministic and sample only the original layer.
/// </summary>
public static unsafe class Inpaint
{
    /// <summary>The largest hole (bounding box, in pixels) worth attempting; beyond this the working buffers reach gigabytes.</summary>
    public const long MaxArea = DocumentLimits.MaxRetouchPixels;

    /// <summary>Returns a copy of <paramref name="source"/> (RGBA premultiplied) with the masked area replaced.</summary>
    public static SKBitmap Fill(SKBitmap source, SKBitmap mask, SKBitmap? donorExclusion = null, CancellationToken cancellation = default, bool coherentSpot = false)
    {
        cancellation.ThrowIfCancellationRequested();
        using var combinedExclusion = donorExclusion == null ? null : SelectionMask.Combine(mask, donorExclusion, SelectionMode.Add);
        donorExclusion = combinedExclusion ?? donorExclusion;
        var hole = SelectionMask.Bounds(mask, 1);
        hole = Geometry.Intersect(hole, new SKRectI(0, 0, source.Width, source.Height));
        if (hole.IsEmpty) return Pixels.Clone(source);
        if ((long)hole.Width * hole.Height > MaxArea) throw new InvalidOperationException($"Select an area below {DocumentLimits.MaxRetouchPixels / 1_000_000} MP for Content-Aware Fill.");

        const int ring = 4;
        var area = Geometry.Intersect(new SKRectI(hole.Left - ring, hole.Top - ring, hole.Right + ring, hole.Bottom + ring),
            new SKRectI(0, 0, source.Width, source.Height));
        int w = area.Width, h = area.Height;
        var m = (byte*)mask.GetPixels();

        var src = (byte*)source.GetPixels();
        var excluded = donorExclusion == null ? null : (byte*)donorExclusion.GetPixels();
        var stride = source.RowBytes;
        bool Known(int x, int y) => m[(long)(y + area.Top) * mask.RowBytes + x + area.Left] == 0
            && (excluded == null || excluded[(long)(y + area.Top) * donorExclusion!.RowBytes + x + area.Left] == 0)
            && src[(long)(y + area.Top) * stride + (x + area.Left) * 4 + 3] > 0;

        // Border samples: known pixels inside the working area (the ring around the hole).
        var border = new List<(int X, int Y)>();
        var step = Math.Max(1, (int)Math.Sqrt((double)w * h / 3_000));
        for (var y = 0; y < h; y += step)
        for (var x = 0; x < w; x += step)
            if (Known(x, y)) border.Add((x, y));

        if (border.Count == 0) return Pixels.Clone(source); // No evidence: never turn a fully selected layer into black.
        var error = double.MaxValue;
        var offset = border.Count >= 8 ? BestOffset(source, donorExclusion ?? mask, area, border, out error, cancellation, coherentSpot) : null;
        if (offset is { } candidate && !CleanDonor(donorExclusion ?? mask, area, candidate)) offset = null;
        // A small repair should carry one continuous piece of real texture, rather than
        // switching donors pixel by pixel and amplifying their grain. Match structure
        // independently of local tone, then harmonically adapt the donor to the boundary.
        if (coherentSpot && offset is { } spot)
            return PatchBlend.Blend(source, source, mask, spot, iterations: 96, preserveAlpha: false, cancellation: cancellation, boundaryExclusion: donorExclusion);
        if ((offset == null || error > 64) && ExemplarFill.TryFill(source, mask, hole, donorExclusion, cancellation) is { } texture)
        {
            using (texture)
            {
                cancellation.ThrowIfCancellationRequested();
                using (var refined = PatchMatchRefinement.Fill(source, texture, mask, donorExclusion, cancellation))
                    return PatchBlend.Blend(source, refined, mask, SKPointI.Empty, preserveAlpha: false, cancellation: cancellation);
            }
        }

        var result = Pixels.Clone(source);
        var patch = new float[w * h * 4];
        var weight = new float[w * h];
        var difference = new float[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = y * w + x;
            var target = src + (long)(y + area.Top) * stride + (x + area.Left) * 4;
            if (offset is { } o)
            {
                var from = src + (long)(y + area.Top + o.Y) * stride + (x + area.Left + o.X) * 4;
                for (var c = 0; c < 4; c++) patch[i * 4 + c] = from[c];
            }
            if (!Known(x, y)) continue;
            weight[i] = 1;
            for (var c = 0; c < 4; c++) difference[i * 4 + c] = target[c] - patch[i * 4 + c];
        }
        PushPull.Fill(difference, weight, w, h, 4);

        var dst = (byte*)result.GetPixels();
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = y * w + x;
            var cover = m[(long)(y + area.Top) * mask.RowBytes + x + area.Left] / 255f;
            if (cover <= 0) continue;
            var p = dst + (long)(y + area.Top) * stride + (x + area.Left) * 4;
            var alpha = Math.Clamp(patch[i * 4 + 3] + difference[i * 4 + 3], 0, 255);
            for (var c = 0; c < 4; c++)
            {
                var filled = Math.Clamp(patch[i * 4 + c] + difference[i * 4 + c], 0, c == 3 ? 255 : alpha);
                p[c] = (byte)(p[c] * (1 - cover) + filled * cover + 0.5f);
            }
        }
        Pixels.Invalidate(result);
        return result;
    }

    private static bool CleanDonor(SKBitmap mask, SKRectI area, SKPointI offset)
    {
        var pixels = (byte*)mask.GetPixels();
        // Search scoring subsamples large holes. Validate the winning FULL patch exactly,
        // including its context, so even a one-pixel/feathered blemish cannot become a donor.
        for (var y = area.Top; y < area.Bottom; y++)
        for (var x = area.Left; x < area.Right; x++)
            if (pixels[(long)(y + offset.Y) * mask.RowBytes + x + offset.X] != 0) return false;
        return true;
    }

    private static SKPointI? BestOffset(SKBitmap source, SKBitmap mask, SKRectI area, List<(int X, int Y)> border, out double error, CancellationToken cancellation, bool structureMatch)
    {
        var src = (byte*)source.GetPixels();
        var m = (byte*)mask.GetPixels();
        var stride = source.RowBytes;
        int reachX = Math.Clamp(area.Width * 2, 24, 160), reachY = Math.Clamp(area.Height * 2, 24, 160);
        var coarse = Math.Max(1, Math.Max(area.Width, area.Height) / 12);
        var holeSamples = new List<(int X, int Y)>();
        var holeStep = Math.Max(1, (int)Math.Sqrt((double)area.Width * area.Height / 2_000));
        for (var y = area.Top; y < area.Bottom; y += holeStep)
        for (var x = area.Left; x < area.Right; x += holeStep)
            if (m[(long)y * mask.RowBytes + x] > 0) holeSamples.Add((x, y));

        double Cost(int ox, int oy)
        {
            cancellation.ThrowIfCancellationRequested();
            if (Math.Abs(ox) < area.Width / 2 && Math.Abs(oy) < area.Height / 2) return double.MaxValue;
            if (area.Left + ox < 0 || area.Top + oy < 0 || area.Right + ox > source.Width || area.Bottom + oy > source.Height) return double.MaxValue;
            // The patch that lands in the hole has to come from real, unmasked pixels.
            foreach (var (x, y) in holeSamples)
            {
                if (m[(long)(y + oy) * mask.RowBytes + x + ox] > 0) return double.MaxValue;
                if (src[(long)(y + oy) * stride + (x + ox) * 4 + 3] < 250 && src[(long)y * stride + x * 4 + 3] >= 250) return double.MaxValue;
            }
            double sum = 0;
            Span<double> shift = stackalloc double[4];
            shift.Clear();
            foreach (var (bx, by) in border)
            {
                var a = src + (long)(by + area.Top) * stride + (bx + area.Left) * 4;
                var b = src + (long)(by + area.Top + oy) * stride + (bx + area.Left + ox) * 4;
                for (var c = 0; c < 4; c++) { double d = a[c] - b[c]; sum += d * d; shift[c] += d; }
            }
            if (structureMatch)
                for (var c = 0; c < 3; c++) sum -= .94 * shift[c] * shift[c] / border.Count;
            // Prefer nearby patches when several match equally well.
            return sum / border.Count + 0.002 * (ox * ox + oy * oy) / Math.Max(1, area.Width * area.Height) * 255;
        }

        var best = double.MaxValue;
        SKPointI? winner = null;
        for (var oy = -reachY; oy <= reachY; oy += coarse)
        for (var ox = -reachX; ox <= reachX; ox += coarse)
        {
            var cost = Cost(ox, oy);
            if (cost < best) { best = cost; winner = new SKPointI(ox, oy); }
        }
        error = best;
        if (winner is not { } found || coarse == 1) return winner;
        for (var oy = found.Y - coarse; oy <= found.Y + coarse; oy++)
        for (var ox = found.X - coarse; ox <= found.X + coarse; ox++)
        {
            var cost = Cost(ox, oy);
            if (cost < best) { best = cost; winner = new SKPointI(ox, oy); }
        }
        error = best;
        return winner;
    }
}

/// <summary>Fills the unweighted samples of a grid smoothly from the weighted ones, using an image pyramid.</summary>
internal static class PushPull
{
    public static void Fill(float[] values, float[] weight, int width, int height, int channels)
    {
        if (width <= 1 && height <= 1) return;
        var complete = true;
        foreach (var value in weight) if (value <= 0) { complete = false; break; }
        if (complete) return;

        int halfW = (width + 1) / 2, halfH = (height + 1) / 2;
        var coarse = new float[halfW * halfH * channels];
        var coarseWeight = new float[halfW * halfH];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var wgt = weight[y * width + x];
            if (wgt <= 0) continue;
            var j = (y / 2) * halfW + x / 2;
            coarseWeight[j] += wgt;
            for (var c = 0; c < channels; c++) coarse[j * channels + c] += values[(y * width + x) * channels + c] * wgt;
        }
        for (var j = 0; j < coarseWeight.Length; j++)
        {
            if (coarseWeight[j] <= 0) continue;
            for (var c = 0; c < channels; c++) coarse[j * channels + c] /= coarseWeight[j];
            coarseWeight[j] = 1;
        }
        Fill(coarse, coarseWeight, halfW, halfH, channels);

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (weight[y * width + x] > 0) continue;
            // Bilinear sample of the coarse level, so filled areas come out smooth rather than blocky.
            float fx = (x - 0.5f) / 2, fy = (y - 0.5f) / 2;
            int x0 = Math.Clamp((int)MathF.Floor(fx), 0, halfW - 1), y0 = Math.Clamp((int)MathF.Floor(fy), 0, halfH - 1);
            int x1 = Math.Min(halfW - 1, x0 + 1), y1 = Math.Min(halfH - 1, y0 + 1);
            float tx = Math.Clamp(fx - x0, 0, 1), ty = Math.Clamp(fy - y0, 0, 1);
            for (var c = 0; c < channels; c++)
            {
                var top = coarse[(y0 * halfW + x0) * channels + c] * (1 - tx) + coarse[(y0 * halfW + x1) * channels + c] * tx;
                var bottom = coarse[(y1 * halfW + x0) * channels + c] * (1 - tx) + coarse[(y1 * halfW + x1) * channels + c] * tx;
                values[(y * width + x) * channels + c] = top * (1 - ty) + bottom * ty;
            }
            weight[y * width + x] = 1;
        }
    }
}
