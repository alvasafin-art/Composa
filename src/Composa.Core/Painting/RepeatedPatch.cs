using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Painting;

/// <summary>Detect a repeated row or column from intact surrounding marks, including marks beyond the immediate rim.</summary>
internal static unsafe class RepeatedPatch
{
    public static SKPointI? Find(SKBitmap source, SKBitmap exclusion, SKRectI hole, CancellationToken cancellation)
    {
        if (hole.Width > 512 || hole.Height > 512) return null;
        var colors = new PixelBuffer(source);
        var mask = (byte*)exclusion.GetPixels();
        var stride = exclusion.RowBytes;
        var context = hole;
        context.Inflate(24, 24);
        context = Geometry.Intersect(context, new SKRectI(0, 0, colors.Width, colors.Height));
        var samples = new List<(int X, int Y, PixelValue Color)>();
        static float Luma(PixelValue p) => .2126f * p.R + .7152f * p.G + .0722f * p.B;
        var step = Math.Max(2, (int)Math.Sqrt(context.Width * context.Height / 800.0));
        double sum = 0, squares = 0;
        for (var y = context.Top; y < context.Bottom; y += step)
        for (var x = context.Left; x < context.Right; x += step)
        {
            if (mask[(long)y * stride + x] != 0) continue;
            var color = colors.Read(x, y);
            if (color.A < .99f) continue;
            var lum = Luma(color);
            samples.Add((x, y, color));
            sum += lum; squares += lum * lum;
        }
        if (samples.Count < 32) return null;
        var variance = (squares - sum * sum / samples.Count) / samples.Count;
        if (variance < .008) return null;

        SKPointI? winner = null;
        // Require the displaced context to explain at least 92% of its luminance variance.
        var best = .08 * variance;
        void Consider(int dx, int dy)
        {
            if (hole.Left + dx < 0 || hole.Right + dx > colors.Width || hole.Top + dy < 0 || hole.Bottom + dy > colors.Height) return;
            double error = 0;
            int count = 0;
            foreach (var (x, y, target) in samples)
            {
                int sx = x + dx, sy = y + dy;
                if ((uint)sx >= colors.Width || (uint)sy >= colors.Height || mask[(long)sy * stride + sx] != 0) continue;
                var donor = colors.Read(sx, sy);
                if (donor.A < .99f) return;
                float dr = donor.R - target.R, dg = donor.G - target.G, db = donor.B - target.B;
                error += .2126f * dr * dr + .7152f * dg * dg + .0722f * db * db;
                if (error >= best * samples.Count) return;
                count++;
            }
            if (count < samples.Count * .7 || error / count >= best) return;
            for (var y = hole.Top + dy; y < hole.Bottom + dy; y++)
            for (var x = hole.Left + dx; x < hole.Right + dx; x++)
                if (mask[(long)y * stride + x] != 0 || colors.Read(x, y).A < .99f) return;
            best = error / count; winner = new SKPointI(dx, dy);
        }
        for (var distance = 1; distance <= 384; distance++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (distance >= hole.Width) { Consider(distance, 0); Consider(-distance, 0); }
            if (distance >= hole.Height) { Consider(0, distance); Consider(0, -distance); }
        }
        return winner;
    }
}
