using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Generators;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;

namespace Leaves_LimitedTraders;

// Must run its OnUpdate strictly after BarterEconomy's: BarterEconomy fully resets and rebuilds a
// trader's entire assort from its own cached snapshot on every update, which would wipe out
// whatever stock counts/loyalty levels this mod had just set if this ran first in the same tick.
// Both mods watch the same trader NextResupply trigger, so when it fires they run in the same
// 5-second tick - TypePriority (which also orders IOnUpdate, not just IOnLoad, since both get
// registered together) is what guarantees the order. Same InjectionType.Singleton requirement as
// BarterEconomy applies here too - see its own comment for why.
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostDBModLoader + 2)]
public class LimitedTraders(
    DatabaseService databaseService,
    ModHelper modHelper,
    RagfairOfferGenerator ragfairOfferGenerator,
    ICloner cloner,
    TimeUtil timeUtil)
    : IOnLoad, IOnUpdate
{
    private LimitedTradersConfig config = null!;

    // This mod's own pristine snapshot, standing in for TS's
    // TraderAssortService.getPristineTraderAssort - not exposed publicly in SPT 4.0 (it's
    // `protected` on TraderAssortHelper). It exists because priceAdjustment/loyaltyMixup both need
    // a stable, NON-COMPOUNDING baseline: AdjustPrice multiplies the pristine price and
    // LoyaltyMixup resets to the pristine level before rolling, so reading back the already-adjusted
    // live value each tick would decay prices toward zero and ratchet every item down to loyalty 1.
    // We can't lean on BarterEconomy's assort reset for that baseline either - this is a standalone
    // mod that can't assume BarterEconomy is installed, and its tradersToBarter list needn't match
    // our tradersToLimit.
    //
    // Taken lazily on the first OnUpdate, NOT in OnLoad - see EnsureLateSetup.
    private Dictionary<MongoId, Trader> originalTraders = null!;

    private List<MongoId> tradersToUpdate = null!;
    private List<long> tradersLastUpdate = null!;
    private HashSet<MongoId> itemBlacklist = null!;
    private bool setUpCompleted;

    public Task OnLoad()
    {
        string modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        config = modHelper.GetJsonDataFromFile<LimitedTradersConfig>(modPath, "config/config.jsonc");

        Leaves.Utils.Instance.printColor("[Limited Traders] Limited Traders Starting");

        setUpCompleted = true;

        return Task.CompletedTask;
    }

    // Reads the trader database lazily, on the first OnUpdate, instead of in OnLoad.
    //
    // WHY: OnLoad runs at PostDBModLoader+2, which is the SAME priority content mods like
    // WTT-Armory/WTT-ContentBackport use to inject their trader stock - so their relative order was
    // undefined, and a snapshot taken in OnLoad could easily miss every modded item. The result was
    // quiet rather than destructive (AdjustPrice/LoyaltyMixup just TryGetValue-miss and skip, so
    // modded stock kept vanilla prices and loyalty while still being stock-limited), but it meant
    // this mod silently didn't apply to modded traders' items at all.
    //
    // OnUpdate only ever runs after the server has fully started - after EVERY mod's OnLoad,
    // whatever priority it used - so the database is guaranteed complete here, and this stays
    // correct no matter what priority a future mod picks. This does NOT change our TypePriority,
    // which must stay above BarterEconomy's so our OnUpdate keeps running AFTER its assort reset.
    private bool lateSetupDone;

    private void EnsureLateSetup()
    {
        if (lateSetupDone)
        {
            return;
        }
        lateSetupDone = true;

        var traders = databaseService.GetTraders();
        originalTraders = cloner.Clone(traders)!;

        tradersToUpdate = new List<MongoId>();
        tradersLastUpdate = new List<long>();
        itemBlacklist = new HashSet<MongoId>(config.ItemBlacklist);

        foreach (var traderID in config.TradersToLimit)
        {
            if (!traders.ContainsKey(traderID))
            {
                continue;
            }

            tradersToUpdate.Add(traderID);
            tradersLastUpdate.Add(0);
        }
    }

    public Task<bool> OnUpdate(long secondsSinceLastRun)
    {
        if (secondsSinceLastRun <= 30)
        {
            return Task.FromResult(false);
        }

        if (!setUpCompleted)
        {
            return Task.FromResult(false);
        }

        // First update = the earliest point the trader database is guaranteed complete.
        EnsureLateSetup();

        long timeNow = timeUtil.GetTimeStamp();
        var traders = databaseService.GetTraders();

        for (int traderNum = 0; traderNum < tradersToUpdate.Count; traderNum++)
        {
            var traderID = tradersToUpdate[traderNum];
            // Re-limit only once we're RefreshDelaySeconds past the stored resupply time, not the
            // instant it passes - see RefreshDelaySeconds. This lets BarterEconomy's assort rebuild
            // (which fires on the same resupply) land first even when the update loop's tick ordering
            // has drifted, so we don't limit a stale assort only to have it overwritten with full stock.
            if (timeNow <= tradersLastUpdate[traderNum] + config.RefreshDelaySeconds)
            {
                continue;
            }

            ModifyTrader(traderID);

            tradersLastUpdate[traderNum] = traders[traderID].Base.NextResupply ?? 0;
            Leaves.Utils.Instance.printColor($"[Limited Traders] Updating Trader:[{Leaves.Utils.Instance.getTraderNickname(traderID)}]-[{traderID}]", LogTextColor.Blue);
        }

        return Task.FromResult(true);
    }

    private void ModifyTrader(MongoId traderID)
    {
        var itemDB = databaseService.GetItems();
        var traders = databaseService.GetTraders();

        if (traders[traderID].Assort.Items == null)
        {
            Leaves.Utils.Instance.printColor($"Trader:{traderID} does not  have an assort.", LogTextColor.Red);
        }

        foreach (var item in traders[traderID].Assort.Items)
        {
            if (item.ParentId != "hideout")
            {
                continue;
            }

            // Blacklisted item tpl - leave it exactly as the trader (or another mod) set it.
            if (itemBlacklist.Contains(item.Template))
            {
                continue;
            }

            //check for invalid data
            if (!itemDB.TryGetValue(item.Template, out var template))
            {
                //Item doesnt exist in the global item database
                Leaves.Utils.Instance.printColor("[Limited Traders] Found trade with item that doesnt exist in the global database. This is most likely caused by a mod doing something wrong.", LogTextColor.Red);
                Leaves.Utils.Instance.printColor("[Limited Traders] Item ID of broken trade is: " + item.Template, LogTextColor.Red);
                continue;
            }
            if (template.Parent == "")
            {
                //Item exists but has no parent.
                Leaves.Utils.Instance.printColor("[Limited Traders] Found trade with item in the global database that has an invalid _parent entry. This is most likely caused by a mod doing something wrong.", LogTextColor.Red);
                Leaves.Utils.Instance.printColor("[Limited Traders] Item ID of broken item is: " + item.Template, LogTextColor.Red);
                continue;
            }

            //Check config categories
            int count = GetCount(template.Parent);
            AdjustPrice(template.Parent, item, traders[traderID].Assort.BarterScheme[item.Id], traders[traderID].Assort, traderID);

            item.Upd ??= new Upd();
            item.Upd.StackObjectsCount = count;

            //fix "a lot" message
            if (item.Upd.UnlimitedCount == true)
            {
                item.Upd.UnlimitedCount = false;
            }

            if (config.RemoveMaxBuyRestrictions)
            {
                //remove purchase limit
                item.Upd.BuyRestrictionMax = null;
                item.Upd.BuyRestrictionCurrent = null;
            }

            //Loyalty stuff
            if (config.LoyaltyMixup)
            {
                LoyaltyMixup(traders, traderID, item);
            }
        }

        ragfairOfferGenerator.GenerateFleaOffersForTrader(traderID);
    }

    private void AdjustPrice(MongoId parent, Item item, List<List<BarterScheme>> scheme, TraderAssort assort, MongoId traderID)
    {
        if (!config.Categories.TryGetValue(parent.ToString(), out var category) || category.PriceAdjustment == null)
        {
            return;
        }

        if (!Leaves.Utils.Instance.isMoneyTrade(assort, item))
        {
            return;
        }

        // Item didn't exist yet at OnLoad time (e.g. a fresh barter-copy BarterEconomy generated
        // this same tick) - nothing to adjust against, so skip it rather than crash.
        if (!originalTraders[traderID].Assort.BarterScheme.TryGetValue(item.Id, out var originalScheme))
        {
            return;
        }

        double priceAdjustment = category.PriceAdjustment.Value;
        //Get pristine price for item
        double originalPrice = originalScheme[0][0].Count ?? 0;
        scheme[0][0].Count = priceAdjustment * originalPrice;
    }

    private void LoyaltyMixup(Dictionary<MongoId, Trader> traders, MongoId traderID, Item item)
    {
        var loyalty = traders[traderID].Assort.LoyalLevelItems;

        // Same fresh-copy caveat as AdjustPrice above.
        if (!originalTraders[traderID].Assort.LoyalLevelItems.TryGetValue(item.Id, out var originalLoyalty))
        {
            return;
        }
        //Get pristine loyalty for this item.
        loyalty[item.Id] = originalLoyalty;

        //Down one
        if (loyalty[item.Id] > 1 && Random.Shared.NextDouble() <= config.LoyaltyMixupChance2)
        {
            loyalty[item.Id] -= 1;
        }

        //Down two
        if (loyalty[item.Id] > 2 && Random.Shared.NextDouble() <= config.LoyaltyMixupChance3)
        {
            loyalty[item.Id] -= 1;
        }

        //Down three
        if (loyalty[item.Id] > 3 && Random.Shared.NextDouble() <= config.LoyaltyMixupChance4)
        {
            loyalty[item.Id] -= 1;
        }
    }

    private int GetCount(MongoId parent)
    {
        double noStockRoll = Random.Shared.NextDouble();

        // Use category if it exists.
        if (config.Categories.TryGetValue(parent.ToString(), out var category))
        {
            // Roll for no stock.
            if (noStockRoll < category.ChanceForNoStock)
            {
                return 0;
            }

            return RandomCount(category.Base, category.Random);
        }

        if (noStockRoll > config.DefaultChanceForNoStock)
        {
            return RandomCount(config.DefaultBase, config.DefaultRandom);
        }

        return 0;
    }

    private int RandomCount(int baseCount, int random)
    {
        return baseCount + (int)Math.Floor(Random.Shared.NextDouble() * random * 2) - random;
    }
}
