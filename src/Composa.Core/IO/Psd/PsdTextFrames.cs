using Composa.Model;
using SkiaSharp;
using static Composa.IO.Psd.PsdText.EngineData;

namespace Composa.IO.Psd;

/// <summary>The document-level type objects and their frames in Photoshop's Txt2 block.</summary>
internal static class PsdTextFrames
{
    public static (bool HasPath, TextPath? Path) ReadPath(Dictionary<string, object?>? frames, Dictionary<string, object?> descriptor, string text)
    {
        var objects = List(Walk(frames, "1", "1"));
        if (PsdDescriptor.Number(descriptor, "TextIndex") is not { } index || index < 0 || index >= objects.Count || index != Math.Truncate(index))
            return (false, null);
        var type = objects[(int)index];
        var story = String(Walk(type, "0", "0"));
        // Photoshop adds one terminal paragraph character to the story in Txt2.
        if (story?.EndsWith('\r') == true) story = story[..^1];
        if (PsdText.Cleaned(story) != text) return (false, null);

        var composer = List(Walk(type, "1", "2")).FirstOrDefault();
        var rendered = List(Walk(composer, "6")).FirstOrDefault(item => String(Walk(item, "99")) == "F");
        var allFrames = List(Walk(frames, "0", "8", "0"));
        if (Number(Walk(rendered, "10")) is not { } id || id < 0 || id >= allFrames.Count || id != Math.Truncate(id))
            return (false, null);
        var frame = Walk(allFrames[(int)id], "0");
        if (Number(Walk(frame, "2", "0")) != 2) return (false, null);
        return (true, Outline(frame));
    }

    private static TextPath? Outline(object? frame)
    {
        var points = List(Walk(frame, "1", "0"));
        if (points.Count is < 8 or > 4096 * 8 || points.Count % 8 != 0) return null;
        var coords = Numbers(points);
        var transform = Numbers(List(Walk(frame, "2", "2")));
        if (coords == null || transform == null || transform.Length != 6) return null;
        var matrix = new SKMatrix((float)transform[0], (float)transform[2], (float)transform[4],
            (float)transform[1], (float)transform[3], (float)transform[5], 0, 0, 1);
        if (!matrix.TryInvert(out _)) return null;
        SKPoint Point(int index) => matrix.MapPoint((float)coords[index], (float)coords[index + 1]);
        var first = Point(0);
        var previous = first;
        var incoming = first;
        var nodes = new List<BezierNode>();
        for (var i = 0; i < coords.Length; i += 8)
        {
            var at = Point(i); var outgoing = Point(i + 2);
            if (!Same(at, previous)) return null;
            nodes.Add(new(at.X, at.Y, incoming.X, incoming.Y, outgoing.X, outgoing.Y));
            incoming = Point(i + 4);
            previous = Point(i + 6);
        }
        var closed = Same(previous, first);
        if (closed) nodes[0] = nodes[0] with { InX = incoming.X, InY = incoming.Y };
        else nodes.Add(new(previous.X, previous.Y, incoming.X, incoming.Y, previous.X, previous.Y));

        var segmentCount = coords.Length / 8;
        var limits = Numbers(List(Walk(frame, "2", "6")));
        if (limits == null || limits.Length is not (0 or 2)) return null;
        var start = limits.Length == 0 || limits[0] < 0 ? (closed ? segmentCount - 1 : 0) : limits[0];
        double? end = limits.Length == 0 || limits[1] < 0 ? null : limits[1];
        if (closed && end is { } finish)
        {
            // Closed frames can cross the start of the stored contour.
            if (finish <= start) finish += segmentCount;
            if (Math.Abs(finish - (start + segmentCount)) <= .0001) finish = start + segmentCount;
            end = finish;
        }
        var path = new TextPath(new VectorPath { Nodes = nodes.ToArray(), Closed = closed }, start, end);
        return path.IsValid() ? path : null;
    }

    private static bool Same(SKPoint a, SKPoint b) => Math.Abs(a.X - b.X) <= .001 && Math.Abs(a.Y - b.Y) <= .001;

    private static double[]? Numbers(List<object?> values)
    {
        var numbers = new double[values.Count];
        for (var i = 0; i < numbers.Length; i++)
        {
            if (Number(values[i]) is not { } value || !double.IsFinite(value)) return null;
            numbers[i] = value;
        }
        return numbers;
    }
}
