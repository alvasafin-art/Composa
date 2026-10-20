using Composa.Rendering;
using Composa.Vision;
using SkiaSharp;

namespace Composa.AI;

/// <summary>Find the temporary object in the arranged scene, whose model-generated silhouette can differ
/// from the immutable PNG. This mask is used only for removing that temporary object.</summary>
public static class SceneBackgroundSubject
{
    public static void EnsureAvailable(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        _ = ModelRunner.Session(PromptModels.EfficientSamS.Encoder);
        token.ThrowIfCancellationRequested();
        _ = ModelRunner.Session(PromptModels.EfficientSamS.Decoder);
        token.ThrowIfCancellationRequested();
    }

    public static SKBitmap Locate(SceneEditInput input, SKBitmap layout, CancellationToken token = default)
    {
        if (layout.Width != input.ObjectAlpha.Width || layout.Height != input.ObjectAlpha.Height)
            throw new InvalidDataException("The background layout and subject mask have different dimensions.");
        var model = PromptModels.EfficientSamS;
        var encoded = PromptSegmentation.Encode(layout, model, token);
        var sx = encoded.Width / (double)layout.Width; var sy = encoded.Height / (double)layout.Height;
        var bounds = input.SubjectBounds;
        // A tight original box forces SAM to select only the part still inside the old PNG.
        // Give it room to follow changed pose/scale, then reject unrelated components by the original alpha.
        var box = new SKRectI(Math.Max(0, (int)Math.Floor((bounds.Left - bounds.Width * .4) * sx)),
            Math.Max(0, (int)Math.Floor((bounds.Top - bounds.Height * .4) * sy)),
            Math.Min(encoded.Width, (int)Math.Ceiling((bounds.Right + bounds.Width * .4) * sx)),
            Math.Min(encoded.Height, (int)Math.Ceiling((bounds.Bottom + bounds.Height * .4) * sy)));
        using var decoded = PromptSegmentation.Decode(encoded, model, encoded.Width, encoded.Height, null, box, token);
        using var original = Pixels.NewMask(encoded.Width, encoded.Height);
        using (var draw = new SKCanvas(original)) draw.DrawImage(Pixels.ImageOf(input.ObjectAlpha), SKRect.Create(original.Width, original.Height), new SKSamplingOptions(SKFilterMode.Nearest));
        Pixels.Invalidate(original);
        var supportThreshold = Math.Max((byte)1, Math.Min((byte)128, input.SupportAlpha));
        using var anchored = Anchored(decoded, original, supportThreshold, token);
        long area = 0, originalArea = 0, overlap = 0;
        for (var y = 0; y < original.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < original.Width; x++)
            {
                var subject = original.GetPixel(x, y).Alpha >= supportThreshold; var found = anchored.GetPixel(x, y).Alpha > 0;
                if (subject) originalArea++; if (found) area++; if (subject && found) overlap++;
            }
        }
        if (originalArea == 0 || overlap < originalArea * .15 || area > Math.Min(originalArea * 5, original.Width * (double)original.Height * .98))
            throw new InvalidOperationException("The temporary subject could not be isolated reliably. No background was applied; try a clearer scene or another seed.");
        var result = Pixels.NewMask(layout.Width, layout.Height);
        try
        {
            using (var draw = new SKCanvas(result)) draw.DrawImage(Pixels.ImageOf(anchored), SKRect.Create(result.Width, result.Height), new SKSamplingOptions(SKFilterMode.Linear));
            Pixels.Invalidate(result); token.ThrowIfCancellationRequested(); return result;
        }
        catch { result.Dispose(); throw; }
    }

    internal static SKBitmap Anchored(SKBitmap candidate, SKBitmap original, byte threshold, CancellationToken token)
    {
        if (candidate.ColorType != SKColorType.Alpha8 || original.ColorType != SKColorType.Alpha8 || candidate.Width != original.Width || candidate.Height != original.Height)
            throw new ArgumentException("Subject isolation needs equally sized Alpha8 masks.");
        var width = candidate.Width; var height = candidate.Height; var visited = new byte[checked(width * height)];
        var result = Pixels.NewMask(width, height);
        try
        {
            var pending = new Stack<int>(); var runs = new List<(int Left, int Right, int Y)>();
            bool Set(int x, int y) => visited[y * width + x] == 0 && candidate.GetPixel(x, y).Alpha >= 128;
            for (var y = 0; y < height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (var x = 0; x < width; x++)
                {
                    if (!Set(x, y)) continue;
                    pending.Push(y * width + x); runs.Clear(); long count = 0, overlap = 0;
                    while (pending.TryPop(out var index))
                    {
                        token.ThrowIfCancellationRequested(); var px = index % width; var py = index / width;
                        if (!Set(px, py)) continue;
                        var left = px; var right = px;
                        while (left > 0 && Set(left - 1, py)) left--;
                        while (right + 1 < width && Set(right + 1, py)) right++;
                        runs.Add((left, right, py));
                        for (var ix = left; ix <= right; ix++) { visited[py * width + ix] = 1; count++; if (original.GetPixel(ix, py).Alpha >= threshold) overlap++; }
                        foreach (var ny in new[] { py - 1, py + 1 })
                        {
                            if (ny < 0 || ny >= height) continue;
                            var inRun = false;
                            for (var ix = left; ix <= right; ix++) { var set = Set(ix, ny); if (set && !inRun) pending.Push(ny * width + ix); inRun = set; }
                        }
                    }
                    if (overlap == 0 || overlap < count * .01) continue;
                    var data = result.GetPixelSpan();
                    foreach (var run in runs) data.Slice(run.Y * result.RowBytes + run.Left, run.Right - run.Left + 1).Fill(255);
                }
            }
            Pixels.Invalidate(result); return result;
        }
        catch { result.Dispose(); throw; }
    }
}
