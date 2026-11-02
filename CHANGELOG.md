# Changelog

## 1.1.0 (2026-11-02)

### Changed

- With AzuAutoStore installed, the cleanup key is now the modifier from the Cleanup `Shortcut` setting (Ctrl by default) plus AzuAutoStore's store key, instead of always Ctrl. Set `Shortcut` to None to turn the cleanup key off.
- Chests now glow and give off a small puff when YAVSOS's stash or cleanup puts items in them (AzuAutoStore's store key keeps its own effect). Choose the effect, or None, with `ChestArrivalEffect`, and set how long chests stay lit with `HighlightSecondsAfterClose`.
- When you search in the YAVSOS panel, chests holding a matching item glow gold, and with the toggle right of the search bar on, other chests whose rule takes it glow blue. Point at a lit chest to see why, such as "holds Copper ×57".
- After a cleanup, a chest glows red-orange if it kept items that no chest takes or that only full chests take. Point at it to see what stayed and why.
- Change the gold, blue and red-orange glows with the `HoldsColor`, `TakesColor` and `StaysColor` settings, which have a colour picker in the configuration manager.
- New **Copy** and **Paste** buttons beside **Clear rule** copy a chest's rule to another chest: its categories, Always and Never items, filters and priority. Paste replaces the other chest's rule.
- A new arrow left of the panel's title folds the panel down to its title, status marks and the chest's label. The panel stays folded on every chest, even after you restart the game, until you unfold it.
- With AzuAutoStore installed, pressing its store key now shows YAVSOS's result panel, listing what AzuAutoStore stored and where, including other players' chests and RossItemDrawers drawers.
- The result panel now closes by itself 10 seconds after a stash or cleanup, even with your inventory open. Set how long it stays up with `ResultPanelSeconds`, or turn on `ResultPanelHoldInInventory` to keep it up while your inventory is open.
- The "Stash stack" line in item tooltips now says why the item goes to that chest, such as "(counts as Metals)". The line appears as soon as the tooltip opens and updates as you walk.
- On a server without YAVSOS, stashing and cleanup can now put items in a chest another player has open or is standing near, if that player has AzuAutoStore or MultiUserChest. Before, that player needed YAVSOS too.
- When stashing or cleanup skips another player's chest, the result panel gives each reason on its own line, such as "no MultiUserChest answered".
- Pointing at the panel's Rule heading shows the chest's label as other players see it. Pointing at the YAVSOS title shows the mod's full name.

### Fixed

- Tankards and mead horns now count as Equipment (under Equipment · Other) instead of Swords, so stashing and cleanup no longer send them to a chest that takes Swords.
- The cleanup button's tooltip no longer names another player's chest that cleanup then skips.
- When middle-clicking stores a whole stack, its tooltip now closes instead of staying over the empty slot. (With AzuAutoStore installed, its single-item key works as before.)

## 1.0.2 (2026-10-31)

### Fixed

- Items with extra data, such as EpicLoot magic items or items spawned with devcommands, no longer vanish when a player with AzuAutoStore but not YAVSOS takes them from your chest, on a server with neither mod. Those items now stay in the chest, and ordinary items move as before.

## 1.0.1 (2026-10-30)

### Fixed

- A chest holding an item YAVSOS can't move, such as an unusual item from another mod, is no longer skipped by stashing and cleanup. That item stays put and the rest of the chest is handled as usual.
- Pressing the cleanup or store key with your inventory open now tells you to close it first, instead of doing nothing.
- The item grid marks the same items as taken that stashing and cleanup would put in the chest when a rule uses a filter such as Damaged or Crafted by.
- AzuAutoStore's exclusion notice no longer counts creature attacks, such as "Lox bite", as items.
- The stash result counts a split stack once, and says when a chest is set to be left alone during cleanup instead of "rules changed".
- YAVSOS loads even when its config folder is read-only.

### Changed

- Amber status marks show "!" instead of a check mark, so they stand apart from the green ones.
- A new Display status mark turns amber when tooltips, labels, search or the result panel turn off after an error, and says which.
- YAVSOS tells you more often when something can't work:
  - The Clean up mark says when cleanup is off because AzuAutoStore's exclusion list can't be read.
  - Cleanup's "AzuAutoStore's config excludes it" names the chest type, so you know which chest to replace.
  - The Categories mark says when the server's category settings can't be read, or when it still shows the categories from before the last change.
  - Opening a chest shows a message when the chest panel is off or chests can't be read in this game version.
  - The stash result counts nearby chests it couldn't read.
  - A rule that can't be saved says why: it's too large, or it uses a category that no longer exists.
- Dedicated servers: EpicLoot 0.15.2 and later put its belt and rings in their categories itself, so YAVSOS no longer does. With an older EpicLoot those three items have no category; update EpicLoot to fix this.

## 1.0.0 (2026-10-28)

First public release.

- Chest rules: categories, Always here / Never here items, filters and a priority, set in a panel beside the open chest.
- Automatic categories for every item, including items other mods add.
- Stashing through AzuAutoStore when it's installed, and YAVSOS's own stash when it isn't.
- Cleanup: one command moves items to the chest whose rule fits them best.
- Hover labels, item tooltips and search that lights up the chests taking an item.
- Dedicated servers, Steam-hosted worlds, single-player, and joining a server without YAVSOS.
- Works with MultiUserChest, EpicLoot and the other mods the guide lists under "Other mods"; each integration is checked when the game starts and turns off alone if it doesn't fit.
