using System.Globalization;
using System.Text.Json;

namespace Composa.App.AI;

public sealed record ImageApiRate(decimal Minimum, decimal Maximum, string Currency)
{
    internal string Label => Minimum == Maximum
        ? $"{Format(Minimum)} {Currency}"
        : $"{Format(Minimum)}–{Format(Maximum)} {Currency}";

    private static string Format(decimal amount) => amount.ToString("0.########", CultureInfo.CurrentCulture);

    internal static ImageApiRate? FromCatalog(JsonElement model)
    {
        var provider = Property(model, "top_provider");
        var pricing = Property(provider, "pricing");
        if (pricing.ValueKind == JsonValueKind.Undefined) pricing = Property(model, "pricing");
        if (CurrencyCode(Property(pricing, "currency")) is not { } currency) return null;
        if (Amount(Property(pricing, "per_request")) is { } perRequest)
            return new(perRequest, perRequest, currency);
        if (Amount(Property(pricing, "per_image")) is { } perImage)
            return new(perImage, perImage, currency);
        var tiers = Property(pricing, "tiers");
        if (currency != "RUB" || tiers.ValueKind != JsonValueKind.Array) return null;
        var prices = tiers.EnumerateArray().Select(tier => Amount(Property(tier, "cost_rub")))
            .OfType<decimal>().ToArray();
        return prices.Length == 0 ? null : new(prices.Min(), prices.Max(), currency);
    }

    internal static ImageApiRate? FromResponse(JsonElement response, string? defaultCurrency)
    {
        var usage = Property(response, "usage");
        var currency = CurrencyCode(Property(usage, "currency")) ?? defaultCurrency;
        return currency != null && Amount(Property(usage, "cost")) is { } cost ? new(cost, cost, currency) : null;
    }

    private static JsonElement Property(JsonElement parent, string key) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out var value) ? value : default;

    private static string? CurrencyCode(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { Length: 3 } code && code.All(char.IsAsciiLetter)
            ? code.ToUpperInvariant() : null;

    private static decimal? Amount(JsonElement value)
    {
        decimal amount = 0;
        bool valid = value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out amount) ||
            value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
        if (!valid) return null;
        return amount >= 0 ? amount : null;
    }
}
