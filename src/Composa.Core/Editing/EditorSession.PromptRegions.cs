using Composa.Model;
using SkiaSharp;

namespace Composa.Editing;

public sealed partial class EditorSession
{
    public event Action? PromptRegionsChanged;

    private void MapPromptRegions(SKMatrix matrix, SKRectI bounds)
    {
        for (var i = document.PromptRegions.Count - 1; i >= 0; i--)
        {
            var r = document.PromptRegions[i];
            var mapped = matrix.MapRect(r.Bounds);
            var clipped = SKRectI.Intersect(bounds, new((int)Math.Round(mapped.Left), (int)Math.Round(mapped.Top),
                (int)Math.Round(mapped.Right), (int)Math.Round(mapped.Bottom)));
            if (clipped.IsEmpty) document.PromptRegions.RemoveAt(i);
            else document.PromptRegions[i] = r with { Left = clipped.Left, Top = clipped.Top, Right = clipped.Right, Bottom = clipped.Bottom };
        }
    }

    public PromptRegion? AddPromptRegion(SKRectI bounds)
    {
        bounds = SKRectI.Intersect(bounds, document.Bounds);
        if (bounds.IsEmpty || document.PromptRegions.Count >= 1000) return null;
        var number = Enumerable.Range(1, 1001).First(n => document.PromptRegions.All(r => r.Number != n));
        var region = new PromptRegion(number, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        Apply("New AI region", () => document.PromptRegions.Add(region));
        PromptRegionsChanged?.Invoke();
        return region;
    }

    public void ChangePromptRegion(PromptRegion region)
    {
        var index = document.PromptRegions.FindIndex(r => r.Number == region.Number);
        if (index < 0 || !region.Valid || document.PromptRegions[index] == region) return;
        Apply("Edit AI region", () => document.PromptRegions[index] = region);
        PromptRegionsChanged?.Invoke();
    }

    public void DeletePromptRegion(int number)
    {
        if (document.PromptRegions.All(r => r.Number != number)) return;
        Apply("Delete AI region", () => document.PromptRegions.RemoveAll(r => r.Number == number));
        PromptRegionsChanged?.Invoke();
    }

    public void DeleteAllPromptRegions()
    {
        if (document.PromptRegions.Count == 0) return;
        Apply("Delete all AI regions", document.PromptRegions.Clear);
        PromptRegionsChanged?.Invoke();
    }
}
