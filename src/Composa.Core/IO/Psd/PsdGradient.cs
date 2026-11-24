using Composa.Model;
using SkiaSharp;

namespace Composa.IO.Psd;

internal static class PsdGradient
{
    public static (GradientRamp? Ramp, bool Approximated) ReadRamp(Dictionary<string, object?>? gradient,
        bool reverse = false, bool approximateMidpoints = false)
    {
        if (PsdDescriptor.Enumeration(gradient, "GrdF") != "CstS"
            || PsdDescriptor.List(gradient, "Clrs") is not { Count: >= 2 and <= 64 } colors
            || PsdDescriptor.List(gradient, "Trns") is not { Count: <= 64 } opacities) return (null, false);

        double? Position(Dictionary<string, object?> stop)
        {
            if (PsdDescriptor.Number(stop, "Lctn") is not { } loc || loc is < 0 or > 4096) return null;
            return loc / 4096;
        }
        double? Midpoint(Dictionary<string, object?> stop)
        {
            var midpoint = PsdDescriptor.Number(stop, "Mdpn") ?? 50;
            return midpoint == 50 || approximateMidpoints && midpoint is > 0 and < 100 ? midpoint / 100 : null;
        }
        var colorStops = new List<(double Position, double Midpoint, uint Value)>();
        foreach (var item in colors)
        {
            if (item is not Dictionary<string, object?> stop || Position(stop) is not { } at || Midpoint(stop) is not { } middle
                || PsdDescriptor.Color(PsdDescriptor.Child(stop, "Clr ")) is not { } color) return (null, false);
            colorStops.Add((at, middle, color));
        }
        var opacityStops = new List<(double Position, double Midpoint, double Value)>();
        foreach (var item in opacities)
        {
            if (item is not Dictionary<string, object?> stop || Position(stop) is not { } at || Midpoint(stop) is not { } middle
                || PsdDescriptor.Number(stop, "Opct") is not { } opacity || opacity is < 0 or > 100) return (null, false);
            opacityStops.Add((at, middle, opacity / 100));
        }
        var approximated = false;
        var colorRamp = Expand(colorStops, AverageColor).Select(s => new GradientColorStop(s.Position, s.Value)).ToArray();
        var opacityRamp = Expand(opacityStops, (a, b) => (a + b) / 2).Select(s => new GradientOpacityStop(s.Position, s.Value)).ToArray();
        if (colorRamp.Length > 64 || opacityRamp.Length > 64) return (null, false);
        return (new GradientRamp { Colors = colorRamp, Opacities = opacityRamp }.Normalized(), approximated);

        IEnumerable<(double Position, T Value)> Expand<T>(List<(double Position, double Midpoint, T Value)> stops, Func<T, T, T> blend)
        {
            var expanded = new List<(double Position, T Value)>();
            var sorted = stops.OrderBy(s => s.Position).ToArray();
            for (var i = 0; i < sorted.Length; i++)
            {
                var stop = sorted[i];
                expanded.Add((stop.Position, stop.Value));
                if (stop.Midpoint == .5 || i + 1 == sorted.Length || stop.Position >= sorted[i + 1].Position) continue;
                var next = sorted[i + 1];
                // The editor has no midpoint handles. Put the half blend at the authored position.
                expanded.Add((stop.Position + stop.Midpoint * (next.Position - stop.Position), blend(stop.Value, next.Value)));
                approximated = true;
            }
            return reverse ? expanded.AsEnumerable().Reverse().Select(s => (1 - s.Position, s.Value)) : expanded;
        }

        static uint AverageColor(uint left, uint right)
        {
            var a = new SKColor(left); var b = new SKColor(right);
            static byte Half(byte x, byte y) => (byte)Math.Round((x + y) / 2.0);
            return (uint)new SKColor(Half(a.Red, b.Red), Half(a.Green, b.Green), Half(a.Blue, b.Blue));
        }
    }
}
