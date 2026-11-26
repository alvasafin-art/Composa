namespace Composa.Model;

public enum StrokeCap { Butt, Round, Square }
public enum StrokeJoin { Miter, Round, Bevel }
public enum StrokePosition { Center, Inside, Outside }
public enum StrokeProfile { Uniform, TaperStart, TaperEnd, TaperBoth }
public enum StrokeMarker { None, Arrow, SlimArrow, Triangle, Circle, Square, Diamond, HollowTriangle, HollowCircle, HollowSquare, HollowDiamond }

/// <summary>Additional live stroke settings. An unspecified cap preserves the source shape's legacy cap.</summary>
public sealed record VectorStroke
{
    public StrokeCap? Cap { get; init; }
    public StrokeJoin Join { get; init; }
    public StrokePosition Position { get; init; }
    public StrokeProfile Profile { get; init; }
    public StrokeMarker Start { get; init; }
    public StrokeMarker End { get; init; }
    public double MiterAngle { get; init; } = 29;

    internal VectorStroke Normalized() => this with
    {
        Cap = Cap is { } cap && Enum.IsDefined(cap) ? cap : null,
        Join = Enum.IsDefined(Join) ? Join : StrokeJoin.Miter,
        Position = Enum.IsDefined(Position) ? Position : StrokePosition.Center,
        Profile = Enum.IsDefined(Profile) ? Profile : StrokeProfile.Uniform,
        Start = Enum.IsDefined(Start) ? Start : StrokeMarker.None,
        End = Enum.IsDefined(End) ? End : StrokeMarker.None,
        MiterAngle = double.IsFinite(MiterAngle) ? Math.Clamp(MiterAngle, 1, 180) : 29
    };
}
