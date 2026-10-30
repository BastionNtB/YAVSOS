# Changelog

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
