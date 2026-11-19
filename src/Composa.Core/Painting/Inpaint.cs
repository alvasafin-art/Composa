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
        bool MatchesGrid(SKBitmap bitmap) => bitmap.ColorType == SKColorType.Alpha8 && bitmap.Width == source.Width && bitmap.Height == source.Height;
        if (!MatchesGrid(mask)) throw new ArgumentException("Expected an Alpha8 repair mask matching the source dimensions.", nameof(mask));
        if (donorExclusion != null && !MatchesGrid(donorExclusion)) throw new ArgumentException("Expected an Alpha8 sampling mask matching the source dimensions.", nameof(donorExclusion));
        using var combinedExclusion = donorExclusion == null ? null : SelectionMask.Combine(mask, donorExclusion, SelectionMode.Add);
        donorExclusion = combinedExclusion ?? donorExclusion;
        var hole = SelectionMask.Bounds(mask, 1);
        hole = Geometry.Intersect(hole, new SKRectI(0, 0, source.Width, source.Height));
        if (hole.IsEmpty) return Pixels.Clone(source);
        if ((long)hole.Width * hole.Height > MaxArea) throw new InvalidOperationException($"Select an area below {DocumentLimits.MaxRetouchPixels / 1_000_000} MP for Content-Aware Fill.");

        if (RepeatedPatch.Find(source, donorExclusion ?? mask, hole, cancellation) is { } repeated)
            return PatchBlend.Blend(source, source, mask, repeated, preserveAlpha: false, cancellation: cancellation, boundaryExclusion: donorExclusion);

        if (TryFlatSurround(source, mask, donorExclusion, hole, cancellation) is { } flat) return flat;

        if (StrokeContours.TryFill(source, mask, donorExclusion, hole) is { } contours) return contours;

        var area = hole;
        int w, h;
        var m = (byte*)mask.GetPixels();

        var src = (byte*)source.GetPixels();
        var floating = source.ColorType == SKColorType.RgbaF32;
        var colors = new PixelBuffer(source);
        var excluded = donorExclusion == null ? null : (byte*)donorExclusion.GetPixels();
        var stride = source.RowBytes;
        bool Known(int x, int y) => m[(long)(y + area.Top) * mask.RowBytes + x + area.Left] == 0
            && (excluded == null || excluded[(long)(y + area.Top) * donorExclusion!.RowBytes + x + area.Left] == 0)
            && (floating ? colors.Read(x + area.Left, y + area.Top).A > 0 : src[(long)(y + area.Top) * stride + (x + area.Left) * 4 + 3] > 0);

        // Border samples: known pixels inside the working area (the ring around the hole).
        var border = new List<(int X, int Y)>();
        // A selection can clip a brush stroke while its entire footprint remains excluded
        // from sampling. Look beyond that footprint before treating the layer as having no donors.
        for (var ring = 4; ; ring *= 2)
        {
            area = hole;
            area.Inflate(ring, ring);
            area = Geometry.Intersect(area, new SKRectI(0, 0, source.Width, source.Height));
            w = area.Width; h = area.Height;
            var step = Math.Max(1, (int)Math.Sqrt((double)w * h / 3000));
            border.Clear();
            for (var y = 0; y < h; y += step)
            for (var x = 0; x < w; x += step)
                if (Known(x, y)) border.Add((x, y));
            if (border.Count >= 8 || ring >= 32 || donorExclusion == null) break;
        }

        if (border.Count == 0) return Pixels.Clone(source); // No evidence: never turn a fully selected layer into black.
        var error = double.MaxValue;
        var offset = border.Count >= 8 ? BestOffset(source, donorExclusion ?? mask, area, border, out error, cancellation, coherentSpot) : null;
        // A small repair should carry one continuous piece of real texture, rather than
        // switching donors pixel by pixel and amplifying their grain. Match structure
        // independently of local tone, then harmonically adapt the donor to the boundary.
        if (coherentSpot && offset is { } spot)
            return PatchBlend.Blend(source, source, mask, spot, iterations: 96, preserveAlpha: false, cancellation: cancellation, boundaryExclusion: donorExclusion);
        if (offset is { } local && error > 64 && border.Count >= 8 && Math.Min(hole.Width, hole.Height) >= 16)
        {
            // Noise and fine grain make even a good donor differ from its perimeter.
            // Compare its mismatch to ordinary intact pixel pairs at the same location.
            var differences = new List<float>(border.Count);
            foreach (var (x, y) in border)
            {
                int xx = x + 4 < w ? x + 4 : x - 4;
                int yy = y + 3 < h ? y + 3 : y - 3;
                if (xx < 0 || yy < 0 || !Known(xx, yy)) continue;
                var a = colors.Read(area.Left + x, area.Top + y);
                var b = colors.Read(area.Left + xx, area.Top + yy);
                float dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
                differences.Add(255 * 255 * (dr * dr + dg * dg + db * db));
            }
            if (differences.Count >= 8)
            {
                differences.Sort();
                if (error <= 2 * differences[differences.Count / 2])
                    return PatchBlend.Blend(source, source, mask, local, preserveAlpha: false, cancellation: cancellation, boundaryExclusion: donorExclusion);
            }
        }
        if (!coherentSpot && (offset == null || error > 64)
            && GuidedPatchFill.TryFill(source, mask, donorExclusion, hole, cancellation) is { } guided) return guided;
        if ((offset == null || error > 64) && ExemplarFill.TryFill(source, mask, hole, donorExclusion, cancellation, applyCoverage: false) is { } texture)
        {
            using (texture)
            {
                cancellation.ThrowIfCancellationRequested();
                using (var refined = PatchMatchRefinement.Fill(source, texture, mask, donorExclusion, cancellation))
                {
                    EdgeContinuation.Refine(source, refined, mask, donorExclusion, hole, cancellation);
                    return PatchBlend.Blend(source, refined, mask, SKPointI.Empty, preserveAlpha: false, cancellation: cancellation);
                }
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
            var target = src + (long)(y + area.Top) * stride + (x + area.Left) * source.BytesPerPixel;
            var targetColor = floating ? colors.Read(x + area.Left, y + area.Top) * 255 : default;
            if (offset is { } o)
            {
                var from = src + (long)(y + area.Top + o.Y) * stride + (x + area.Left + o.X) * source.BytesPerPixel;
                var donorColor = floating ? colors.Read(x + area.Left + o.X, y + area.Top + o.Y) * 255 : default;
                for (var c = 0; c < 4; c++) patch[i * 4 + c] = floating ? donorColor[c] : from[c];
            }
            if (!Known(x, y)) continue;
            weight[i] = 1;
            for (var c = 0; c < 4; c++) difference[i * 4 + c] = (floating ? targetColor[c] : target[c]) - patch[i * 4 + c];
        }
        PushPull.Fill(difference, weight, w, h, 4);

        var dst = (byte*)result.GetPixels();
        var output = new PixelBuffer(result);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = y * w + x;
            var cover = m[(long)(y + area.Top) * mask.RowBytes + x + area.Left] / 255f;
            if (cover <= 0) continue;
            var p = dst + (long)(y + area.Top) * stride + (x + area.Left) * source.BytesPerPixel;
            var alpha = Math.Clamp(patch[i * 4 + 3] + difference[i * 4 + 3], 0, 255);
            if (floating)
            {
                var filled = new PixelValue(Math.Clamp(patch[i * 4] + difference[i * 4], 0, alpha) / 255,
                    Math.Clamp(patch[i * 4 + 1] + difference[i * 4 + 1], 0, alpha) / 255,
                    Math.Clamp(patch[i * 4 + 2] + difference[i * 4 + 2], 0, alpha) / 255, alpha / 255);
                output.Write(x + area.Left, y + area.Top, output.Read(x + area.Left, y + area.Top) * (1 - cover) + filled * cover);
                continue;
            }
            for (var c = 0; c < 4; c++)
            {
                var filled = Math.Clamp(patch[i * 4 + c] + difference[i * 4 + c], 0, c == 3 ? 255 : alpha);
                p[c] = (byte)(p[c] * (1 - cover) + filled * cover + 0.5f);
            }
        }
        Pixels.Invalidate(result);
        return result;
    }

    private static SKBitmap? TryFlatSurround(SKBitmap source, SKBitmap mask, SKBitmap? exclusion,
        SKRectI hole, CancellationToken cancellation)
    {
        // A genuinely flat surround is common on printed labels. Copying an unrelated textured patch
        // introduces faint marks, even if its average color matches perfectly.
        var area = hole;
        area.Inflate(2, 2);
        area = Geometry.Intersect(area, new SKRectI(0, 0, source.Width, source.Height));
        var colors = new PixelBuffer(source);
        var selected = (byte*)mask.GetPixels();
        var excluded = (byte*)(exclusion ?? mask).GetPixels();
        int stride = (exclusion ?? mask).RowBytes;
        PixelValue surround = default;
        int samples = 0;
        for (int y = area.Top; y < area.Bottom; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = area.Left; x < area.Right; x++)
            {
                if (excluded[(long)y * stride + x] != 0) continue;
                var color = colors.Read(x, y);
                if (color.A < 1 || samples > 0 && !color.Equals(surround)) return null;
                surround = color;
                samples++;
            }
        }
        if (samples < 8) return null;
        var result = Pixels.Clone(source);
        var output = new PixelBuffer(result);
        for (int y = hole.Top; y < hole.Bottom; y++)
        for (int x = hole.Left; x < hole.Right; x++)
        {
            float coverage = selected[(long)y * mask.RowBytes + x] / 255f;
            if (coverage > 0) output.Write(x, y, colors.Read(x, y) * (1 - coverage) + surround * coverage);
        }
        Pixels.Invalidate(result);
        return result;
    }

    private static bool CleanDonor(SKBitmap mask, SKRectI area, SKPointI offset)
    {
        var pixels = (byte*)mask.GetPixels();
        var stride = mask.RowBytes;
        // Search scoring subsamples large holes. Validate the winning FULL patch exactly,
        // including its context, so even a one-pixel/feathered blemish cannot become a donor.
        for (var y = area.Top; y < area.Bottom; y++)
            if (new ReadOnlySpan<byte>(pixels + (long)(y + offset.Y) * stride + area.Left + offset.X, area.Width).ContainsAnyExcept((byte)0))
                return false;
        return true;
    }

    private static SKPointI? BestOffset(SKBitmap source, SKBitmap mask, SKRectI area, List<(int X, int Y)> border, out double error, CancellationToken cancellation, bool structureMatch)
    {
        var src = (byte*)source.GetPixels();
        var floating = source.ColorType == SKColorType.RgbaF32;
        var colors = new PixelBuffer(source);
        var m = (byte*)mask.GetPixels();
        var stride = source.RowBytes;
        var maskStride = mask.RowBytes;
        var borderBytes = border.Select(p => (long)(area.Top + p.Y) * stride + (area.Left + p.X) * 4).ToArray();
        double DistancePenalty(int ox, int oy) => .002 * (ox * ox + oy * oy) / Math.Max(1, area.Width * area.Height) * 255;
        int reachX = Math.Clamp(area.Width * 2, 24, 160), reachY = Math.Clamp(area.Height * 2, 24, 160);
        var coarse = Math.Clamp(Math.Max(area.Width, area.Height) / 12, 1, 16);
        var holeSamples = new List<(int X, int Y)>();
        var holeStep = Math.Max(1, (int)Math.Sqrt((double)area.Width * area.Height / 2_000));
        for (var y = area.Top; y < area.Bottom; y += holeStep)
        for (var x = area.Left; x < area.Right; x += holeStep)
            if (m[(long)y * maskStride + x] > 0) holeSamples.Add((x, y));

        double Cost(int ox, int oy)
        {
            cancellation.ThrowIfCancellationRequested();
            if (Math.Abs(ox) < area.Width / 2 && Math.Abs(oy) < area.Height / 2) return double.MaxValue;
            if (area.Left + ox < 0 || area.Top + oy < 0 || area.Right + ox > colors.Width || area.Bottom + oy > colors.Height) return double.MaxValue;
            if (!floating)
            {
                var delta = (long)oy * stride + ox * 4;
                long squared = 0;
                int red = 0, green = 0, blue = 0;
                foreach (var pos in borderBytes)
                {
                    var a = src + pos;
                    var b = a + delta;
                    int r = a[0] - b[0], g = a[1] - b[1], bl = a[2] - b[2], alpha = a[3] - b[3];
                    squared += r * r + g * g + bl * bl + alpha * alpha;
                    red += r; green += g; blue += bl;
                }
                var cost = (double)squared;
                if (structureMatch) cost -= .94 * ((double)red * red + (double)green * green + (double)blue * blue) / border.Count;
                return cost / border.Count + DistancePenalty(ox, oy);
            }
            double sum = 0;
            Span<double> shift = stackalloc double[4];
            shift.Clear();
            foreach (var (bx, by) in border)
            {
                var ca = colors.Read(bx + area.Left, by + area.Top) * 255;
                var cb = colors.Read(bx + area.Left + ox, by + area.Top + oy) * 255;
                for (var c = 0; c < 4; c++) { double d = ca[c] - cb[c]; sum += d * d; shift[c] += d; }
            }
            if (structureMatch)
                for (var c = 0; c < 3; c++) sum -= .94 * shift[c] * shift[c] / border.Count;
            // Prefer nearby patches when several match equally well.
            return sum / border.Count + DistancePenalty(ox, oy);
        }

        float Luma(int x, int y)
        {
            var p = colors.Read(x, y);
            return p.R * .2126f + p.G * .7152f + p.B * .0722f;
        }
        var luminance = border.Select(p => Luma(area.Left + p.X, area.Top + p.Y)).Order().ToArray();
        var low = luminance[luminance.Length / 16];
        var high = luminance[luminance.Length - 1 - luminance.Length / 16];
        var targetMean = luminance.Average();
        var margin = .06f + (high - low) * .25f;

        bool Suitable(int ox, int oy)
        {
            if (!CleanDonor(mask, area, new SKPointI(ox, oy))) return false;
            float donorMean = 0;
            foreach (var (x, y) in border) donorMean += Luma(x + area.Left + ox, y + area.Top + oy);
            var shift = targetMean - donorMean / border.Count;
            var outliers = 0;
            foreach (var (x, y) in holeSamples)
            {
                var p = colors.Read(x + ox, y + oy);
                if (p.A < 250 / 255f && colors.Read(x, y).A >= 250 / 255f) return false;
                // A clean perimeter can surround an unrelated letter or a dark object.
                // Such a patch would stamp that object into an otherwise plain background.
                var luminanceAtDonor = Luma(x + ox, y + oy) + shift;
                if ((luminanceAtDonor < low - margin || luminanceAtDonor > high + margin)
                    && ++outliers > Math.Max(2, holeSamples.Count / 16)) return false;
            }
            return true;
        }

        var best = double.MaxValue;
        SKPointI? winner = null;
        for (var oy = -reachY; oy <= reachY; oy += coarse)
        for (var ox = -reachX; ox <= reachX; ox += coarse)
        {
            var cost = Cost(ox, oy);
            if (cost < best && Suitable(ox, oy)) { best = cost; winner = new SKPointI(ox, oy); }
        }
        error = best;
        if (winner is not { } found || coarse == 1) return winner;
        for (var oy = found.Y - coarse; oy <= found.Y + coarse; oy++)
        for (var ox = found.X - coarse; ox <= found.X + coarse; ox++)
        {
            var cost = Cost(ox, oy);
            if (cost < best && Suitable(ox, oy)) { best = cost; winner = new SKPointI(ox, oy); }
        }
        error = best;
        return winner;
    }
}

/// <summary>Fills the unweighted samples of a grid smoothly from the weighted ones, using an image pyramid.</summary>
internal static class PushPull
{
    public static void Fill(float[] values, ReadOnlySpan<float> weight, int width, int height, int channels)
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
        }
    }
}
