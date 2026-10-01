using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Composa.AI;

namespace Composa.App.AI;

internal sealed record PartnerPriceEstimate(double MinimumUsd, double MaximumUsd)
{
    public string Label => $"≈ {(MinimumUsd * PartnerPricing.CreditsPerDollar).ToString("0.0", CultureInfo.InvariantCulture)}–{(MaximumUsd * PartnerPricing.CreditsPerDollar).ToString("0.0", CultureInfo.InvariantCulture)} cr / ${MinimumUsd.ToString("0.000", CultureInfo.InvariantCulture)}–{MaximumUsd.ToString("0.000", CultureInfo.InvariantCulture)}";
}

/// <summary>Reads data tables from the server's official GPT price badge. Never evaluates remote code/JSONata.</summary>
internal static class PartnerPricing
{
    // Official Comfy rate, verified 2026-10-01: https://support.comfy.org/articles/1982697177-partner-nodes-pricing
    public const double CreditsPerDollar = 211;
    public static string Reported(double credits) => $"{credits.ToString("0.##", CultureInfo.InvariantCulture)} cr / ${(credits / CreditsPerDollar).ToString("0.000", CultureInfo.InvariantCulture)}";
    public static PartnerPriceEstimate? Estimate(ComfyServerCapabilities? capabilities, string model, string quality, string size, int images, int variants)
    {
        var expression = capabilities?.NodeDefinitions.GetValueOrDefault("OpenAIGPTImageNodeV2")?["price_badge"]?["expr"]?.GetValue<string>();
        if (expression == null || variants is < 1 or > 3 || images is < 0 or > 16) return null;
        JsonObject? Table(string name)
        {
            var match = Regex.Match(expression, @"\$" + name + @"\s*:=\s*(\{[\s\S]*?\})\s*;", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            return match.Success ? JsonNode.Parse(match.Groups[1].Value) as JsonObject : null;
        }
        try
        {
            var range = Table("ranges")?[model]?[quality] as JsonArray;
            var perImage = Table("perImage")?[model] as JsonArray;
            if (range?.Count != 2 || perImage?.Count != 2) return null;
            var preset = Table("presets")?[model.StartsWith("gpt-image-2.5-", StringComparison.Ordinal) ? "gpt-image-2.5" : model]?[quality]?[size]?.GetValue<double>();
            var minimum = ((preset ?? range[0]!.GetValue<double>()) + images * perImage[0]!.GetValue<double>()) * variants;
            var maximum = ((preset ?? range[1]!.GetValue<double>()) + images * perImage[1]!.GetValue<double>()) * variants;
            return double.IsFinite(minimum) && double.IsFinite(maximum) && minimum >= 0 && maximum >= minimum ? new(minimum, maximum) : null;
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or RegexMatchTimeoutException) { return null; }
    }

    public static bool SupportsModel(ComfyServerCapabilities server, string model) =>
        server.NodeDefinitions.GetValueOrDefault("OpenAIGPTImageNodeV2")?["input"]?["required"]?["model"]?[1]?["options"] is JsonArray choices
            && choices.Any(choice => choice?["key"]?.GetValue<string>() == model);

    public static IReadOnlyList<string> Choices(ComfyServerCapabilities? server, string model, string input)
    {
        if (server?.NodeDefinitions.GetValueOrDefault("OpenAIGPTImageNodeV2")?["input"]?["required"]?["model"]?[1]?["options"] is not JsonArray models) return [];
        var selected = models.FirstOrDefault(value => value?["key"]?.GetValue<string>() == model);
        return selected?["inputs"]?["required"]?[input]?[1]?["options"] is JsonArray choices
            ? choices.Select(value => value!.GetValue<string>()).ToArray() : [];
    }
}
