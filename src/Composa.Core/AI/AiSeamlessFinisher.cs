using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.AI;

/// <summary>Automatic registration and a screened harmonic color residual. No texture synthesis or contrast gain.</summary>
public static class AiSeamlessFinisher
{
    public static SKBitmap Match(SKBitmap generated, SKBitmap reference, SKBitmap coverage, SKRectI decodedArea, int seamWidth, bool matchFlatBackground = true, bool continueUniformStrip = false, bool alignPixels = true, bool keepGeneratedContext = false)
    {
        // Generated/model pixels are RGB8. A caller may retain a precise reference; read an owned
        // display copy for model residual analysis, never reinterpret its float memory as bytes.
        if (!Pixels.IsSrgb8(generated) || !Pixels.IsSrgb8(reference))
        {
            using var resultDisplay = !Pixels.IsSrgb8(generated) ? Pixels.DisplayCopy(generated) : null;
            using var referenceDisplay = !Pixels.IsSrgb8(reference) ? Pixels.DisplayCopy(reference) : null;
            return Match(resultDisplay ?? generated, referenceDisplay ?? reference, coverage, decodedArea, seamWidth, matchFlatBackground, continueUniformStrip, alignPixels, keepGeneratedContext);
        }
        if (generated.Width != reference.Width || generated.Height != reference.Height
            || coverage.Width != reference.Width || coverage.Height != reference.Height)
            throw new ArgumentException("Generated pixels, reference and coverage must share coordinates.");
        var area = SKRectI.Intersect(decodedArea, reference.Info.Rect);
        // A fill layer can reveal the entire decoded crop when its mask is edited.
        // Keep document coordinates and leave pixels beyond the model's field of view transparent.
        var result = keepGeneratedContext ? Pixels.NewLike(generated) : Pixels.Clone(generated);
        if (area.IsEmpty) return result;
        if (keepGeneratedContext)
        {
            for (var y = area.Top; y < area.Bottom; y++)
                generated.GetPixelSpan().Slice(y * generated.RowBytes + area.Left * 4, area.Width * 4)
                    .CopyTo(result.GetPixelSpan().Slice(y * result.RowBytes + area.Left * 4));
        }
        if (continueUniformStrip && UniformStrip(reference, coverage, area) is { } tone)
        {
            for (var y = area.Top; y < area.Bottom; y++) for (var x = area.Left; x < area.Right; x++)
                if (coverage.GetPixel(x, y).Alpha != 0) result.SetPixel(x, y, tone);
                else if (!keepGeneratedContext) result.SetPixel(x, y, reference.GetPixel(x, y));
            Pixels.Invalidate(result); return result;
        }
        if (alignPixels)
        {
            var offset = Registration(generated, reference, coverage, area, area.Location);
            if (offset != SKPoint.Empty)
            {
                using var crop = Pixels.ImageOf(generated).Subset(area);
                DrawEdit(crop, result, area, offset);
            }
        }
        var flat = matchFlatBackground && IsFlatContinuation(result, reference, coverage, area);
        var field = Residual.Build(result, reference, coverage, area, seamWidth, flat, keepGeneratedContext);
        var pixels = result.GetPixelSpan(); var original = reference.GetPixelSpan(); var mask = coverage.GetPixelSpan();
        for (var y = area.Top; y < area.Bottom; y++) for (var x = area.Left; x < area.Right; x++)
        {
            var index = y * result.RowBytes + x * 4;
            if (!keepGeneratedContext && mask[y * coverage.RowBytes + x] == 0)
            {
                original.Slice(y * reference.RowBytes + x * 4, 4).CopyTo(pixels.Slice(index, 4));
                continue;
            }
            // An editable fill keeps the decoded context. Apply the same continuous residual
            // on both sides of the mask so revealing those pixels does not expose a color step.
            var alpha = pixels[index + 3];
            if (alpha == 0 || field == null) continue;
            for (var c = 0; c < 3; c++)
            {
                var straight = pixels[index + c] * 255.0 / alpha;
                var corrected = Math.Clamp(straight + field.At(x, y, c), 0, 255);
                pixels[index + c] = (byte)Math.Round(corrected * alpha / 255);
            }
        }
        Pixels.Invalidate(result); return result;
    }

