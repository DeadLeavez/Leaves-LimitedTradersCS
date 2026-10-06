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

[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostDBModLoader + 2)]
public partial class LimitedTraders(
    DatabaseService databaseService,
    ModHelper modHelper,
    RagfairOfferGenerator ragfairOfferGenerator,
    ICloner cloner,
    TimeUtil timeUtil)
    : IOnLoad, IOnUpdate
{
    private LimitedTradersConfig config = null!;
    private string modPath = null!;

    private Dictionary<MongoId, Trader> originalTraders = null!;

    private List<MongoId> tradersToUpdate = null!;
    private List<long> tradersLastUpdate = null!;
    private bool setUpCompleted;

    // Guards the OnUpdate trader loop against a config apply (HTTP thread) swapping the two lists mid-iteration.
    private readonly object scheduleLock = new();

    public Task OnLoad()
    {
        modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        config = modHelper.GetJsonDataFromFile<LimitedTradersConfig>(modPath, "config/config.jsonc");

        Leaves.Utils.Instance.printColor("[Limited Traders] Limited Traders Starting");

        setUpCompleted = true;

        return Task.CompletedTask;
    }

    public void saveConfig()
    {
        Leaves.Utils.Instance.saveJsoncWithComments(config, modPath, "config/config.jsonc");
    }

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

        foreach (var traderID in config.TradersToLimit)
        {
            if (!traders.ContainsKey(traderID))
            {
                continue;
            }

            tradersToUpdate.Add(traderID);
            tradersLastUpdate.Add(0);
        }

        // Item DB is complete by the first OnUpdate (WTT & friends inject their items after
        // PostDBModLoader), so this is the earliest safe point to check the [AllowedIds] keys.
        // Stray keys are harmless at runtime (no chain match -> item left untouched), so warn only.
        foreach (string problem in Leaves.Utils.Instance.CheckAllowedIds(config))
        {
            Leaves.Utils.Instance.printColor("[Limited Traders] config: " + problem, LogTextColor.Yellow);
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

        // Held so a config apply on the HTTP thread can't swap tradersToUpdate / tradersLastUpdate
        // out from under this loop (see scheduleLock).
        lock (scheduleLock)
        {
            for (int traderNum = 0; traderNum < tradersToUpdate.Count; traderNum++)
            {
                var traderID = tradersToUpdate[traderNum];

                if (timeNow <= tradersLastUpdate[traderNum] + config.RefreshDelaySeconds)
                {
                    continue;
                }

                ModifyTrader(traderID);

                tradersLastUpdate[traderNum] = traders[traderID].Base.NextResupply ?? 0;
                Leaves.Utils.Instance.printColor($"[Limited Traders] Updating Trader:[{Leaves.Utils.Instance.getTraderNickname(traderID)}]-[{traderID}]", LogTextColor.Blue);
            }
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
            if (config.ItemBlacklist.Contains(item.Template))
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

            // Resolve the most specific matching config entry: exact item tpl, then each parent
            // up the category tree, root "Item" node last. No match anywhere -> leave the trade
            // exactly as it is (this is the intended fallback now that "default" is gone).
            CategoryEntry? category = ResolveEntry(item.Template, template);
            if (category == null)
            {
                continue;
            }

            int count = GetCount(category);
            AdjustPrice(category, item, traders[traderID].Assort.BarterScheme[item.Id], traders[traderID].Assort, traderID);

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
        }

        ragfairOfferGenerator.GenerateFleaOffersForTrader(traderID);
    }

    private void AdjustPrice(CategoryEntry category, Item item, List<List<BarterScheme>> scheme, TraderAssort assort, MongoId traderID)
    {
        if (category.PriceAdjustment == null)
        {
            return;
        }

        if (!Leaves.Utils.Instance.isMoneyTrade(assort, item))
        {
            return;
        }

        // Item didn't exist yet at OnLoad time (e.g. a fresh barter-copy BarterEconomy generated this same tick) - nothing to adjust against, so skip it rather than crash.
        if (!originalTraders[traderID].Assort.BarterScheme.TryGetValue(item.Id, out var originalScheme))
        {
            return;
        }

        double priceAdjustment = category.PriceAdjustment.Value;
        //Get pristine price for item
        double originalPrice = originalScheme[0][0].Count ?? 0;
        scheme[0][0].Count = priceAdjustment * originalPrice;
    }

    private int GetCount(CategoryEntry category)
    {
        // Roll for no stock.
        if (Random.Shared.NextDouble() < category.ChanceForNoStock)
        {
            return 0;
        }

        return RandomCount(category.Base, category.Random);
    }

    // Root "Item" node - every item template descends from it, so an entry keyed here matches
    // anything the more specific checks missed.
    private const string RootItemNode = "54009119af1c881c07000029";

    // Walks item tpl -> immediate parent -> parent's parent -> ... -> root "Item" node, returning
    // the first configured entry that matches. Most specific wins: an exact item tpl beats a
    // shallow category, which beats a deeper (more general) one. Null = nothing in the chain is
    // configured, and the caller leaves the trade untouched.
    private CategoryEntry? ResolveEntry(MongoId itemTpl, TemplateItem template)
    {
        if (config.Categories.TryGetValue(itemTpl, out var entry))
        {
            return entry;
        }

        var itemDB = databaseService.GetItems();

        MongoId currentParent = template.Parent;
        int guard = 0;
        while (currentParent != "" && guard < 64)
        {
            if (config.Categories.TryGetValue(currentParent, out entry))
            {
                return entry;
            }

            if (currentParent == RootItemNode)
            {
                break;
            }

            if (!itemDB.TryGetValue(currentParent, out var parentNode))
            {
                break;
            }

            currentParent = parentNode.Parent;
            guard++;
        }

        return null;
    }

    private int RandomCount(int baseCount, int random)
    {
        return baseCount + (int)Math.Floor(Random.Shared.NextDouble() * random * 2) - random;
    }
}
