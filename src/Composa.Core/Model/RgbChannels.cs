namespace Composa.Model;

[Flags]
public enum RgbChannels { None = 0, Red = 1, Green = 2, Blue = 4, All = Red | Green | Blue }
