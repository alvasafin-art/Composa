using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.AI;

/// <summary>Generation geometry, independent of editing-tool softness and saved AI edge controls.</summary>
public sealed record AiMaskPlan(SKRectI Bounds, int SeamWidth, int ContextPadding)
{
    public const int ModelBlur = 4;

    // A fully denoised overlap surrounds the insertion seam. A latent cell and the
    // Gaussian tail stay outside the opaque replacement core even for tiny edits.
    public int ConditioningGrow(double modelScale) => SeamWidth + (int)Math.Ceiling((16 + 3 * ModelBlur) / Math.Max(.01, modelScale));
}

public static class AutomaticAiMask
{
    public static AiTaskRequest IgnoreLegacyControls(AiTaskRequest request)
    {
        if (!AppliesTo(request.Task)) return request;
        var values = new Dictionary<string, object?>(request.Settings.Values, StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "maskGrow", "maskBlend", "maskBlur", "maskContext", "colorMatch", "gptContextPadding" }) values.Remove(key);
        return request with { Settings = request.Settings with { Values = values }, RemoveObject = request.RemoveObject with { Dilation = 0, Feather = 0 } };
    }

    public static bool AppliesTo(AiTaskKind task) => task is AiTaskKind.GenerativeFill or AiTaskKind.RemoveObject
        or AiTaskKind.GenerativeExpand or AiTaskKind.ChangeBackground or AiTaskKind.Harmonize or AiTaskKind.Relight;

    /// <summary>Only the contour is an edit request. Alpha strength is not denoising strength.</summary>
    public static SKBitmap Normalize(SKBitmap selection)
    {
        var result = Pixels.NewMask(selection.Width, selection.Height);
        var source = selection.GetPixelSpan(); var output = result.GetPixelSpan();
        byte peak = 0;
        for (var y = 0; y < selection.Height; y++) for (var x = 0; x < selection.Width; x++)
            peak = Math.Max(peak, source[y * selection.RowBytes + x]);
        if (peak == 0) return result;
        var threshold = (peak + 1) / 2;
        for (var y = 0; y < selection.Height; y++) for (var x = 0; x < selection.Width; x++)
            output[y * result.RowBytes + x] = source[y * selection.RowBytes + x] >= threshold ? (byte)255 : (byte)0;
        Pixels.Invalidate(result); return result;
    }

    public static SKRectI ContourBounds(SKBitmap selection)
    {
        byte peak = 0; var pixels = selection.GetPixelSpan();
        for (var y = 0; y < selection.Height; y++) for (var x = 0; x < selection.Width; x++)
            peak = Math.Max(peak, pixels[y * selection.RowBytes + x]);
        return peak == 0 ? SKRectI.Empty : SelectionMask.Bounds(selection, (byte)((peak + 1) / 2));
    }

    public static AiMaskPlan Geometry(SKRectI selection, SKRectI canvas)
    {
        if (selection.IsEmpty) return new(SKRectI.Empty, 0, 0);
        var shortSide = Math.Min(selection.Width, selection.Height);
        var seam = Math.Clamp((int)Math.Round(shortSide * .04), 3, 32);
        var padding = Math.Max(32, (int)Math.Ceiling(shortSide * .22)) + seam;
        var w = Math.Min(canvas.Width, selection.Width + 2 * padding);
        var h = Math.Min(canvas.Height, selection.Height + 2 * padding);
        // Thin edits still need a useful two-dimensional neighbourhood; this also
        // satisfies partner API aspect limits whenever the source canvas allows it.
        w = Math.Min(canvas.Width, Math.Max(w, (h + 2) / 3));
        h = Math.Min(canvas.Height, Math.Max(h, (w + 2) / 3));
        var left = Math.Clamp((int)Math.Round((selection.Left + (double)selection.Right - w) / 2), canvas.Left, canvas.Right - w);
        var top = Math.Clamp((int)Math.Round((selection.Top + (double)selection.Bottom - h) / 2), canvas.Top, canvas.Bottom - h);
        return new(new(left, top, left + w, top + h), seam, padding);
    }

    public static AiMaskPlan Analyze(SKBitmap core, SKBitmap reference)
    {
        if (core.Width != reference.Width || core.Height != reference.Height) throw new ArgumentException("Mask and context must share coordinates.");
        var geometry = Geometry(SelectionMask.Bounds(core, 128), reference.Info.Rect);
        if (geometry.Bounds.IsEmpty) return geometry;
        // Narrow the seam in detailed surroundings; use a wider transition on smooth
        // skies/walls. Sample source pixels only, never the object being replaced.
        double detail = 0; var count = 0;
        var step = Math.Max(1, Math.Max(geometry.Bounds.Width, geometry.Bounds.Height) / 96);
        for (var y = geometry.Bounds.Top; y < geometry.Bounds.Bottom - 1; y += step)
        for (var x = geometry.Bounds.Left; x < geometry.Bounds.Right - 1; x += step)
        {
            if (core.GetPixel(x, y).Alpha != 0 || core.GetPixel(x + 1, y).Alpha != 0 || core.GetPixel(x, y + 1).Alpha != 0) continue;
            var a = reference.GetPixel(x, y); var b = reference.GetPixel(x + 1, y); var c = reference.GetPixel(x, y + 1);
            if (a.Alpha != 255 || b.Alpha != 255 || c.Alpha != 255) continue;
            detail += (Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue)
                + Math.Abs(a.Red - c.Red) + Math.Abs(a.Green - c.Green) + Math.Abs(a.Blue - c.Blue)) / 6.0;
            count++;
        }
        var seam = count == 0 ? geometry.SeamWidth : Math.Max(2, (int)Math.Round(geometry.SeamWidth / (1 + detail / count / 18)));
        return geometry with { SeamWidth = seam };
    }

    public static SKBitmap OutputMask(SKBitmap core, SKBitmap reference, AiMaskPlan plan, bool expansion = false)
    {
        var result = expansion ? AiResultPostprocessor.ExpansionEditMask(core, reference, plan.SeamWidth)
            : AiResultPostprocessor.OutwardEditMask(core, 0, plan.SeamWidth);
        var coverage = result.GetPixelSpan(); var requested = core.GetPixelSpan(); var colors = reference.GetPixelSpan();
        for (var y = 0; y < result.Height; y++) for (var x = 0; x < result.Width; x++)
        {
            // A translucent source cannot be replaced by layering an opaque feather
            // without increasing its alpha. Protect it outside the requested core.
            if (requested[y * core.RowBytes + x] == 0 && colors[y * reference.RowBytes + x * 4 + 3] < 255)
                coverage[y * result.RowBytes + x] = 0;
        }
        Pixels.Invalidate(result); return result;
    }
}
