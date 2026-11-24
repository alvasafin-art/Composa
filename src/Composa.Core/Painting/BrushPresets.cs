namespace Composa.Painting;

public sealed record BrushPreset(string Name, BrushSettings Settings);

public static class BrushPresets
{
    public static IReadOnlyList<BrushPreset> All { get; } = [
        new("Soft", new BrushSettings { Hardness = 0 }),
        new("Hard", new BrushSettings { Hardness = 1 })
    ];
}
