using System.Text.Json.Serialization;

namespace Composa.AI;

[JsonConverter(typeof(JsonStringEnumConverter<SceneMatchMode>))]
public enum SceneMatchMode { LightAndShadows, LightOnly, ToneAndColor }
[JsonConverter(typeof(JsonStringEnumConverter<RelightPreset>))]
public enum RelightPreset { MatchScene, SoftboxLeft, SoftboxRight, WindowLight, Overcast, GoldenHour, Backlight, StudioProduct, Neon }
[JsonConverter(typeof(JsonStringEnumConverter<RelightScope>))]
public enum RelightScope { Subject, WholeImage }

/// <summary>Scene editing is independent of legacy inpaint edge controls.</summary>
public sealed record SceneEditSettings
{
    public SceneMatchMode MatchMode { get; init; } = SceneMatchMode.LightAndShadows;
    public RelightPreset Preset { get; init; } = RelightPreset.MatchScene;
    public RelightScope Scope { get; init; } = RelightScope.Subject;
    public double Denoise { get; init; } = .8;
    public double Strength { get; init; } = 1;
    public double ShadowReach { get; init; } = 1;
    public bool PreserveDetails { get; init; } = true;
    public bool AddShadows { get; init; } = true;
    public double? LightAngle { get; init; }

    public SceneEditSettings Normalized() => this with
    {
        MatchMode = Enum.IsDefined(MatchMode) ? MatchMode : SceneMatchMode.LightAndShadows,
        Preset = Enum.IsDefined(Preset) ? Preset : RelightPreset.MatchScene,
        Scope = Enum.IsDefined(Scope) ? Scope : RelightScope.Subject,
        LightAngle = LightAngle is { } angle && double.IsFinite(angle) ? ((angle%360)+360)%360 : null,
        Denoise = Math.Clamp(double.IsFinite(Denoise) ? Denoise : .8, .1, 1),
        Strength = Math.Clamp(double.IsFinite(Strength) ? Strength : 1, 0, 1),
        ShadowReach = Math.Clamp(double.IsFinite(ShadowReach) ? ShadowReach : 1, .1, 2)
    };

    public static string Name(RelightPreset preset) => preset switch
    {
        RelightPreset.MatchScene => "Match surrounding light", RelightPreset.SoftboxLeft => "Soft studio · left",
        RelightPreset.SoftboxRight => "Soft studio · right", RelightPreset.WindowLight => "Window light",
        RelightPreset.Overcast => "Overcast daylight", RelightPreset.GoldenHour => "Golden hour",
        RelightPreset.Backlight => "Backlight / rim", RelightPreset.StudioProduct => "Product studio",
        RelightPreset.Neon => "Blue / magenta neon", _ => preset.ToString()
    };

    public string LightingInstruction => Preset switch
    {
        RelightPreset.SoftboxLeft => "Replace the subject lighting with a large softbox from upper camera-left, neutral daylight, gentle fill on the right, soft shadows and natural skin tones.",
        RelightPreset.SoftboxRight => "Replace the subject lighting with a large softbox from upper camera-right, neutral daylight, gentle fill on the left, soft shadows and natural skin tones.",
        RelightPreset.WindowLight => "Soft natural window light from the left, gradual light falloff, gentle directional shadows, neutral highlights. No window or new objects should be added.",
        RelightPreset.Overcast => "Diffuse overcast daylight, soft wraparound illumination, natural colors, subdued highlights and very soft contact shadows.",
        RelightPreset.GoldenHour => "Warm low golden-hour sunlight from upper left, cooler ambient fill, warm highlights and coherent elongated soft shadows. Preserve photographic materials.",
        RelightPreset.Backlight => "A light behind the subject creates a clean natural rim on its silhouette, with subtle neutral frontal fill. Avoid halos, glow, fog and blown highlights.",
        RelightPreset.StudioProduct => "Clean product photography with a large neutral overhead softbox, controlled side highlights, faithful material and label colors, and a grounded soft contact shadow.",
        RelightPreset.Neon => "A restrained cyan-blue key light from camera-left and a magenta rim from camera-right. Retain facial detail and material texture; do not add neon signs or objects.",
        _ => "Infer the actual ambient and dominant light from the surrounding scene. Match its direction, color temperature, contrast and softness on the foreground subject."
    };
}
