namespace Composa.Painting;

public sealed record BrushPreset(string Name, BrushSettings Settings);

public static class BrushPresets
{
    public static IReadOnlyList<BrushPreset> All { get; } = [
        new("Soft round", new BrushSettings { Hardness = 0, Flow = .12, Spacing = .06 }),
        new("Hard round", new BrushSettings { Hardness = 1, Flow = 1, Spacing = .08 }),
        new("Airbrush", new BrushSettings { Hardness = 0, Flow = .04, Spacing = .04, PressureFlow = true }),
        new("Pen", new BrushSettings { Hardness = .95, Flow = 1, Spacing = .04, Smoothing = 15 }),
        new("Soft eraser", new BrushSettings { Hardness = .1, Flow = .15, Spacing = .06 })
    ];
}
