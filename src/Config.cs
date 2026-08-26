using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Common;

namespace Leaves_LimitedTraders;

// Mirrors config/config.jsonc. timeBetweenUpdates is declared in the JSON but never actually
// read by the original TS mod - omitted here rather than ported as dead config.
public record LimitedTradersConfig
{
    [JsonPropertyName("tradersToLimit")]
    public required List<MongoId> TradersToLimit { get; set; }

    [JsonPropertyName("categories")]
    public required Dictionary<string, CategoryEntry> Categories { get; set; }

    // Item template IDs (tpls) to leave completely untouched - no stock/price/loyalty/buy-limit
    // changes. Matched against each assort item's Template. Non-required with a default so configs
    // written before this key existed still load.
    [JsonPropertyName("itemBlacklist")]
    public List<MongoId> ItemBlacklist { get; set; } = new List<MongoId>();

    [JsonPropertyName("defaultBase")]
    public required int DefaultBase { get; set; }

    [JsonPropertyName("defaultRandom")]
    public required int DefaultRandom { get; set; }

    [JsonPropertyName("defaultChanceForNoStock")]
    public required double DefaultChanceForNoStock { get; set; }

    [JsonPropertyName("removeMaxBuyRestrictions")]
    public required bool RemoveMaxBuyRestrictions { get; set; }

    [JsonPropertyName("loyaltyMixup")]
    public required bool LoyaltyMixup { get; set; }

    [JsonPropertyName("loyaltyMixupChance2")]
    public required double LoyaltyMixupChance2 { get; set; }

    [JsonPropertyName("loyaltyMixupChance3")]
    public required double LoyaltyMixupChance3 { get; set; }

    [JsonPropertyName("loyaltyMixupChance4")]
    public required double LoyaltyMixupChance4 { get; set; }

    // Seconds to wait AFTER a trader's resupply time before re-applying stock limits. We share the
    // resupply trigger with BarterEconomy (which rebuilds the assort to full stock on resupply) and
    // rely on TypePriority to run our OnUpdate after its in the same tick - but that same-tick
    // ordering isn't reliable once the server has been up a long time (the update loop drifts), so
    // occasionally we'd re-limit BEFORE BarterEconomy's rebuild and it would then overwrite us with
    // full stock ("infinite supply"). Waiting a couple of update ticks past the resupply guarantees
    // the rebuild has already happened. Cost: a brief window of full stock right after each resupply.
    // Non-required with a default so configs written before this key existed still load.
    [JsonPropertyName("refreshDelaySeconds")]
    public long RefreshDelaySeconds { get; set; } = 60;
}

public record CategoryEntry
{
    [JsonPropertyName("base")]
    public required int Base { get; set; }

    [JsonPropertyName("random")]
    public required int Random { get; set; }

    [JsonPropertyName("chanceForNoStock")]
    public required double ChanceForNoStock { get; set; }

    // Not required - several categories in config.jsonc omit this entirely, meaning "don't
    // price-adjust this category" (see AdjustPrice's null check).
    [JsonPropertyName("priceAdjustment")]
    public double? PriceAdjustment { get; set; }
}
