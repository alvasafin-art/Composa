namespace Composa.Filters;

/// <summary>A clockwise hue interval, in degrees. Start + FadeIn begins full influence; Width is the full
/// influence interval, followed by FadeOut. The representation crosses red without a discontinuity.</summary>
public readonly record struct HueBand(double Start, double FadeIn, double Width, double FadeOut)
{
    public static double Wrap(double hue) => double.IsFinite(hue) ? (hue % 360 + 360) % 360 : 0;
    public HueBand Normalized()
    {
        static double Safe(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 360) : 0;
        var incoming = Safe(FadeIn); var width = Math.Min(Safe(Width), 360 - incoming);
        return new(Wrap(Start), incoming, width, Math.Min(Safe(FadeOut), 360 - incoming - width));
    }
    public static HueBand Default(HueRange range)
    {
        if (range is < HueRange.Reds or > HueRange.Magentas) throw new ArgumentOutOfRangeException(nameof(range));
        return new(Wrap(((int)range - 1) * 60 - 45), 30, 30, 30);
    }
    public double Weight(double hue)
    {
        var position = Wrap(hue - Start); var end = FadeIn + Width + FadeOut;
        if (position > end) return 0;
        if (position < FadeIn) return FadeIn == 0 ? 1 : position / FadeIn;
        if (position <= FadeIn + Width) return 1;
        return FadeOut == 0 ? 0 : (end - position) / FadeOut;
    }
    public static HueBand FromEdges(double start, double fullStart, double fullEnd, double end)
    {
        if (!double.IsFinite(start) || !double.IsFinite(fullStart) || !double.IsFinite(fullEnd) || !double.IsFinite(end)) throw new ArgumentException("Hue boundaries must be finite.");
        static double Span(double from, double to) => to - from == 360 ? 360 : Wrap(to - from);
        var incoming = Span(start, fullStart); var width = Span(fullStart, fullEnd); var outgoing = Span(fullEnd, end);
        if (incoming + width + outgoing > 360 + 1e-8) throw new ArgumentException("Hue boundaries must form one clockwise interval.");
        return new HueBand(Wrap(start), incoming, width, outgoing).Normalized();
    }
}
