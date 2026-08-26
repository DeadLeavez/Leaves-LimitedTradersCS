# Leaves-LimitedTraders — Handoff

Last updated: 2026-07-15. Read `4.0 Mods\HANDOFF.md` first for project-wide rules.

---

## 1. What the mod does

Reduces trader stock counts (per-category, with a chance of zero stock) and optionally shuffles
items down loyalty tiers and removes purchase limits — for a more hardcore economy. Re-applies
on every trader resupply.

Single class, `src/LimitedTraders.cs` (~280 lines).

## 2. Lifecycle & load order (two interacting constraints)

`[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostDBModLoader + 2)]`,
implements **both `IOnLoad` and `IOnUpdate`**.

- **Singleton is required** — same double-construction pitfall as BarterEconomy (IOnLoad +
  IOnUpdate resolving as two instances; OnLoad sets up one, OnUpdate ticks the other, mod
  silently never runs). See BarterEconomy handoff §2.
- **Must run OnUpdate *after* BarterEconomy** (which is why it's at +2, after BarterEconomy's
  +1). BarterEconomy fully rebuilds a trader's assort from its own snapshot every update; if
  LimitedTraders ran first in the same 5-second tick, that rebuild would wipe the stock/loyalty
  changes. TypePriority orders IOnUpdate too (both mods register together), so it guarantees
  the sequence. Both mods watch the same `NextResupply` trigger, so they fire in the same tick.

`OnLoad` snapshots `originalTraders` via a deep clone. This stands in for TS's
`getPristineTraderAssort` (not public in SPT 4.0 — it's `protected` on TraderAssortHelper) and
is **essential for correctness**: price/loyalty adjustments must work from a fixed baseline, or
reading back the already-adjusted live value each tick would compound the multiplier/loyalty
drop forever. Safe to snapshot at OnLoad regardless of order vs. BarterEconomy — neither mod
touches trader data during OnLoad.

## 3. Per-trader update (`ModifyTrader`, runs on resupply)

For each money-trade item parented to "hideout" in the trader's assort:
- **Stock count** (`GetCount`): roll for no-stock (`chanceForNoStock`, per-category or default),
  else `base ± random`. Sets `Upd.StackObjectsCount`, clears `UnlimitedCount` (fixes the "a
  lot" display), optionally clears buy restrictions.
- **Price** (`AdjustPrice`): only money trades; multiplies the *pristine* price (from
  `originalTraders`) by the category's `priceAdjustment`. Skips items absent from the OnLoad
  snapshot (e.g. a barter copy BarterEconomy generated this same tick — nothing to adjust
  against).
- **Loyalty** (`LoyaltyMixup`): resets to pristine loyalty, then rolls up to three independent
  one-level demotions (`loyaltyMixupChance2/3/4`) so items can drift down tiers.

Then regenerates the trader's flea offers.

## 4. Robustness notes

Logs (doesn't crash) on assort items missing from the global item DB or with an empty parent —
these indicate another mod misbehaving, and the loop just skips them.

## 5. Files

| File | Role |
|---|---|
| `src/LimitedTraders.cs` | Everything. |
| `src/Config.cs` | `tradersToLimit`, per-category `{ base, random, chanceForNoStock, priceAdjustment }`, defaults, loyalty-mixup chances, `removeMaxBuyRestrictions`. |
| `config/config.jsonc` | The knobs. |
