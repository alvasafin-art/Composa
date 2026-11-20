namespace Composa.Model;

public readonly record struct TextFormat(Typography Type, double Tracking, double Leading, TextAlignment Alignment)
{
    internal TextFormat Scale(double x, double y) => this with
    {
        Tracking = Type.PercentTracking ? Tracking : Tracking * x,
        Leading = Type.PercentLeading ? Leading : Leading * y,
        Type = Type with { Indent = Type.Indent * x, ParagraphSpacing = Type.ParagraphSpacing * y }
    };
}
public sealed record TextFormatRun(int Start, int Length, TextFormat Format)
{
    [System.Text.Json.Serialization.JsonIgnore] public int End => Start + Length;
}

public sealed partial record TextStyle
{
    public IReadOnlyList<TextFormatRun>? FormatRuns { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public TextFormat Format => new(Type, Tracking, Leading, Alignment);
    public TextStyle WithFormat(TextFormat f) => this with { Type = f.Type, Tracking = f.Tracking, Leading = f.Leading, Alignment = f.Alignment };
    public TextFormat FormatAt(int index)
    {
        if (FormatRuns == null) return Format;
        int lo = 0, hi = FormatRuns.Count - 1;
        index = Math.Clamp(index, 0, Math.Max(0, Text.Length - 1));
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            var run = FormatRuns[mid];
            if (index < run.Start) hi = mid - 1;
            else if (index >= run.End) lo = mid + 1;
            else return run.Format;
        }
        return Format;
    }
    private IReadOnlyList<TextFormatRun>? ValidateFormatting(int length)
    {
        if (FormatRuns is not { Count: > 0 }) return null;
        int end = 0;
        foreach (var r in FormatRuns)
        {
            if (r == null || r.Start < end || r.Length <= 0 || r.Start > length - r.Length || r.Format.Type == null ||
                !double.IsFinite(r.Format.Tracking) || !double.IsFinite(r.Format.Leading) || !double.IsFinite(r.Format.Type.ParagraphSpacing)
                || !double.IsFinite(r.Format.Type.Indent) || r.Format.Tracking is < -100 or > 1000
                || r.Format.Leading is < 0 or > 5000 || r.Format.Type.Indent is < 0 or > 5000
                || r.Format.Type.ParagraphSpacing is < 0 or > 5000 || r.Format.Type.Axes is null or { Length: > 512 }) return null;
            end = r.End;
        }
        return FormatRuns;
    }
    private static void AddFormat(List<TextFormatRun> list, int start, int length, TextFormat value, TextFormat baseline)
    {
        if (length <= 0 || value == baseline) return;
        if (list.Count > 0 && list[^1].End == start && list[^1].Format == value)
            list[^1] = list[^1] with { Length = list[^1].Length + length };
        else list.Add(new(start, length, value));
    }
    private IReadOnlyList<TextFormatRun>? ReplaceFormatting(int start, int end, int length)
    {
        if (FormatRuns == null) return null;
        var list = new List<TextFormatRun>();
        foreach (var r in FormatRuns)
            AddFormat(list, r.Start, Math.Min(start, r.End) - r.Start, r.Format, Format);
        AddFormat(list, start, length, FormatAt(Math.Max(0, start - 1)), Format);
        foreach (var r in FormatRuns)
        {
            var from = Math.Max(end, r.Start);
            AddFormat(list, from + length - (end - start), r.End - from, r.Format, Format);
        }
        return list.Count == 0 ? null : list;
    }
    public TextStyle FormatRange(int start, int end, Func<TextStyle, TextStyle> edit, bool paragraphs = false)
    {
        bool whole = start == end || start == 0 && end == Text.Length;
        if (!whole && paragraphs)
        {
            while (start > 0 && Text[start - 1] != '\n') start--;
            while (end < Text.Length && Text[end - 1] != '\n') end++;
        }
        var baseline = whole ? edit(this).Format : Format;
        if (whole && FormatRuns == null) return WithFormat(baseline);
        var cuts = new SortedSet<int> { 0, Text.Length, start, end };
        foreach (var run in FormatRuns ?? []) { cuts.Add(run.Start); cuts.Add(run.End); }
        var list = new List<TextFormatRun>(); int previous = 0;
        foreach (int next in cuts)
        {
            if (next > previous)
            {
                var f = FormatAt(previous);
                if (whole || previous >= start && previous < end) f = edit(WithFormat(f)).Format;
                AddFormat(list, previous, next - previous, f, baseline);
            }
            previous = next;
        }
        return WithFormat(baseline) with { FormatRuns = list.Count == 0 ? null : list };
    }
}
