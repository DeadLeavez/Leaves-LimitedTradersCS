using System.ComponentModel.DataAnnotations;
using SPTarkov.Server.Core.Models.Common;

namespace Leaves_LimitedTraders;

// Web config editor surface (see LimitedTradersConfigApi for the routes). Split out of the
// gameplay engine because it's a distinct concern - reading/replacing config, not applying it.
public partial class LimitedTraders
{
    // GET /limitedtraders/config -> { Schema, Current }.
    public LimitedTradersConfig CurrentConfig
    {
        get { return config; }
    }

    // POST /limitedtraders/config. Parse -> validate -> atomic swap -> rebuild the collections
    // EnsureLateSetup latches -> persist (comments kept).
    public Leaves.HttpUtils.ConfigApplyResult ApplyConfigJson(string newConfigJson)
    {
        LimitedTradersConfig? incomingConfig = Leaves.Utils.Instance.stringToJson<LimitedTradersConfig>(newConfigJson);
        if (incomingConfig == null)
        {
            return Leaves.HttpUtils.ConfigApplyResult.Failure("could not parse config");
        }

        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(incomingConfig, new ValidationContext(incomingConfig), errors, true))
        {
            var messages = new List<string>();
            foreach (ValidationResult error in errors)
            {
                messages.Add(error.ErrorMessage ?? "invalid value");
            }
            return Leaves.HttpUtils.ConfigApplyResult.Failure(messages.ToArray());
        }

        List<string> idProblems = Leaves.Utils.Instance.CheckAllowedIds(incomingConfig);
        if (idProblems.Count > 0)
        {
            return Leaves.HttpUtils.ConfigApplyResult.Failure(idProblems.ToArray());
        }

        Interlocked.Exchange(ref config, incomingConfig);
        RebuildLatchedCollections();
        saveConfig();

        return Leaves.HttpUtils.ConfigApplyResult.Success(
            "stock / price changes apply on each trader's next resupply; a trader-list change re-limits within one update tick");
    }

    // Re-derives from config the collections EnsureLateSetup builds. A no-op before that runs -
    // EnsureLateSetup then builds them from the current (edited) config instead.
    private void RebuildLatchedCollections()
    {
        if (!lateSetupDone)
        {
            return;
        }

        var traders = databaseService.GetTraders();
        lock (scheduleLock)
        {
            var ids = new List<MongoId>();
            var times = new List<long>();
            foreach (var traderID in config.TradersToLimit)
            {
                if (!traders.ContainsKey(traderID))
                {
                    continue;
                }
                ids.Add(traderID);
                times.Add(0);
            }
            tradersToUpdate = ids;
            tradersLastUpdate = times;
        }
    }
}
