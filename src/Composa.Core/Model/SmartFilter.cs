using Composa.Filters;
namespace Composa.Model;
public sealed record SmartFilter(Guid Id, FilterSettings Settings, bool Enabled = true)
{
    public double Opacity { get; init; } = 1;
    public BlendMode Blend { get; init; } = BlendMode.Normal;
    public SmartFilter Normalized() => this with { Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0, 1) : 1,
        Blend = Enum.IsDefined(Blend) && Blend != BlendMode.PassThrough ? Blend : BlendMode.Normal };
}
