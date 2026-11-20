using Composa.Rendering;
using SkiaSharp;

namespace Composa.Vision;

/// <summary>Resolve a box over printed lettering when the semantic mask joins the strokes or selects their backing.</summary>
internal static class CaptionSelection
{
    private readonly record struct Band(int Top, int Bottom, int Left, int Right);

    private readonly record struct InkRange(bool Bright, int Threshold)
    {
        public bool Contains(int level) => Bright ? level > Threshold : level < Threshold;
    }

    private static byte Luma(PixelValue p) => (byte)Math.Clamp((int)MathF.Round(255 * (.299f * p.R + .587f * p.G + .114f * p.B)), 0, 255);

    internal static SKBitmap? Recover(SKBitmap image, SKBitmap matte, SKRectI box, CancellationToken cancel, bool joinedOnly = false)
    {
        box = SKRectI.Intersect(box, SKRectI.Create(image.Width, image.Height));
        if (box.Height < 10 || box.Width < 32 || box.Width < 1.55 * box.Height) return null;
        // A prediction contained in the gesture can still be repaired by normal matting.
        SKRectI? subjectBounds = joinedOnly ? Selections.SelectionMask.Bounds(matte, 128) : null;
        if (subjectBounds is { } initial && box.Contains(initial)) return null;
        var rgb = new PixelBuffer(image);
        int step = Math.Max(1, (box.Width + 1199) / 1200);
        int w = (box.Width + step - 1) / step, h = (box.Height + step - 1) / step;
        var levels = new byte[w * h]; var histogram = new int[256];
        for (int y = 0; y < h; y++)
        {
            cancel.ThrowIfCancellationRequested();
            for (int x = 0; x < w; x++)
            {
                var p = rgb.Read(box.Left + x * step, box.Top + y * step);
                if (p.A < .98f) return null;
                byte v = Luma(p); levels[y * w + x] = v; histogram[v]++;
            }
        }
        int Quantile(double fraction)
        {
            int count = 0;
            for (int i = 0; i < 255; i++) if ((count += histogram[i]) >= fraction * levels.Length) return i;
            return 255;
        }
        int bg = Quantile(.50);
        foreach (int ink in new[] { Quantile(.97), Quantile(.03) })
        {
            if (Math.Abs(ink - bg) < 50) continue;
            bool bright = ink > bg;
            int split = (int)Math.Round(bg + .62 * (ink - bg));
            var range = new InkRange(bright, split);
            var (grid, shift, gridHeight) = Align(levels, w, h, range);
            if (gridHeight == 0) continue;
            var bands = FindBands(grid, w, h, gridHeight, cancel);
            if (bands.Count == 0 || bands.Max(l => l.Right - l.Left) < .60 * w) continue;
            // Include dots, accents and descenders omitted from the dense part of a line.
            var rows = new (int Left, int Right)[gridHeight];
            foreach (var line in bands)
            {
                int pad = Math.Max(3, (line.Bottom - line.Top) / 2);
                for (int y = Math.Max(0, line.Top - pad); y < Math.Min(gridHeight, line.Bottom + pad); y++)
                {
                    int left = Math.Max(0, line.Left - pad), right = Math.Min(w, line.Right + pad);
                    if (rows[y].Right > 0) { left = Math.Min(left, rows[y].Left); right = Math.Max(right, rows[y].Right); }
                    rows[y] = (left, right);
                }
            }
            int supported = 0, other = 0, intersection = 0, union = 0;
            int firstRow = h, lastRow = 0;
            var old = matte.GetPixelSpan();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    bool mark = range.Contains(levels[y * w + x]);
                    var row = rows[y + shift[x]];
                    bool letter = mark && x >= row.Left && x < row.Right;
                    if (letter) { supported++; firstRow = Math.Min(firstRow, y); lastRow = y; }
                    else if (mark) other++;
                    bool selected = old[(box.Top + y * step) * matte.RowBytes + box.Left + x * step] >= 128;
                    if (letter || selected) union++;
                    if (letter && selected) intersection++;
                }
            if (lastRow - firstRow + 1 < .34 * h || supported < 24 || other > .10 * supported || intersection >= .86 * union) continue;
            if (joinedOnly && (intersection < .80 * supported || union < 2.3 * supported)) continue;
            // A surrounding box can deliberately describe the complete printed sign.
            // Keep that object when it has a visible margin on every side of the ink.
            var subject = subjectBounds ?? Selections.SelectionMask.Bounds(matte, 128);
            int inset = Math.Max(3, bands.Max(l => l.Bottom - l.Top) * step / 3);
            var lettering = new SKRectI(box.Left + bands.Min(l => l.Left) * step - inset,
                box.Top + firstRow * step - inset, box.Left + bands.Max(l => l.Right) * step + inset,
                box.Top + (lastRow + 1) * step + inset);
            if (box.Contains(subject) && subject.Contains(lettering)) continue;
            if (!subject.IsEmpty && intersection < .40 * supported) continue;
            return Rasterize(rgb, box, step, shift, rows, range, cancel);
        }
        return null;
    }

    private static (bool[] Grid, int[] Shift, int Height) Align(byte[] levels, int w, int h, InkRange range)
    {
        // Measure alignment in a sheared grid for an inclined line. The final
        // alpha is still sampled at the native pixel, with no resampling of RGB.
        double sx = 0, sy = 0, xx = 0, xy = 0, yy = 0;
        int n = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!range.Contains(levels[y * w + x])) continue;
                n++; sx += x; sy += y; xx += x * x; xy += x * y; yy += y * y;
            }
        if (n < 24) return ([], [], 0);
        double cov = xy - sx * sy / n, varX = xx - sx * sx / n, varY = yy - sy * sy / n;
        double slope = varX > 0 ? cov / varX : 0;
        if (Math.Abs(slope) < .09 || Math.Abs(slope) > .58 || cov * cov < .50 * varX * varY) slope = 0;
        var shift = new int[w]; int rise = (int)Math.Round(slope * (w - 1));
        int gridHeight = h + Math.Abs(rise);
        var grid = new bool[gridHeight * w];
        for (int x = 0; x < w; x++)
        {
            shift[x] = Math.Max(0, rise) - (int)Math.Round(x * slope);
            for (int y = 0; y < h; y++) grid[(y + shift[x]) * w + x] = range.Contains(levels[y * w + x]);
        }
        return (grid, shift, gridHeight);
    }

    private static List<Band> FindBands(bool[] grid, int w, int h, int gridHeight, CancellationToken cancel)
    {
        var bands = new List<Band>();
        var columns = new int[w];
        int top = -1;
        for (int y = 0; y <= gridHeight; y++)
        {
            cancel.ThrowIfCancellationRequested();
            int count = 0;
            if (y < gridHeight) for (int x = 0; x < w; x++) if (grid[y * w + x]) count++;
            if (count > Math.Max(3, .018 * w))
            {
                if (top < 0) { top = y; Array.Clear(columns); }
                for (int x = 0; x < w; x++) if (grid[y * w + x]) columns[x]++;
                continue;
            }
            if (top < 0) continue;
            int height = y - top;
            if (height >= 5 && height <= .85 * h && top > 0 && y < gridHeight)
            {
                int letters = 0, narrow = w, wide = 0, left = w, right = 0, strokes = 0;
                for (int x = 0; x < w;)
                {
                    if (columns[x] == 0) { x++; continue; }
                    int start = x, tallest = 0;
                    while (x < w && columns[x] > 0) { tallest = Math.Max(tallest, columns[x]); strokes += columns[x++]; }
                    if (tallest < .35 * height || x - start > 2.5 * height) continue;
                    letters++; narrow = Math.Min(narrow, x - start); wide = Math.Max(wide, x - start);
                    left = Math.Min(left, start); right = x;
                }
                // Repeated identical slots or printed dots are a different gesture.
                if (letters >= 4 && wide >= 1.22 * narrow && right - left >= 2.6 * height &&
                    strokes < .76 * height * (right - left))
                    bands.Add(new(top, y, left, right));
            }
            top = -1;
        }
        return bands;
    }

    private static SKBitmap Rasterize(PixelBuffer rgb, SKRectI box, int step, int[] shift,
        (int Left, int Right)[] rows, InkRange range, CancellationToken cancel)
    {
        var result = Pixels.NewMask(rgb.Width, rgb.Height);
        try
        {
            var output = result.GetPixelSpan();
            for (int y = box.Top; y < box.Bottom; y++)
            {
                cancel.ThrowIfCancellationRequested();
                for (int x = box.Left; x < box.Right; x++)
                {
                    int gx = (x - box.Left) / step;
                    var row = rows[(y - box.Top) / step + shift[gx]];
                    if (gx < row.Left || gx >= row.Right) continue;
                    int v = Luma(rgb.Read(x, y)), low = v, high = v;
                    for (int ny = Math.Max(box.Top, y - 2); ny <= Math.Min(box.Bottom - 1, y + 2); ny++)
                        for (int nx = Math.Max(box.Left, x - 2); nx <= Math.Min(box.Right - 1, x + 2); nx++)
                        {
                            int neighbor = Luma(rgb.Read(nx, ny));
                            low = Math.Min(low, neighbor); high = Math.Max(high, neighbor);
                        }
                    int localInk = range.Bright ? high : low, localBg = range.Bright ? low : high;
                    if (!range.Contains(localInk)) continue;
                    float alpha = range.Contains(localBg) ? 1 : Math.Clamp((v - localBg) / (float)(localInk - localBg), 0, 1);
                    if (alpha > 0 && alpha < .49f)
                    {
                        // A single dark or bright grain on the photograph can look like
                        // a faint halo. Estimate the backdrop from nearby samples of
                        // the same tone before accepting partial coverage.
                        float total = 0, weight = 0;
                        for (int ny = Math.Max(box.Top, y - 2); ny <= Math.Min(box.Bottom - 1, y + 2); ny++)
                            for (int nx = Math.Max(box.Left, x - 2); nx <= Math.Min(box.Right - 1, x + 2); nx++)
                            {
                                int neighbor = Luma(rgb.Read(nx, ny));
                                if (Math.Abs(neighbor - localBg) > .09f * (high - low)) continue;
                                int dx = nx - x, dy = ny - y;
                                float w = 1f / (1 + dx * dx + dy * dy);
                                total += w * neighbor; weight += w;
                            }
                        float background = total / weight;
                        float fitted = Math.Clamp((v - background) / (localInk - background), 0, alpha);
                        alpha += Math.Min(1, (.49f - alpha) / .27f) * (fitted - alpha);
                    }
                    output[y * result.RowBytes + x] = (byte)Math.Round(255 * alpha);
                }
            }
            Pixels.Invalidate(result);
            return result;
        }
        catch { result.Dispose(); throw; }
    }
}
