namespace Composa.App.AI;

internal static class PartnerNodes
{
    public const string Seedream = "seedream 5.5";
    public const string SeedreamNode = "ByteDanceSeedreamNodeV3";

    public static string NodeFor(string model) => model switch
    {
        Seedream => SeedreamNode,
        NanoBanana.Model => "GeminiNanoBanana2V2",
        _ => "OpenAIGPTImageNodeV2"
    };

    public static (int Width, int Height) SeedreamSize(int width, int height)
    {
        if (width < 1 || height < 1 || (double)Math.Max(width, height) / Math.Min(width, height) > 16)
            throw new InvalidOperationException("Seedream 5.5 supports aspect ratios from 1:16 through 16:1.");
        const int minPixels = 921600, maxPixels = 4624220;
        double area = (double)width * height;
        var scale = Math.Sqrt(Math.Clamp(area, minPixels, maxPixels) / area);
        var shorter = Math.Max(240, 2 * (int)Math.Round(Math.Min(width, height) * scale / 2));
        var minLong = 2 * (int)Math.Ceiling(minPixels / (2.0 * shorter));
        var maxLong = Math.Min(16 * shorter, 2 * (maxPixels / (2 * shorter)));
        var longer = Math.Clamp(2 * (int)Math.Round(Math.Max(width, height) * scale / 2), minLong, maxLong);
        return width >= height ? (longer, shorter) : (shorter, longer);
    }
}