    // The caller owns this freshly decoded RGB8 result.
    public static void AlignEdit(SKBitmap generated, SKBitmap reference)
    {
        if (!Pixels.IsSrgb8(generated)) return;
        var offset = EstimateOffset(generated, reference, reference.Info.Rect, null);
        if (offset == SKPoint.Empty) return;
        var native = new SKPoint(offset.X * generated.Width / reference.Width, offset.Y * generated.Height / reference.Height);
        using var snapshot = Pixels.Clone(generated);
        DrawEdit(Pixels.ImageOf(snapshot), generated, generated.Info.Rect, native);
    }

    /// <summary>Fit a decoded rectangle and correct any shift in a single resampling pass.</summary>
    public static SKBitmap FitEdit(SKBitmap generated, SKBitmap reference, SKRectI area, SKBitmap? coverage, bool align)
    {
        if (!align || !Pixels.IsSrgb8(generated)) return Sample(generated, area.Size, SKPoint.Empty);
        if (generated.Info.Size == area.Size)
            return Sample(generated, area.Size, EstimateOffset(generated, reference, area, coverage));

        // Use this analysis frame directly if no correction is needed.
        var proxy = Sample(generated, area.Size, SKPoint.Empty);
        try
        {
            var offset = EstimateOffset(proxy, reference, area, coverage);
            if (offset == SKPoint.Empty) return proxy;
            var result = Sample(generated, area.Size, offset);
            proxy.Dispose();
            return result;
        }
        catch
        {
            proxy.Dispose();
            throw;
        }
    }

    private static SKPoint EstimateOffset(SKBitmap generated, SKBitmap reference, SKRectI area, SKBitmap? coverage)
    {
        using var display = Pixels.IsSrgb8(reference) ? null : Pixels.DisplayCopy(reference);
        // Analyze in destination pixels. The final image is sampled from the original decoded grid.
        using var proxy = generated.Info.Size == area.Size ? null : Sample(generated, area.Size, SKPoint.Empty);
        return Registration(proxy ?? generated, display ?? reference, coverage, area, SKPointI.Empty);
    }

    private static SKBitmap Sample(SKBitmap image, SKSizeI size, SKPoint offset)
    {
        if (image.Info.Size == size && offset == SKPoint.Empty) return Pixels.Clone(image);
        var result = Pixels.NewColor(size.Width, size.Height);
        DrawEdit(Pixels.ImageOf(image), result, result.Info.Rect, offset);
        return result;
    }

