using Composa.Rendering;
using SkiaSharp;

namespace Composa.Selections;

public static partial class MagicWand
{
    private static SKBitmap SelectHighPrecision(SKBitmap source, int seedX, int seedY, int tolerance, bool contiguous, bool smooth)
    {
        var result = Pixels.NewMask(source.Width, source.Height);
        if (seedX < 0 || seedY < 0 || seedX >= source.Width || seedY >= source.Height) return result;
        var pixels = new PixelBuffer(source);
        var seed = pixels.Read(seedX, seedY); var limit = Math.Clamp(tolerance, 0, 255) / 255f;
        bool Matches(int x, int y)
        {
            var p = pixels.Read(x, y);
            return Math.Abs(p.R - seed.R) <= limit && Math.Abs(p.G - seed.G) <= limit && Math.Abs(p.B - seed.B) <= limit && Math.Abs(p.A - seed.A) <= limit;
        }
        if (!contiguous)
        {
            var selected = result.GetPixelSpan();
            for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++) if (Matches(x, y)) selected[y * result.RowBytes + x] = 255;
        }
        else
        {
            var stack = new Stack<(int X, int Y)>(); stack.Push((seedX, seedY));
            var selected = result.GetPixelSpan();
            while (stack.Count > 0)
            {
                var (x, y) = stack.Pop();
                if (selected[y * result.RowBytes + x] != 0 || !Matches(x, y)) continue;
                int left = x, right = x;
                while (left > 0 && selected[y * result.RowBytes + left - 1] == 0 && Matches(left - 1, y)) left--;
                while (right < source.Width - 1 && selected[y * result.RowBytes + right + 1] == 0 && Matches(right + 1, y)) right++;
                for (var i = left; i <= right; i++) selected[y * result.RowBytes + i] = 255;
                for (var ny = y - 1; ny <= y + 1; ny += 2)
                {
                    if (ny < 0 || ny >= source.Height) continue;
                    var inRun = false;
                    for (var i = left; i <= right; i++)
                    {
                        var open = selected[ny * result.RowBytes + i] == 0 && Matches(i, ny);
                        if (open && !inRun) stack.Push((i, ny));
                        inRun = open;
                    }
                }
            }
        }
        Pixels.Invalidate(result);
        if (!smooth) return result;
        using (result) return SelectionMask.Feather(result, 1f);
    }
}
