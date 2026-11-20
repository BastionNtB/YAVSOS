# Changelog

## 1.3.1 (2026-11-20)

Increased future-proof compatibility! Smarter search!

### Mod compatibility

- **Increased AzuAutoStore compatibility.** Chest rules will keep working through more AzuAutoStore updates.
- **EpicLoot rarities from EpicLoot.** The rarity filter now picks up new EpicLoot rarities by itself.
- **Game updates.** Stashing and cleanup will keep working through more Valheim updates.

### Fixed

- **Category search.** Fixed search listing unrelated items, such as Forest items when searching "ores".
- **Very large mod packs.** Fixed the panel turning off in mod packs with more than 8,192 items.

## 1.3.0 (2026-11-08)

Templates! Full multiplayer rule editing! Storehouse mod support!

### Before you update

- **New stack key.** Moved the stack key from **middle mouse** to **Shift + middle mouse**, for greater compatibility with ZenSign's search. This move overwrites previous stack key bindings if it was set to its previous **middle mouse** default.

### New

- **Rule templates.** You can now save a chest's rule on the new **Templates** tab and apply it to any chest.
- **Server templates.** Admins can share templates with everyone on the server. You can use them or make a copy of them for yourself.
- **Edit chests in use.** You can now change a chest's rule while another player has it open.
- **Show or hide the panel.** You can now use the **YAVSOS** button below the chest window to hide the panel completely.

### Improved

- **Items tab sections.** Each ticked category gets its own section on the **Items** tab.
- **Bulk Never actions.** You can now mark a whole section as *Never* and clear it all with a single button. This will help with narrowing down categories to a few exceptions.
- **Right-click to step back.** You can now use right-click on an item in the **Items** tab to go in reverse order from left-clicking: *Never*, *Always*, then clear.
- **Lit chests show through walls.** Chests that are lit will get a marker you can see through walls.
- **Lit chests stay lit after you clear the search.** Clearing the search box no longer turns the lights off at once: the chests stay lit for your lit-chest time, which starts over when you close the chest, so you can still walk to them.
- **Chests in stacks.** When a chest is in a vertical stack, the results, tooltips and search will inform you of its place, such as "2 of 3 from the bottom".

### Mod compatibility

- **Storehouse mod compatibility.** A new Storehouse store will automatically take what it's built for without needing a rule. This also applies to other mods that add filtered containers the same way as the Storehouse mod does.
- **Filtered containers as categories.** When a mod adds a container (much like the Storehouse mod), those filtered chests automatically become categories that you can now use for other containers as well.
- **Zen.ModLib outlines.** With Zen.ModLib installed, lit chests get its outline instead of a marker.

### For server admins

- **Bigger reach.** You can now let stashing and cleanup reach up to 512 chests, and let one cleanup organize up to 256 of them.

### Fixed

- **Cleanup in large bases.** Fixed cleanup getting stuck on the same nearby chests in a large base, so items further away never got cleaned up unless you walked closer. Cleanup will now start with the chests holding items that don't belong, and pressing it again will reach the rest.
- **No more false "no room".** Fixed a rare case where a chest refused an item: stashing and cleanup now pick another chest instead of falsely saying there's "no room".
- **Clear message when categories fail.** Fixed an issue when the server couldn't build the categories: players now see a message saying so instead of "still loading". The server will also retry every few minutes.

## 1.2.2 (2026-11-07)

Your favorites stay in your inventory!

### Mod compatibility

- **Favorites stay put.** YAVSOS's stash and stack keys will now leave your favorites from AzuExtendedPlayerInventory, Quick Stack Store and ItemFavoriteFramework in your inventory, whether you favorited the stack or its slot. With AzuAutoStore installed, AzuAutoStore keeps its own favorites, as before.
- **Why a favorite stays.** Pressing the stack key on a favorite will now tell you why it stays, such as "Wood stays: it's a favorite in Quick Stack Store." The item tooltip says so too.
- **Unreadable favorites.** If YAVSOS can't read a favorites mod, stashing will still work and treat those items like any other. The result panel will name that mod once per game session.

## 1.2.1 (2026-11-05)

Categories load on busy servers!

### Fixed

- **Categories stuck loading.** Fixed categories staying "still loading" for good on a busy server, which left chests taking only their *Always* items and what they already held.
- **Retry when loading fails.** Fixed the server waiting for a restart after loading the categories failed. It will now try again every few minutes, and the panel will say so.

## 1.2.0 (2026-11-04)

Undo! Rule suggestions! See exactly where your items went!

### New

- **Undo and redo.** You can now click ↶ or press **Ctrl + Z** to undo your last change to a chest's rule, even a **Paste** or **Clear rule**. Do it again to redo.
- **Rule suggestions.** You can now set a rule from what a chest already holds. The panel will suggest one, such as "Set rule: Metals", and ✕ hides it.

### Improved

- **Where your items went.** The result panel will now list every chest or drawer that got items, and what went in. This works with AzuAutoStore's stash key too.
- **Search names the matches.** When a search matches several items, a lit chest will now name the ones it accepts, such as "Accepts Copper, Tin".
- **Panel messages.** Moved the panel's messages into one strip under its title, so you can see why a change wasn't made.
- **Category names.** Renamed two categories: *Moulds* is now *Molds*, and *Epic Loot* is now *EpicLoot*.

### For server admins

- **Add or remove items from categories.** You can now add an item to a category, or remove it from one, with `<add>` and `<remove>` lines in the categories file. The item will keep its other categories.

### Fixed

