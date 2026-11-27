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
        ("violet", new(148,74,227)), ("red", new(233,43,43)),
        ("lime", new(164,220,37)), ("sky blue", new(74,166,255)), ("pink", new(255,149,184)),
        ("teal", new(12,130,124)), ("brown", new(124,70,48)), ("coral", new(255,121,111)),
        ("olive", new(126,139,39)), ("lavender", new(192,159,255))
    ];
    [JsonIgnore]
    public (string Name, SKColor Color) Ink
    {
        get
        {
            if (Number <= Palette.Length) return Palette[Math.Max(0, Number - 1)];

            // A permutation of the 11×11×11 RGB grid keeps all 1,000 possible document IDs distinct.
            var index = (int)(((long)Number - Palette.Length - 1) * 499 % 1331);
            static byte Channel(int digit) => (byte)(24 + digit * 21);
            var color = new SKColor(Channel(index % 11), Channel(index / 11 % 11), Channel(index / 121));
            return ($"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}", color);
        }
    }
}
