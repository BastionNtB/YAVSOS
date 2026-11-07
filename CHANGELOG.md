# Changelog

## 1.2.2 (2026-11-07)

### Changed

- Without AzuAutoStore, YAVSOS's stash key and stack key now skip favorites from AzuExtendedPlayerInventory, Quick Stack Store and ItemFavoriteFramework, so stacks you favorited and anything in a favorited slot stay in your inventory. Unfavorite a stack to stash it. (With AzuAutoStore installed, AzuAutoStore stashes and keeps its own favorites, as before.)
- Pressing the stack key on a favorite now says why it stays, such as "Wood stays: it's a favorite in Quick Stack Store. Unfavorite it to stash it." instead of "YAVSOS can't stash this stack." The item tooltip now says "stays in your inventory (Quick Stack Store favorite)" instead of "(protected or held)".
- If YAVSOS can't read one of those mods' favorites, stashing still works and stashes them like other items. The result panel names that mod once per game session.

## 1.2.1 (2026-11-05)

### Fixed

- A busy dedicated server no longer runs out of time while it loads categories, which could leave them "still loading" for good and chests taking only their Always items and items they already held. Update YAVSOS on the server to get the fix; players can keep 1.2.0.
- If loading the categories fails, the server now tries again after a minute, then at longer intervals, up to 5 minutes apart. Before, it waited for a restart, which could fail the same way.

### Changed

- When YAVSOS can't build the categories, the panel's message now ends "YAVSOS tries again every few minutes."

## 1.2.0 (2026-11-04)

### Changed

- Click ↶ beside Copy, or press Ctrl + Z, to undo your last change to a chest's rule, including Paste and Clear rule. Do it again to redo. Each chest keeps its last change until you leave the world.
- The panel now suggests a rule from what the chest holds: "Set rule: Metals" on a chest without a rule where most stacks share a category, and "Always: these items" on a chest holding items its rule doesn't accept. Click ✕ to hide them on that chest until they change.
- The panel now shows its messages in one strip under its title, such as why a change wasn't made or that another player is using the chest. A refused control is outlined in amber, and when there's no message the strip shows the chest label.
- The result panel now lists each chest or drawer that got items and what went in, then "+N more places" when they don't all fit. This works for YAVSOS's stash and cleanup and for AzuAutoStore's stash key.
- When a search matches several items, a chest lit because its rule accepts some of them now names them, such as "Accepts Copper, Tin".
- Changed wording throughout the documentation and interface to be more consistent. Two category names were changed: "Moulds" is now "Molds", and "Epic Loot" is now "EpicLoot".
- Server admins and modders can now add an item to a category, or remove it from one, with `<add>` and `<remove>` lines in the categories file, and the item keeps its other categories. Every player then needs YAVSOS 1.2: on an older version, categories in rules accept nothing until they update, and the server log names them.

### Fixed

- Cleanup no longer stutters on its first press after you start the game, and pauses less in large bases.
- Items that YAVSOS's stash or cleanup puts in storage that sorts them into its own slots, such as Storehouse's armor stands, now count as moved instead of unconfirmed.

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

## 1.2.2 changes not in the store text (2026-11-07, D151)

- **Client log:** at startup, one line per favorites mod YAVSOS reads ("Stash keeps … favorites in your inventory."), or a warning saying why it can't.
- **Lab fixtures:** `StashLab.Favorites` (what the readers find), `StashLab.Favorite` (mark or unmark through a mod's own API) and `StashLab.FavoritesFault` (every reader throws, D088).

## 1.2.1 hotfix changes not in the store text (2026-11-05, D144)

- **Server and client log:** a loading stage that keeps restarting without finishing is named, with its count and last cause, after 60 s and 3 discarded captures, then after 1, 2, 4, 8 and 16 minutes and every 30 minutes; an info line when it finishes. The "Loaded catalog" and "Fresh loaded sources" lines now give capture work, capture elapsed and build time. Each failure logs one warning, also after its count restarted.
- **Bug report template:** a field for the server's log next to the client's.