    private static void DrawEdit(SKImage source, SKBitmap result, SKRectI area, SKPoint offset)
    {
        var matrix = SKMatrix.CreateScale((float)area.Width / source.Width, (float)area.Height / source.Height);
        matrix.TransX = area.Left - offset.X;
        matrix.TransY = area.Top - offset.Y;
        var exact = area.Width == source.Width && area.Height == source.Height
            && offset.X == MathF.Round(offset.X) && offset.Y == MathF.Round(offset.Y);
        using (var canvas = new SKCanvas(result))
        using (var shader = source.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
            exact ? new SKSamplingOptions(SKFilterMode.Nearest) : new SKSamplingOptions(SKCubicResampler.CatmullRom), matrix))
        using (var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src })
            canvas.DrawRect(new SKRect(area.Left, area.Top, area.Right, area.Bottom), paint);
        Pixels.Invalidate(result);
    }

    // A narrow automatic continuation of a verified constant background has no
    // new subject to synthesize. Never apply this to an ordinary prompted fill,
    // a large extension, or textured/transparent surroundings.
    private static SKColor? UniformStrip(SKBitmap reference, SKBitmap coverage, SKRectI area)
    {
        var core = SelectionMask.Bounds(coverage, 255);
        var shorter = Math.Min(core.Width, core.Height); var longer = Math.Max(core.Width, core.Height);
        if (core.IsEmpty) return null;
        if (shorter > 32 || shorter >= longer * .2) return UniformCanvasMargin(reference, coverage, area);
        var min = new[] { 255, 255, 255 }; var max = new int[3]; var sum = new long[3]; var count = 0;
        for (var y = area.Top; y < area.Bottom; y++) for (var x = area.Left; x < area.Right; x++)
        {
            if (coverage.GetPixel(x, y).Alpha != 0) continue;
            var pixel = reference.GetPixel(x, y); if (pixel.Alpha != 255) return null;
            for (var c = 0; c < 3; c++)
            { var value = c == 0 ? pixel.Red : c == 1 ? pixel.Green : pixel.Blue; min[c] = Math.Min(min[c], value); max[c] = Math.Max(max[c], value); sum[c] += value; if (max[c] - min[c] > 1) return null; }
            count++;
        }
        return count < 64 ? null : new SKColor((byte)((sum[0] + count / 2) / count), (byte)((sum[1] + count / 2) / count), (byte)((sum[2] + count / 2) / count));
    }

    // Extending two or more edges makes the mask bounds as large as the canvas,
    // even when the actual extension is a narrow strip of solid studio backdrop.
    private static SKColor? UniformCanvasMargin(SKBitmap reference, SKBitmap mask, SKRectI area)
    {
        var frame = Pixels.ContentBounds(reference);
        if (frame.IsEmpty) return null;
        var left = frame.Left - area.Left; var right = area.Right - frame.Right;
        var top = frame.Top - area.Top; var bottom = area.Bottom - frame.Bottom;
        var margin = Math.Max(Math.Max(left, right), Math.Max(top, bottom));
        if (margin < 1 || margin > Math.Min(32, Math.Min(frame.Width, frame.Height) / 5)) return null;
        var band = Math.Max(8, margin);
        SKColor? tone = null;
        for (var y = area.Top; y < area.Bottom; y++) for (var x = area.Left; x < area.Right; x++)
        {
            var coverage = mask.GetPixel(x, y).Alpha;
            if (!frame.Contains(x, y))
            {
                if (coverage != 255) return null;
                continue;
            }
            var bordersExtension = left > 0 && x < frame.Left + band || right > 0 && x >= frame.Right - band
                || top > 0 && y < frame.Top + band || bottom > 0 && y >= frame.Bottom - band;
            if (!bordersExtension && coverage == 0) continue;
            if (!bordersExtension) return null;
            var pixel = reference.GetPixel(x, y);
            if (pixel.Alpha != 255 || tone.HasValue && tone.Value != pixel) return null;
            tone = pixel;
        }
        return tone;
    }

    private static bool IsFlatContinuation(SKBitmap generated, SKBitmap reference, SKBitmap mask, SKRectI area)
    {
        var sum = new double[6]; var squares = new double[6]; var counts = new int[2];
        var step = Math.Max(1, Math.Max(area.Width, area.Height) / 96);
        for (var y = area.Top; y < area.Bottom; y += step) for (var x = area.Left; x < area.Right; x += step)
        {
            var alpha = mask.GetPixel(x, y).Alpha;
            var group = alpha == 0 ? 0 : alpha == 255 ? 1 : -1;
            if (group < 0) continue;
            var color = group == 0 ? reference.GetPixel(x, y) : generated.GetPixel(x, y);
            if (color.Alpha != 255) continue;
            for (var c = 0; c < 3; c++) { var value = c == 0 ? color.Red : c == 1 ? color.Green : color.Blue;
                var i = group * 3 + c; sum[i] += value; squares[i] += value * value; }
            counts[group]++;
        }
        if (counts.Any(n => n < 64)) return false;
        var delta = new double[3];
        for (var c = 0; c < 3; c++)
        {
            var sourceMean = sum[c] / counts[0]; var generatedMean = sum[c + 3] / counts[1];
            if (squares[c] / counts[0] - sourceMean * sourceMean > 4
                || squares[c + 3] / counts[1] - generatedMean * generatedMean > 64) return false;
            delta[c] = sourceMean - generatedMean;
            if (Math.Abs(delta[c]) > 24) return false;
        }
        // Only low-contrast, nearly neutral exposure drift on an already flat
        // background. Colored objects and intentional relighting stay independent.
        return delta.Max() - delta.Min() < 4;
    }

    private readonly record struct Anchor(int X, int Y, double Gx, double Gy, int Quadrant);

    private static SKPoint Registration(SKBitmap generated, SKBitmap reference, SKBitmap? mask, SKRectI area, SKPointI origin)
    {
        var radius = Math.Clamp(Math.Min(area.Width, area.Height) / 32, 3, 8);
        var margin = radius + 2;
        var step = Math.Max(2, Math.Max(area.Width, area.Height) / 64);
        var anchors = new List<Anchor>();
        var counts = new int[4];
        for (var y = margin; y < area.Height - margin; y += step)
        for (var x = margin; x < area.Width - margin; x += step)
        {
            var sx = area.Left + x; var sy = area.Top + y;
            if (mask != null && (mask.GetPixel(sx, sy).Alpha != 0 ||
                mask.GetPixel(sx - 1, sy).Alpha != 0 || mask.GetPixel(sx + 1, sy).Alpha != 0 ||
                mask.GetPixel(sx, sy - 1).Alpha != 0 || mask.GetPixel(sx, sy + 1).Alpha != 0)) continue;
            var (gx, gy, valid) = Gradient(reference, sx, sy);
            if (!valid || gx * gx + gy * gy < 100) continue;
            var quadrant = (x >= area.Width / 2 ? 1 : 0) + (y >= area.Height / 2 ? 2 : 0);
            anchors.Add(new(origin.X + x, origin.Y + y, gx, gy, quadrant));
            counts[quadrant]++;
        }
        var requiredRegions = mask == null ? 3 : 2;
        if (anchors.Count < 24 || counts.Count(n => n >= 6) < requiredRegions) return SKPoint.Empty;

        double Error(SKPoint offset, double[]? regions = null)
        {
            var dx = (int)MathF.Floor(offset.X); var dy = (int)MathF.Floor(offset.Y);
            var fx = offset.X - dx; var fy = offset.Y - dy;
            double sum = 0;
            foreach (var a in anchors)
            {
                var x = a.X + dx; var y = a.Y + dy;
                var (gx, gy, valid) = Row(x, y, fx);
                if (fy != 0)
                {
                    var (bx, by, bottomValid) = Row(x, y + 1, fx);
                    gx += fy * (bx - gx); gy += fy * (by - gy); valid &= bottomValid;
                }
                var ex = a.Gx - gx; var ey = a.Gy - gy;
                var error = valid ? Math.Min(1, (ex * ex + ey * ey) / (a.Gx * a.Gx + a.Gy * a.Gy)) : 1;
                sum += error;
                if (regions != null) regions[a.Quadrant] += error;
            }
            return sum / anchors.Count;
        }

        (double X, double Y, bool Valid) Row(int x, int y, float fraction)
        {
            var left = Gradient(generated, x, y);
            if (fraction == 0) return left;
            var right = Gradient(generated, x + 1, y);
            return (left.X + fraction * (right.X - left.X), left.Y + fraction * (right.Y - left.Y), left.Valid && right.Valid);
        }

        var baselineRegions = new double[4]; var bestRegions = new double[4];
        var baseline = Error(SKPoint.Empty, baselineRegions);
        if (baseline < .035) return SKPoint.Empty;
        var best = baseline;
        var shift = SKPoint.Empty;
        var integerScores = new List<(SKPoint Shift, double Error)>();
        for (var dy = -radius; dy <= radius; dy++)
        for (var dx = -radius; dx <= radius; dx++)
        {
            var candidate = new SKPoint(dx, dy);
            var error = candidate == SKPoint.Empty ? baseline : Error(candidate);
            integerScores.Add((candidate, error));
            if (error < best) { shift = candidate; best = error; }
        }

        // Refine around the integer minimum, including zero. An already good integer match stays exact.
        if (best > .025)
        {
            foreach (var spacing in new[] { .5f, .25f, .125f })
            {
                var center = shift;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    var candidate = new SKPoint(center.X + dx * spacing, center.Y + dy * spacing);
                    if (Math.Abs(candidate.X) > radius || Math.Abs(candidate.Y) > radius || candidate == center) continue;
                    var error = Error(candidate);
                    if (error + .002 < best) { shift = candidate; best = error; }
                }
            }
        }
        if (shift == SKPoint.Empty || best > .43 || best >= baseline * .76 || baseline - best < .025) return SKPoint.Empty;
        // Reject periodic matches and translations explained by one moved object or one corner.
        if (integerScores.Any(c => Math.Max(Math.Abs(c.Shift.X - shift.X), Math.Abs(c.Shift.Y - shift.Y)) >= 1.5
            && c.Error < best + .055)) return SKPoint.Empty;
        Error(shift, bestRegions);
        var agreeing = Enumerable.Range(0, 4).Count(i => counts[i] >= 6
            && baselineRegions[i] - bestRegions[i] > counts[i] * .025 && bestRegions[i] < counts[i] * .57);
        return agreeing >= requiredRegions ? shift : SKPoint.Empty;
    }

    private static (double X, double Y, bool Valid) Gradient(SKBitmap image, int x, int y)
    {
        var left = image.GetPixel(x - 1, y); var right = image.GetPixel(x + 1, y);
        var top = image.GetPixel(x, y - 1); var bottom = image.GetPixel(x, y + 1);
        var valid = left.Alpha >= 250 && right.Alpha >= 250 && top.Alpha >= 250 && bottom.Alpha >= 250;
        static double Luma(SKColor c) => (54 * c.Red + 183 * c.Green + 19 * c.Blue) / 256.0;
        return (Luma(right) - Luma(left), Luma(bottom) - Luma(top), valid);
    }

    private sealed record Residual(SKRectI Area, int Step, int Width, int Height, double[] Values)
    {
        internal static Residual? Build(SKBitmap generated, SKBitmap reference, SKBitmap mask, SKRectI area, int seam, bool flat, bool editableFill)
        {
            var step = Math.Max(1, (Math.Max(area.Width, area.Height) + 127) / 128);
            if (flat) step = Math.Max(8, step);
            var w = (area.Width + step - 1) / step; var h = (area.Height + step - 1) / step;
            var values = new double[w * h * 3]; var counts = new int[w * h]; long total = 0;
            var flatTone = flat ? FlatSource(reference, mask, area) : SKColors.Transparent;
            var limits = editableFill ? ContextLimits(generated, reference, mask, area) : null;
            var sampleStep = Math.Max(1, step / 4);
            for (var y = area.Top; y < area.Bottom; y += sampleStep) for (var x = area.Left; x < area.Right; x += sampleStep)
            {
                var inside = mask.GetPixel(x, y).Alpha != 0;
                if (inside && !flat) continue;
                var a = reference.GetPixel(x, y); var b = generated.GetPixel(x, y);
                if (inside && flat)
                {
                    // The source inside a removal may contain the old object: use
                    // the verified uniform surrounding tone, never its old pixels.
                    a = flatTone;
                }
                if (a.Alpha != 255 || b.Alpha != 255) continue;
                var cell = ((y - area.Top) / step * w + (x - area.Left) / step) * 3;
                var dr = a.Red - b.Red; var dg = a.Green - b.Green; var db = a.Blue - b.Blue;
                if (limits != null && (dr < limits[0].Min || dr > limits[0].Max || dg < limits[1].Min || dg > limits[1].Max
                    || db < limits[2].Min || db > limits[2].Max)) continue;
                values[cell] += dr; values[cell + 1] += dg; values[cell + 2] += db;
                counts[cell / 3]++; total++;
            }
            if (total == 0) return null;
            for (var i = 0; i < counts.Length; i++) for (var c = 0; c < 3; c++)
                values[i * 3 + c] = counts[i] > 0 ? values[i * 3 + c] / counts[i] : 0;
            // Screen the harmonic residual towards zero away from the boundary so
            // intentional object colors/relighting survive in the interior.
            var falloff = Math.Max(8, seam * 4); var lambda = Math.Pow((double)step / falloff, 2);
            for (var pass = 0; pass < 180; pass++)
            {
                double changed = 0;
                for (var row = 0; row < h; row++) for (var col = 0; col < w; col++)
                {
                    var y = pass % 2 == 0 ? row : h - row - 1; var x = pass % 2 == 0 ? col : w - col - 1; var i = y * w + x;
                    if (counts[i] != 0) continue;
                    var n = (x > 0 ? 1 : 0) + (x + 1 < w ? 1 : 0) + (y > 0 ? 1 : 0) + (y + 1 < h ? 1 : 0);
                    for (var c = 0; c < 3; c++)
                    {
                        var next = ((x > 0 ? values[(i - 1) * 3 + c] : 0) + (x + 1 < w ? values[(i + 1) * 3 + c] : 0)
                            + (y > 0 ? values[(i - w) * 3 + c] : 0) + (y + 1 < h ? values[(i + w) * 3 + c] : 0)) / (n + lambda);
                        changed = Math.Max(changed, Math.Abs(next - values[i * 3 + c])); values[i * 3 + c] = next;
                    }
                }
                if (changed < .01) break;
            }
            return new(area, step, w, h, values);
        }

        // An object can cross the selection. Such pixels in the hidden context are useful
        // layer content, but treating them as a background color cast bleaches its visible edge.
        private static (int Min, int Max)[] ContextLimits(SKBitmap generated, SKBitmap reference, SKBitmap mask, SKRectI area)
        {
            List<int>[] channels = [[], [], []];
            var stride = Math.Max(1, Math.Max(area.Width, area.Height) / 96);
            for (var y = area.Top; y < area.Bottom; y += stride) for (var x = area.Left; x < area.Right; x += stride)
            {
                if (mask.GetPixel(x, y).Alpha != 0) continue;
                var a = reference.GetPixel(x, y); var b = generated.GetPixel(x, y);
                if (a.Alpha != 255 || b.Alpha != 255) continue;
                channels[0].Add(a.Red - b.Red); channels[1].Add(a.Green - b.Green); channels[2].Add(a.Blue - b.Blue);
            }
            return channels.Select(values =>
            {
                if (values.Count < 8) return (-64, 64);
                values.Sort();
                var median = values[values.Count / 2];
                for (var i = 0; i < values.Count; i++) values[i] = Math.Abs(values[i] - median);
                values.Sort();
                var tolerance = Math.Clamp(4 * values[values.Count / 2], 8, 64);
                return (Math.Max(-96, median - tolerance), Math.Min(96, median + tolerance));
            }).ToArray();
        }

        private static SKColor FlatSource(SKBitmap reference, SKBitmap mask, SKRectI area)
        {
            long r = 0, g = 0, b = 0, count = 0;
            var step = Math.Max(1, Math.Max(area.Width, area.Height) / 96);
            for (var y = area.Top; y < area.Bottom; y += step) for (var x = area.Left; x < area.Right; x += step)
            {
                var color = reference.GetPixel(x, y);
                if (mask.GetPixel(x, y).Alpha != 0 || color.Alpha != 255) continue;
                r += color.Red; g += color.Green; b += color.Blue; count++;
            }
            return count == 0 ? SKColors.Transparent : new SKColor((byte)(r / count), (byte)(g / count), (byte)(b / count));
        }

        internal double At(int x, int y, int channel)
        {
            var px = Math.Clamp((x - Area.Left + .5) / Step - .5, 0, Width - 1); var py = Math.Clamp((y - Area.Top + .5) / Step - .5, 0, Height - 1);
            var left = (int)px; var top = (int)py; var right = Math.Min(left + 1, Width - 1); var bottom = Math.Min(top + 1, Height - 1);
            var dx = px - left; var dy = py - top;
            return (Values[(top * Width + left) * 3 + channel] * (1 - dx) + Values[(top * Width + right) * 3 + channel] * dx) * (1 - dy)
                + (Values[(bottom * Width + left) * 3 + channel] * (1 - dx) + Values[(bottom * Width + right) * 3 + channel] * dx) * dy;
        }
    }
}
