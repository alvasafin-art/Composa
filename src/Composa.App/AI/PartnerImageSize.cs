namespace Composa.App.AI;

/// <summary>Official GPT 2.5 custom-size limits; rounding/scaling is uniform, never a square stretched to the canvas.</summary>
internal static class PartnerImageSize
{
    public const int MinimumEdge = 480, MaximumEdge = 3840;
    public const long MinimumPixels = 655_360, MaximumPixels = 8_294_400;

    public static (int Width, int Height) Plan(int width, int height)
    {
        if (width <= 0 || height <= 0 || (double)Math.Max(width, height) / Math.Min(width, height) > 3)
            throw new InvalidOperationException("GPT supports proportions from 1:3 to 3:1. Use FLUX for a wider canvas; no image will be stretched.");
        var scale = Math.Max((double)MinimumEdge / Math.Min(width, height), Math.Sqrt((double)MinimumPixels / width / height));
        scale = Math.Max(1, scale);
        scale = Math.Min(scale, Math.Min((double)MaximumEdge / Math.Max(width, height), Math.Sqrt((double)MaximumPixels / width / height)));
        var w = (int)Math.Round(width * scale / 16) * 16; var h = (int)Math.Round(height * scale / 16) * 16;
        while ((long)w * h < MinimumPixels) { if (width >= height) { h += 16; w = (int)Math.Round((double)h * width / height / 16) * 16; } else { w += 16; h = (int)Math.Round((double)w * height / width / 16) * 16; } }
        while ((long)w * h > MaximumPixels || Math.Max(w, h) > MaximumEdge) { if (width >= height) { w -= 16; h = (int)Math.Round((double)w * height / width / 16) * 16; } else { h -= 16; w = (int)Math.Round((double)h * width / height / 16) * 16; } }
        // Quantizing near the 3:1 limit must not create an invalid 3.01:1 request.
        if (w > 3 * h) h = (int)Math.Ceiling(w / 3.0 / 16) * 16;
        if (h > 3 * w) w = (int)Math.Ceiling(h / 3.0 / 16) * 16;
        if ((long)w * h > MaximumPixels) { w -= 16; h -= 16; }
        return (w, h);
    }
}
