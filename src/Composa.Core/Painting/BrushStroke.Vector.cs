using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Composa.Painting;

public sealed unsafe partial class BrushStroke
{
    private bool CanVectorizeCoverage(bool buildsUp, float radius) =>
        Avx2.IsSupported && buildsUp && radius > 2 && radius * (1 - hardness) >= 1 && hardness < .995f;

    // The wide soft dab has the same lookup, edge coverage and ties-to-even rounding
    // as CoverageAt and AccumulateBand. Thin antialiased rims stay on their scalar path.
    private bool AccumulateVectors(Span<ushort> row, int left, float centerX, float dy, float radius, float flow, int saturated, out int offset)
    {
        var lane = Vector256.Create(0f, 1, 2, 3, 4, 5, 6, 7);
        var zero = Vector256<float>.Zero;
        var one = Vector256.Create(1f);
        var half = Vector256.Create(.5f);
        var cx = Vector256.Create(centerX);
        var radiusVector = Vector256.Create(radius);
        var radiusSquared = Vector256.Create(radius * radius);
        var edgeStart = Vector256.Create((radius - .5f) * (radius - .5f));
        var edgeEnd = Vector256.Create((radius + .5f) * (radius + .5f));
        var dySquared = Vector256.Create(dy * dy);
        var steps = Vector256.Create((float)FalloffSteps);
        var lastIndex = Vector256.Create(FalloffSteps - 1);
        var flat = Vector256.Create((float)flatFalloffIndex);
        var saturation = Vector256.Create(saturated);
        var flowVector = Vector256.Create(flow);
        var full = Vector256.Create((float)ushort.MaxValue);
        var changed = false;
        ref var start = ref MemoryMarshal.GetReference(row);
        fixed (float* table = falloff)
        for (offset = 0; offset + 8 <= row.Length; offset += 8)
        {
            var previous = Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref start, (nuint)offset));
            if (Avx.MoveMask(Avx2.CompareEqual(Avx2.Min(previous, saturation), saturation).AsSingle()) == 255) continue;
            var dx = Vector256.Create((float)(left + offset)) + lane + half - cx;
            var squared = dx * dx + dySquared;
            var inside = Vector256.LessThan(squared, edgeEnd);
            if (Avx.MoveMask(inside) == 0) continue;

            var position = Avx.Min(steps, squared / radiusSquared * steps);
            var amount = one;
            if (Avx.MoveMask(Vector256.GreaterThan(position, flat)) != 0)
            {
                var index = Avx2.Min(lastIndex, Avx.ConvertToVector256Int32WithTruncation(position));
                var lower = Avx2.GatherVector256(table, index, 4);
                var upper = Avx2.GatherVector256(table + 1, index, 4);
                amount = lower + (upper - lower) * (position - Avx.ConvertToVector256Single(index));
            }
            var inRim = Vector256.GreaterThan(squared, edgeStart);
            if (Avx.MoveMask(inRim) != 0)
            {
                var edge = Avx.Min(one, Avx.Max(zero, radiusVector - Avx.Sqrt(squared) + half));
                amount = Vector256.ConditionalSelect(inRim, amount * edge, amount);
                amount = Avx.And(amount, inside);
            }

            var old = Avx.ConvertToVector256Single(previous);
            var next = Avx.ConvertToVector256Int32(Avx.RoundToNearestInteger(old + (full - old) * amount * flowVector));
            if (Avx.MoveMask(Avx2.CompareEqual(previous, next).AsSingle()) == 255) continue;

            Sse41.PackUnsignedSaturate(next.GetLower(), next.GetUpper()).StoreUnsafe(ref start, (nuint)offset);
            changed = true;
        }
        return changed;
    }
}