- **Cleanup stutter.** Fixed cleanup stuttering on its first press after you start the game, and pausing in large bases.
- **Storehouse armor stands.** Fixed stashing and cleanup reporting items as unconfirmed when they went onto storage that sorts them into its own slots, such as Storehouse's armor stands.

## 1.1.0 (2026-11-02)

Search that lights up your base! Glowing chests! Copy and paste rules!

### New

- **Search lights up chests.** When you search in the YAVSOS panel, chests holding a match will now glow gold. Turn on the toggle beside the search bar to also light, in blue, the chests whose rule takes it.
- **Arrival glow.** Chests will now glow and give off a small puff when stashing or cleanup puts items in them. Choose the effect, or none, with the `ChestArrivalEffect` setting.
- **See what stayed.** After a cleanup, a chest that kept items no chest takes or has room for will now glow red-orange. Point at it to see what stayed and why.
- **Copy and paste rules.** You can now use **Copy** and **Paste** beside **Clear rule** to copy a chest's whole rule to another chest. Pasting replaces that chest's rule.
- **Fold the panel.** You can now use the arrow left of the panel's title to fold the panel down to its title, status marks and chest label. It will stay folded on every chest until you unfold it.

### Improved

- **Glow colors.** You can now change the gold, blue and red-orange glows with the `HoldsColor`, `TakesColor` and `StaysColor` settings.
- **Result panel closes itself.** The result panel will now close 10 seconds after a stash or cleanup, even with your inventory open. Change how long with `ResultPanelSeconds`.
- **Tooltips say why.** The "Stash stack" line in item tooltips will now say why the item goes to that chest, such as "(counts as Metals)".
- **Skipped chests explained.** When stashing or cleanup skips another player's chest, the result panel will now give each reason on its own line.
- **Panel hover hints.** Pointing at the Rule heading will now show the chest's label as other players see it, and the YAVSOS title will show the mod's full name.

### Mod compatibility

- **AzuAutoStore results.** With AzuAutoStore installed, its store key will now show YAVSOS's result panel, listing what it stored and where.
- **AzuAutoStore cleanup key.** With AzuAutoStore installed, the cleanup key will now use the modifier from the Cleanup `Shortcut` setting instead of always **Ctrl**. Set `Shortcut` to None to turn the cleanup key off.
- **Shared chests without YAVSOS.** On a server without YAVSOS, stashing and cleanup can now put items in a chest another player has open or stands near, as long as that player has AzuAutoStore or MultiUserChest.

### Fixed

- **Tankards and mead horns.** Fixed tankards and mead horns going to chests that take *Swords*. They now count as *Equipment*.
- **Cleanup tooltip.** Fixed the cleanup button's tooltip naming another player's chest that cleanup then skipped.
- **Leftover tooltip.** Fixed an item's tooltip staying over the empty slot after you stashed the whole stack with **middle mouse**.

## 1.0.2 (2026-10-31)

Magic items stay safe!

### Fixed

- **Vanishing magic items.** Fixed items with extra data, such as EpicLoot magic items, vanishing when a player with AzuAutoStore but not YAVSOS took them from your chest on a server without either mod. Those items will now stay in the chest.

## 1.0.1 (2026-10-30)

Clearer warnings! Fewer skipped chests!

### Before you update

- **Update EpicLoot on the server.** EpicLoot 0.15.2 and later put its belt and rings in their categories itself, so YAVSOS no longer does. With an older EpicLoot on the server, those items won't have a category.

### New

- **Display mark.** A new Display status mark will turn amber when tooltips, labels, search or the result panel turn off after an error, and say which.

### Improved

- **Clearer warnings.** YAVSOS will now tell you when something can't work and why, such as cleanup being off because AzuAutoStore's exclusion list can't be read, or a rule too large to save.
- **Warning marks stand out.** Amber status marks will now show "!" instead of a check mark, so they stand apart from the green ones.

### Fixed

- **Chests with unmovable items.** Fixed stashing and cleanup skipping a whole chest because it held one item YAVSOS can't move. That item will now stay put, and the rest of the chest will be handled as usual.
- **Keys with the inventory open.** Fixed the cleanup and store keys doing nothing while your inventory is open. They will now tell you to close it first.
- **Item grid and filters.** Fixed the item grid marking different items as taken than stashing and cleanup would move, when a rule uses a filter such as Damaged or Crafted by.
- **Split stacks.** Fixed the stash result counting a split stack more than once.
- **Left-alone chests.** Fixed the stash result saying "rules changed" for a chest set to be left alone during cleanup. It will now say the chest is left alone.
- **AzuAutoStore notice.** Fixed AzuAutoStore's exclusion notice counting creature attacks, such as "Lox bite", as items.
- **Read-only config folder.** Fixed YAVSOS failing to load when its config folder is read-only.

## 1.0.0 (2026-10-28)

First public release! Chest rules! Automatic categories! One-key cleanup!

### New

- **Chest rules.** Set what each chest takes in a panel beside the open chest: categories, *Always* and *Never* items, filters and a priority.
- **Automatic categories.** Every item gets its categories automatically, including items other mods add.
- **Stashing.** Stash items through AzuAutoStore when it's installed, or with YAVSOS's own stash when it isn't.
- **Cleanup.** One key moves items to the chest whose rule fits them best.
- **Find your items.** Hover labels, item tooltips and a search that lights up the chests taking an item.
- **Play anywhere.** Works on any server, in single player, and when you join a server without YAVSOS.

### Mod compatibility

- **Works with other mods.** Works with MultiUserChest, EpicLoot and the other mods the player guide lists under "Other mods". Each one is checked when the game starts and turns off on its own if it doesn't fit.
