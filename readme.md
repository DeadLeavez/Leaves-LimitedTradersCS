# Limited Traders
Reduces the stock of all traders, with optional per-category price adjustments.

This mod will make traders have a much more limited stock of items available for purchase. I use this for more hardcore economy runs.

## 1.0.0
- Release

## 1.1.0
- Update to 3.8.0

## 1.2.0
- Added ``chanceForNoStock`` to config. Which adds a chance for an item to not be in stock at all, regardless of rolls.
- Made the mod track trader updates properly

## 1.2.1
- Added ``priceAdjustment`` to config. It can be left out and it'll default to no change.

Previous config files ARE compatible.

## 1.3.0

- Updated to 3.9.0
- renamed folders and names to follow package.json standards

## 1.3.1
- Updated to 3.10.0
- fixed slow degeneration of prices and loyalty levels.

## 1.3.2
- Added nickname to server message
- Fixed flea not being updated with new prices and limits

## 1.3.3
- Update to 3.11 (still works on 3.10)

## 1.4.0
- Update to SPT 4.0
- Removed the loyalty-tier drop system (`loyaltyMixup`); out of scope for this mod
- Config is now rewritten with fresh comments on load

## 1.5.0
- `categories` entries can now be keyed by an individual item tpl as well as a category, and the
  category match walks the whole parent chain (parent, parent's parent, ...) instead of only the
  immediate parent.
- When an item matches more than one entry the most specific one wins:
  item tpl > nearest category > ... > root "Item" node.
- Removed the `default` config key. For a catch-all, add a `categories` entry keyed by the root
  "Item" node `54009119af1c881c07000029`. An item that matches nothing is now left untouched.
- Migrating: rename your old `default` block to a `categories` entry under
  `"54009119af1c881c07000029"`.
- `categories` keys are now checked on startup: a key that isn't a category or a real item is
  logged (it just won't match anything), and the web config editor refuses to save one.