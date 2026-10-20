namespace Composa.Model;

/// <summary>Split black and white handles in the 0–255 display range. Equal handles make a hard cutoff.</summary>
public sealed record BlendRange(double Black = 0, double BlackEnd = 0, double WhiteStart = 255, double White = 255)
{
    [System.Text.Json.Serialization.JsonIgnore] public bool IsIdentity => Black == 0 && BlackEnd == 0 && WhiteStart == 255 && White == 255;
    public BlendRange Normalized()
    {
        static double Clamp(double value, double fallback) => double.IsFinite(value) ? Math.Clamp(value, 0, 255) : fallback;
        var black = Clamp(Black, 0);
        var blackEnd = Math.Max(black, Clamp(BlackEnd, 0));
        var whiteStart = Math.Max(blackEnd, Clamp(WhiteStart, 255));
        return new(black, blackEnd, whiteStart, Math.Max(whiteStart, Clamp(White, 255)));
    }

    public double Coverage(double value)
    {
        if (value < Black || value > White) return 0;
        if (value < BlackEnd) return (value - Black) / (BlackEnd - Black);
        if (value > WhiteStart) return (White - value) / (White - WhiteStart);
        return 1;
    }
}

public sealed record BlendChannelRanges
{
    public BlendRange ThisLayer { get; init; } = new();
    public BlendRange Underlying { get; init; } = new();
    public BlendChannelRanges Normalized() => new() { ThisLayer = (ThisLayer ?? new()).Normalized(), Underlying = (Underlying ?? new()).Normalized() };
    public bool IsIdentity => ThisLayer.IsIdentity && Underlying.IsIdentity;
}

/// <summary>Independent gray and RGB intersections, evaluated from straight colors before layer compositing.</summary>
public sealed record BlendIfSettings
{
    public BlendChannelRanges Gray { get; init; } = new();
    public BlendChannelRanges Red { get; init; } = new();
    public BlendChannelRanges Green { get; init; } = new();
    public BlendChannelRanges Blue { get; init; } = new();
    public bool IsIdentity => Gray.IsIdentity && Red.IsIdentity && Green.IsIdentity && Blue.IsIdentity;
    public BlendIfSettings Normalized() => new()
    {
        Gray = (Gray ?? new()).Normalized(), Red = (Red ?? new()).Normalized(),
        Green = (Green ?? new()).Normalized(), Blue = (Blue ?? new()).Normalized()
    };
}
