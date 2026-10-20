using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Painting;

public sealed unsafe partial class BrushStroke
{
    private static PixelValue WithAlpha(PixelValue value, float alpha) => (value.Straight with { A = alpha }).Premultiplied;

    private void RecomputeHighPrecision(SKRectI rect)
    {
        if (mode == BrushMode.Blur && blurred == null) blurred = MakeBlurred();
        var input = new PixelBuffer(original); var output = new PixelBuffer(Working);
        PixelBuffer? donor = CloneSource == null ? null : new PixelBuffer(CloneSource);
        PixelBuffer? soft = blurred == null ? null : new PixelBuffer(blurred);
        var opacity = (float)Math.Clamp(settings.Opacity, 0, 1);
        var color = new PixelValue(premulColor[0] / 255, premulColor[1] / 255, premulColor[2] / 255, premulColor[3] / 255);
        for (var y = rect.Top; y < rect.Bottom; y++)
        for (var x = rect.Left; x < rect.Right; x++)
        {
            if (coverage[x, y] == 0) continue;
            var amount = coverage[x, y] / (float)ushort.MaxValue * opacity * SelectionAt(x, y);
            var before = input.Read(x, y);
            if (LockTransparency && mode == BrushMode.Erase) amount = 0;
            if (amount <= 0) { output.Write(x, y, before); continue; }
            PixelValue after;
            switch (mode)
            {
                case BrushMode.Paint: after = color; break;
                case BrushMode.Erase: after = default; break;
                case BrushMode.Heal: after = new(0, 0, 0, 1); amount *= .45f; break;
                case BrushMode.Clone:
                case BrushMode.Healing:
                    int cx = x + CloneOffset.X, cy = y + CloneOffset.Y;
                    if (donor is not { } sample || cx < 0 || cy < 0 || cx >= sample.Width || cy >= sample.Height) { after = default; amount = 0; }
                    else after = sample.Read(cx, cy);
                    break;
                case BrushMode.Blur: after = soft!.Value.Read(x, y); break;
                case BrushMode.Dodge: after = new(before.R + (before.A - before.R) * .5f, before.G + (before.A - before.G) * .5f, before.B + (before.A - before.B) * .5f, before.A); break;
                case BrushMode.Burn: after = new(before.R * .5f, before.G * .5f, before.B * .5f, before.A); break;
                default: after = before; break;
            }
            var result = before * (1 - amount) + after * amount;
            output.Write(x, y, LockTransparency ? WithAlpha(result, before.A) : result);
        }
    }

    private bool SmudgeHighPrecision(SKPoint center, SKRectI rect)
    {
        var size = (int)MathF.Ceiling(fullRadius * 2 + 3);
        var pixels = new PixelBuffer(Working); var source = new PixelBuffer(original);
        int originX = (int)MathF.Floor(center.X - radius - 1), originY = (int)MathF.Floor(center.Y - radius - 1);
        if (carry == null)
        {
            carrySize = size;
            carry = new float[size * size * 4];
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var p = pixels.Read(Math.Clamp(originX + x, 0, width - 1), Math.Clamp(originY + y, 0, height - 1));
                var at = (y * size + x) * 4;
                carry[at] = p.R; carry[at + 1] = p.G; carry[at + 2] = p.B; carry[at + 3] = p.A;
            }
            return false;
        }
        var strength = (float)Math.Clamp(settings.Opacity, 0, 1) * .95f;
        for (var y = rect.Top; y < rect.Bottom; y++)
        for (var x = rect.Left; x < rect.Right; x++)
        {
            int lx = x - originX, ly = y - originY;
            if (lx < 0 || ly < 0 || lx >= carrySize || ly >= carrySize) continue;
            float dx = x + .5f - center.X, dy = y + .5f - center.Y;
            var amount = Falloff(MathF.Sqrt(dx * dx + dy * dy)) * SelectionAt(x, y);
            var at = (ly * carrySize + lx) * 4;
            var under = pixels.Read(x, y);
            var held = new PixelValue(carry[at], carry[at + 1], carry[at + 2], carry[at + 3]) * strength + under * (1 - strength);
            carry[at] = held.R; carry[at + 1] = held.G; carry[at + 2] = held.B; carry[at + 3] = held.A;
            if (amount <= 0) continue;
            var result = under * (1 - amount) + held * amount;
            pixels.Write(x, y, LockTransparency ? WithAlpha(result, source.Read(x, y).A) : result);
        }
        return true;
    }

    private SKRectI PushHighPrecision(SKPoint center, SKRectI rect)
    {
        var previous = lastDab;
        lastDab = center;
        if (previous is not { } from) return SKRectI.Empty;
        float dx = center.X - from.X, dy = center.Y - from.Y;
        if (dx == 0 && dy == 0) return SKRectI.Empty;
        var reach = (int)MathF.Ceiling(MathF.Max(MathF.Abs(dx), MathF.Abs(dy))) + 2;
        var area = Geometry.Intersect(new(rect.Left - reach, rect.Top - reach, rect.Right + reach, rect.Bottom + reach), new(0, 0, width, height));
        var snapshot = new PixelValue[area.Width * area.Height];
        var pixels = new PixelBuffer(Working); var source = new PixelBuffer(original);
        for (var y = 0; y < area.Height; y++)
        for (var x = 0; x < area.Width; x++) snapshot[y * area.Width + x] = pixels.Read(x + area.Left, y + area.Top);
        var strength = (float)Math.Clamp(settings.Opacity, 0, 1);
        PixelValue At(int x, int y) => snapshot[Math.Clamp(y, 0, area.Height - 1) * area.Width + Math.Clamp(x, 0, area.Width - 1)];
        for (var y = rect.Top; y < rect.Bottom; y++)
        for (var x = rect.Left; x < rect.Right; x++)
        {
            float px = x + .5f - center.X, py = y + .5f - center.Y;
            var weight = Falloff(MathF.Sqrt(px * px + py * py)) * strength * SelectionAt(x, y);
            if (weight <= 0) continue;
            float sx = x - dx * weight - area.Left, sy = y - dy * weight - area.Top;
            int x0 = (int)MathF.Floor(sx), y0 = (int)MathF.Floor(sy);
            float tx = sx - x0, ty = sy - y0;
            var result = (At(x0, y0) * (1 - tx) + At(x0 + 1, y0) * tx) * (1 - ty) + (At(x0, y0 + 1) * (1 - tx) + At(x0 + 1, y0 + 1) * tx) * ty;
            pixels.Write(x, y, LockTransparency ? WithAlpha(result, source.Read(x, y).A) : result);
        }
        return rect;
    }
}
