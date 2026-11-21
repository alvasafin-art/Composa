using System.Text.Json.Serialization;
using SkiaSharp;

namespace Composa.Model;

public enum CanvasColorMode { RGB, CMYK }
public enum CanvasGridKind { Columns, Rows, Squares }
public enum GridSizing { Stretch, Fixed }
public enum GridAlignment { Start, Center, End }

public readonly record struct CanvasMargins(int Top, int Right, int Bottom, int Left)
{
    public CanvasMargins(int all) : this(all, all, all, all) { }

    public CanvasMargins Normalized() => new(
        Math.Clamp(Top, 0, DocumentLimits.MaxSide), Math.Clamp(Right, 0, DocumentLimits.MaxSide),
        Math.Clamp(Bottom, 0, DocumentLimits.MaxSide), Math.Clamp(Left, 0, DocumentLimits.MaxSide));
}

/// <summary>Geometry of either columns or rows. A zero count means the grid is awaiting input.</summary>
public sealed record CanvasTracks(int Count = 4, int Gap = 24, int Margin = 64, GridSizing Sizing = GridSizing.Stretch,
    int Size = 96, GridAlignment Alignment = GridAlignment.Start)
{
    public CanvasTracks Normalized() => new(
        Math.Clamp(Count, 0, DocumentLimits.MaxLayoutTracks), Math.Clamp(Gap, 0, DocumentLimits.MaxSide),
        Math.Clamp(Margin, 0, DocumentLimits.MaxSide), Enum.IsDefined(Sizing) ? Sizing : GridSizing.Stretch,
        Math.Clamp(Size, 1, DocumentLimits.MaxSide), Enum.IsDefined(Alignment) ? Alignment : GridAlignment.Start);

    public TrackGeometry? Geometry(int extent)
    {
        if (Count < 1) return null;
        var available = extent - 2.0 * Margin - (Count - 1.0) * Gap;
        var size = Sizing == GridSizing.Fixed ? Size : available / Count;
        var extra = available - size * Count;
        if (size <= 0 || extra < -1e-8) return null;
        var start = Margin + Math.Max(0, extra) * (Sizing == GridSizing.Stretch ? 0 : Alignment switch
        {
            GridAlignment.Center => .5,
            GridAlignment.End => 1,
            _ => 0
        });
        return new TrackGeometry(Count, start, size, Gap);
    }
}

public readonly record struct TrackGeometry(int Count, double Start, double Size, double Gap)
{
    public double Position(int index) => Start + index * (Size + Gap);
}

/// <summary>Non-printing geometry for a single canvas. Visibility and snapping belong to view preferences.</summary>
public sealed record CanvasLayout
{
    public static CanvasLayout Empty { get; } = new();
    public const double MaxBleedMm = 100;
    public CanvasMargins SafeArea { get; init; }
    public bool LinkSafeArea { get; init; } = true;
    public CanvasTracks? Columns { get; init; }
    public CanvasTracks? Rows { get; init; }
    public int? SquareSize { get; init; }
    public double BleedMm { get; init; }

    [JsonIgnore]
    public bool HasGuides => SafeArea != default || Columns?.Count > 0 || Rows?.Count > 0 || SquareSize > 0 || BleedMm > 0;

    public CanvasLayout Normalized() => this with
    {
        SafeArea = SafeArea.Normalized(),
        Columns = Columns?.Normalized(), Rows = Rows?.Normalized(),
        SquareSize = SquareSize is { } size ? size == 0 ? 0 : Math.Clamp(size, LayoutGrid.MinSpacing, DocumentLimits.MaxSide) : null,
        BleedMm = double.IsFinite(BleedMm) ? Math.Clamp(BleedMm, 0, MaxBleedMm) : 0
    };

    public SKRect SafeBounds(int width, int height) => new(SafeArea.Left, SafeArea.Top, width - SafeArea.Right, height - SafeArea.Bottom);
    public double BleedPixels(double resolution) => BleedMm * resolution / 25.4;

    internal void AddSnapTargets(int width, int height, List<float> xs, List<float> ys)
    {
        AddTracks(Columns?.Geometry(width), xs);
        AddTracks(Rows?.Geometry(height), ys);
        if (SquareSize is > 0 and var step)
        {
            for (var x = 0; x <= width; x += step) xs.Add(x);
            for (var y = 0; y <= height; y += step) ys.Add(y);
        }
    }

    private static void AddTracks(TrackGeometry? geometry, List<float> targets)
    {
        if (geometry is not { } tracks) return;
        for (var i = 0; i < tracks.Count; i++)
        {
            var start = tracks.Position(i);
            targets.Add((float)start);
            targets.Add((float)(start + tracks.Size));
        }
    }
}
