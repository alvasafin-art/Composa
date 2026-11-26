using System.Text.Json.Serialization;
using SkiaSharp;

namespace Composa.Model;

public sealed record PromptRegion(int Number, int Left, int Top, int Right, int Bottom, string Prompt = "", bool Enabled = true)
{
    [JsonIgnore]
    public SKRectI Bounds => new(Left, Top, Right, Bottom);
    [JsonIgnore]
    public bool Valid => Number > 0 && Left < Right && Top < Bottom && Prompt is { Length: <= 8192 };
    public static readonly (string Name, SKColor Color)[] Palette =
    [
        ("blue", new(25,79,212)), ("yellow", new(255,228,0)), ("magenta", new(232,16,156)),
        ("cyan", new(0,222,224)), ("orange", new(255,110,0)), ("green", new(41,168,61)),
        ("violet", new(148,74,227)), ("red", new(233,43,43))
    ];
    [JsonIgnore]
    public (string Name, SKColor Color) Ink => Palette[(Number - 1) % Palette.Length];
}
