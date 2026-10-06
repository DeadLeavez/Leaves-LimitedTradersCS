using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Leaves;
using SPTarkov.Server.Core.Models.Common;

namespace Leaves_LimitedTraders;

[ConfigHeader("""
  Limited Traders reduces the stock of all traders, with optional per-category price adjustments.
  Works with vanilla traders and *should* work with modded traders too. Probably avoid adding fence.
""")]
public record LimitedTradersConfig
{
    [Comment("Remove or add traders you want to have adjusted.")]
    [AllowedIds(AllowedIdKind.Trader)]
    [JsonPropertyName("tradersToLimit")]
    public required List<MongoId> TradersToLimit { get; set; }

    [Comment("Items to leave completely untouched by this mod.")]
    [AllowedIds(AllowedIdKind.Item)]
    [JsonPropertyName("itemBlacklist")]
    public List<MongoId> ItemBlacklist { get; set; } = new List<MongoId>();

    [Comment("""
      Keyed by an item tpl OR a category (parent) node id at ANY depth. For each trade the most
      specific matching entry wins, effectively prioritizing this order:
        1. the item's own tpl
        2. its immediate parent category
        3. that parent's parent, and so on up the category tree
        4. the root "Item" node 54009119af1c881c07000029 (Add this category to have a "default")
      An item that matches nothing in its chain is left completely untouched. 
      Check https://localhost:6969/webui/db while the server is running for category and item IDs.

      A parent tree might look something like this. 
      ``Item › Compound item › Weapon › Assault rifle › Colt M4A1 5.56x45 assault rifle``
    """)]
    [AllowedIds(AllowedIdKind.Node)]
    [AllowedIds(AllowedIdKind.Item)]
    [JsonPropertyName("categories")]
    public required Dictionary<MongoId, CategoryEntry> Categories { get; set; }

    [Comment("Removes max buy limit")]
    [JsonPropertyName("removeMaxBuyRestrictions")]
    public required bool RemoveMaxBuyRestrictions { get; set; }

    [Comment("""
      Seconds to wait after a trader's resupply before re-applying stock limits. 
      Recommended to leave at 60 so BarterEconomy's (if installed) full-stock rebuild (which fires on the same resupply) lands first. 
      Higher -> less chance of getting tangled, but longer full-stock window right after resupply.
    """)]
    [JsonPropertyName("refreshDelaySeconds")]
    public long RefreshDelaySeconds { get; set; } = 60;
}

public record CategoryEntry
{
    [Comment("The base count of an item.")]
    [JsonPropertyName("base")]
    [SchemaDefault(300)]
    public required int Base { get; set; } = 300;

    [Comment("The variance of the base count. E.g. Base is 300 and random is 200, the final spread is 100-500.")]
    [JsonPropertyName("random")]
    [SchemaDefault(200)]
    public required int Random { get; set; } = 200;

    [Comment("Chance for there to be none of an item available. Rolled for eached individual item.")]
    [JsonPropertyName("chanceForNoStock")]
    [SchemaDefault(0)]
    [Range(0, 1)]
    [Percent]
    public required double ChanceForNoStock { get; set; } = 0.0;

    [Comment("Price adjustment. 2.0 means twice the price. 0.5 means half the price.")]
    [SchemaDefault(1.0)]
    [JsonPropertyName("priceAdjustment")]
    public double? PriceAdjustment { get; set; } = 1.0;
}
