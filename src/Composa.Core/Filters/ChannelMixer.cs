using System.Text.Json.Serialization;

namespace Composa.Filters;

public readonly record struct ChannelMix(double Red, double Green, double Blue, double Constant = 0)
{
    public const double Min = -200, Max = 200;
    [JsonIgnore] public double Total => Red + Green + Blue;
    public ChannelMix Normalized()
    {
        static double Safe(double v) => double.IsFinite(v) ? Math.Clamp(v, Min, Max) : 0;
        return new(Safe(Red), Safe(Green), Safe(Blue), Safe(Constant));
    }
    internal double Map(double r, double g, double b) => Math.Clamp((Red * r + Green * g + Blue * b + Constant) / 100, 0, 1);
}

/// <summary>Independent output rows sampled simultaneously from the original straight RGB channels.</summary>
public sealed record ChannelMixerAdjustment : Adjustment
{
    public ChannelMix Red { get; init; } = new(100, 0, 0);
    public ChannelMix Green { get; init; } = new(0, 100, 0);
    public ChannelMix Blue { get; init; } = new(0, 0, 100);
    public ChannelMix Gray { get; init; } = new(40, 40, 20);
    public bool Monochrome { get; init; }
    public override AdjustmentKind Kind => AdjustmentKind.ChannelMixer;
    public override string DisplayName => "Channel Mixer";
    public override bool IsIdentity => !Monochrome && Red.Normalized() == new ChannelMix(100, 0, 0) && Green.Normalized() == new ChannelMix(0, 100, 0) && Blue.Normalized() == new ChannelMix(0, 0, 100);
    public ChannelMix Row(int output) => output switch { 0 => Red, 1 => Green, 2 => Blue, 3 => Gray, _ => throw new ArgumentOutOfRangeException(nameof(output)) };
    public ChannelMixerAdjustment WithRow(int output, ChannelMix row) => output switch
    { 0 => this with { Red = row.Normalized() }, 1 => this with { Green = row.Normalized() }, 2 => this with { Blue = row.Normalized() }, 3 => this with { Gray = row.Normalized() }, _ => throw new ArgumentOutOfRangeException(nameof(output)) };
    public static IReadOnlyDictionary<string, ChannelMixerAdjustment> Presets { get; } = new System.Collections.ObjectModel.ReadOnlyDictionary<string, ChannelMixerAdjustment>(new Dictionary<string, ChannelMixerAdjustment>
    {
        ["Identity"] = new(), ["Swap Red / Blue"] = new() { Red = new(0, 0, 100), Blue = new(100, 0, 0) },
        ["Monochrome 40 / 40 / 20"] = new() { Monochrome = true },
        ["Monochrome red filter"] = new() { Monochrome = true, Gray = new(100, 0, 0) },
        ["Monochrome green filter"] = new() { Monochrome = true, Gray = new(0, 100, 0) },
        ["Monochrome blue filter"] = new() { Monochrome = true, Gray = new(0, 0, 100) }
    });
    internal FloatPixelOp CreateColorOp()
    {
        var red = Red.Normalized(); var green = Green.Normalized(); var blue = Blue.Normalized(); var gray = Gray.Normalized(); var mono = Monochrome;
        return (ref double r, ref double g, ref double b, int _, int _) =>
        {
            if (mono) { r = g = b = gray.Map(r, g, b); return; }
            var nr = red.Map(r, g, b); var ng = green.Map(r, g, b); var nb = blue.Map(r, g, b); r = nr; g = ng; b = nb;
        };
    }
    internal override PixelOp CreateOp()
    {
        var transform = CreateColorOp();
        return (ref int r, ref int g, ref int b, int _, int _) =>
        { double nr = r / 255.0, ng = g / 255.0, nb = b / 255.0; transform(ref nr, ref ng, ref nb, 0, 0); r = Lut.ToByte(nr); g = Lut.ToByte(ng); b = Lut.ToByte(nb); };
    }
}
