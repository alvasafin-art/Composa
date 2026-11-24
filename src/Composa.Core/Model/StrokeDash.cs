using SkiaSharp;

namespace Composa.Model;

/// <summary>Alternating stroke and gap lengths in layer pixels, or in multiples of the stroke width.</summary>
public sealed record StrokeDash(double[] Intervals, double Offset = 0, bool Relative = false)
{
    internal StrokeDash? Normalized() => Intervals is { Length: >= 2 and <= 64 } && Intervals.Length % 2 == 0
        && Intervals.All(n => double.IsFinite(n) && n is >= 0 and <= DocumentLimits.MaxSide)
        && Intervals.Sum() > 0 && double.IsFinite(Offset) && Math.Abs(Offset) <= DocumentLimits.MaxSide
        ? this : null;

    internal SKPathEffect? CreateEffect(float strokeWidth)
    {
        var factor = Relative ? strokeWidth : 1;
        if (Intervals.Sum() * factor < .25) return null;
        return SKPathEffect.CreateDash(Intervals.Select(n => (float)(n * factor)).ToArray(), (float)Offset);
    }

    public bool Equals(StrokeDash? other) => other != null && Offset == other.Offset && Relative == other.Relative
        && (ReferenceEquals(Intervals, other.Intervals) || Intervals != null && other.Intervals != null && Intervals.SequenceEqual(other.Intervals));

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Offset); hash.Add(Relative);
        if (Intervals != null)
            foreach (var interval in Intervals) hash.Add(interval);
        return hash.ToHashCode();
    }
}
