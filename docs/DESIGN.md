# YAVSOS design

This document explains why YAVSOS works the way it does. Each section gives the current
answer, the reason for it, and the main alternative that was considered. Sections end with
the IDs of the decisions they come from (for example "Decisions: D042, D058"). The IDs come
from the project's decision log and are kept so later changes can be traced; they are plain
labels, not links.

For how to install and use YAVSOS, see the player guide (the README).

## 1. Goals and the four pillars

YAVSOS gives chests rules and puts items where the rules say. A rule names categories, exact
items and filters, and a chest has a priority. The store key moves items from the player's
inventory into nearby chests. "Clean up nearby" moves items between nearby chests so each one
ends up where it belongs.

Four priorities guide every change:

- **Efficiency.** Work happens when its inputs change, not every frame. Categories are built
  when items, recipes or the category settings change. Every scan and run has a bound: one
  stash or cleanup looks at the nearest 256 chests at most.
- **Compatibility.** YAVSOS runs in large mod packs. It works with the mods players already
  use instead of replacing them (section 2), and it checks what it relies on instead of
  trusting version numbers (section 3).
- **Extensibility.** Classification, rules, ranking and move planning live in a core library
  (`YAVSOS.Core.dll`) with no game, Unity, BepInEx or optional-mod dependencies. It is tested
  against plain descriptions of items and chests. Game adapters, the interface and networking
  live in the plugin.
- **Multiplayer safety.** Every feature that changes a chest, an item, a rule or a shared
  setting defines its authority, access checks, concurrency, timeouts and item conservation
  from its first design (sections 6 and 7).

What YAVSOS is not:

- Not a replacement stasher when AzuAutoStore is installed. AzuAutoStore keeps doing the
  stashing; YAVSOS tells it which chest each item belongs in.
- Not a replacement chest screen. The rule panel sits beside the game's own chest window.
- Not an item database for other mods. Its categories serve storage (section 10).

YAVSOS is licensed GPL-3.0-or-later. The bundled ServerSync and MultiUserChest code is MIT
and ships with its notices.

Decisions: D042, D082.

## 2. Compatibility first

**AzuAutoStore does the stashing.** When AzuAutoStore is installed, its store key,
single-item key and ground pickup stay in charge. YAVSOS hooks the check AzuAutoStore makes
before storing into a chest. On a chest with a rule, the rule replaces AzuAutoStore's "the
chest must already hold this item" check. YAVSOS also runs AzuAutoStore's store key item by
item in YAVSOS's chest order (section 5). Every item still goes in through AzuAutoStore's own
store call, so its messages, skip lists, exclusions and multiplayer handling are unchanged.
Without AzuAutoStore, YAVSOS stashes itself, with the same order.

Why: players already know and trust AzuAutoStore. A storage mod that replaces it forces a
choice between the two. The alternative, a YAVSOS stasher that is always active, was
rejected for that reason.

**A partner's behaviour is accepted.** A job handed to another mod follows that mod's model,
and a fault inside it is that mod's to fix. YAVSOS documents such behaviour as inherited
instead of rebuilding around it. Two examples:

- AzuAutoStore's pause key stops only its ground pickup; its own store key ignores the pause.
  YAVSOS does the same: cleanup and the ordered store key ignore the pause, and YAVSOS's
  ground-pickup choice respects it.
- A setting that belongs to a partner stays there. With AzuAutoStore installed, YAVSOS's
  `Stash` settings are shown read-only, with a note that AzuAutoStore controls them. Their
  values are kept for when AzuAutoStore is removed.

**Upstream bugs get small, removable patches.** Each workaround for another mod's bug is an
isolated patch that records when it can be removed. Examples:

- RossItemDrawers: an empty drawer reserved for an item holds nothing, so AzuAutoStore's
  "must already hold the item" check skips it. YAVSOS answers yes for the drawer's own item;
  the drawer mod still decides what lands. It can go when either mod handles reserved drawers
  itself.
- EpicLoot: on a dedicated server, EpicLoot did not register its own crafted items (a belt and
  two rings), so they had no categories. YAVSOS calls EpicLoot's own loader once. When
  EpicLoot registers them itself, YAVSOS logs that the patch is not needed and does nothing.

**Bundled code stays close to upstream.** ServerSync and MultiUserChest are bundled inside
`YAVSOS.dll` at pinned versions. Both are kept as close to upstream as possible, so players
get the same behaviour as from the standalone mods. When upstream already tracks a needed
change, YAVSOS waits for upstream's fix instead of patching its copy. The one documented
change to the MultiUserChest copy is its per-player item format (section 6). The bundled
MultiUserChest steps aside when the standalone mod or AzuAutoStore's copy is installed.

Decisions: D030, D042, D043, D047, D061, D070, D079, D090, D097.

## 3. Capability checks, not version locks

**Nothing is switched off because a version number changed.** A version difference is not
evidence that something broke. YAVSOS checks the things it actually uses:

- **Shape:** the types, members and signatures it calls or reads.
- **IL call graph:** for example, that AzuAutoStore's store-key loop makes the calls YAVSOS's
  ordering replaces, and no others.
- **Round trip:** the inventory reader saves and loads sample items, including other mods'
  item data, and checks that nothing changed.

When a check fails, only the feature that depends on it turns off. The rest keeps working.
A version-specific block is allowed only as a documented exception for a known critical bug,
with its removal condition. YAVSOS's own client/server compatibility version is a separate
matter (section 8).

**Which objects can have rules is detected too.** Any player-built container that uses the
game's own container component can have a rule, at any size, including modded chests, carts
and ships. Tombstones, containers on a creature, world-spawned containers (such as dungeon
chests) and RossItemDrawers drawers are excluded; a drawer's own item reservation already acts
as its rule. The alternative, a list of supported chests, would miss every new modded chest.

**The runtime backstop refuses one write, not the mod.** If AzuAutoStore writes into a ruled
chest by a path the rule did not approve, YAVSOS refuses that write and the item stays with
the player. It logs once per path and keeps the integration running. Only the startup checks
turn an integration off. Disabling the integration on the first surprise was rejected: one
unusual path would make the whole mod useless.

**A per-item round trip guards moves into another player's chest.** Such an item travels in
the game's save format. Each one is saved and loaded before it moves. A durability change of
one save step (the game's own rounding) passes; any other difference keeps the item where it
is. Dropping the guard would lose mod data silently.

**Nothing fails silently.** Every failure or disabled feature is stated somewhere. What a
player needs to know is shown in the game: the panel, its status marks or the result panel.
What an admin should look into goes to the log, naming the item, mod or chest. A failing
optional piece disables only itself:

- one item whose data can't be read lands in Misc alone, named in the log;
- a stack that can't be captured safely is skipped and the rest of the inventory is stashed;
- if a category build step still fails after 3 tries, 3 seconds apart, the Categories mark
  says so, and exact items keep working.

The status marks at the top right of the panel use four states: a tick (working), amber
("This chest:" a caution about the open chest, or "Partly working:" one optional part is
off), a cross ("Not working:") and a grey dash ("Off:" a setting turned it off). A mod that
is not installed gets no mark.

Decisions: D045, D046, D058, D068, D086, D087.

## 4. Categories are automatic

**Membership comes from evidence.** Each item's categories come from what the game and mods
have loaded: the item's own properties, the recipes that make it, what uses it (stations,
fermenters, tame creatures' food) and structured mod data read by small optional adapters.
A new item from any mod gets categories without anyone tagging it. Runtime collects only
this ordinary evidence; it does not trace where items come from in the world.

A manual definition is allowed only where the available evidence cannot establish the
intended meaning. Each one records what was checked, the version, and what would let an
automatic rule replace it. A longer exact list, moving a list into configuration, or guessing
from item names is not a substitute.

**An item can belong to several categories.** Wood is a building material and a fuel. Rules
match through every membership. One primary category is chosen for display only, so showing
an item somewhere never removes its other purposes.

**Umbrellas have complete subcategories.** Every family has an umbrella (Weapons over Swords,
Axes, Bows…). The umbrella holds everything in its subcategories. A computed "Other"
subcategory holds members that fit no named one, so the subcategories together cover the
umbrella. Ticking an umbrella equals ticking all its subcategories. Both properties are
computed, not listed by hand. The taxonomy keeps growing as new mods and useful distinctions
appear; a category ID stays the same while its meaning stays compatible.

**The admin's categories file replaces, not adds.** `com.bastionntb.yavsos.categories.xml`
in the config folder can add categories and assign items. An assignment replaces that item's
automatic categories, so no unexpected automatic membership remains. YAVSOS creates the file
empty and never overwrites it. A saved change applies without a restart; a file that can't be
read keeps the last version that worked in use, and the log says why. There is one file for
now; reading several (one per mod or pack) is later work.

**The server's answer wins, per item.** On a server with YAVSOS, the server classifies and
sends its answer for each item. Clients don't need an identical set of loaded items: an item
the server didn't classify falls back to Misc, which players can still give a chest. A value
a newer server sends that this client doesn't know is kept and matches nothing, instead of
refusing every answer. The alternative, refusing the whole set when client and server
catalogs differ, cost players every category rule over one mismatched item.

**Answers survive a rebuild.** When mods register items or change recipes mid-session, the
last good answers stay in use until the new ones are ready. An item whose category really
changed may briefly go to its old chest; nothing is lost, and cleanup can move it later.

Decisions: D001, D015, D020, D028, D029, D038, D049, D051, D081, D086, D094.

## 5. Rules and ranking

**What a rule holds.** A chest's rule lists categories, exact items marked Always or Never,
and filters. Filters judge either the item (food's main stat, eaten by a tame, can go
through portals, item origin, used for, can be upgraded) or the actual stack (upgrade level,
damaged, EpicLoot rarity, crafted by). Never always wins. Always accepts the item even when
no ticked category or filter does. The editor offers only the filters and values that the
ticked categories' items can have. A stack filter that can't read its evidence at that moment
rejects the stack, so a "plain" rarity rule never takes an item it could not check.

**One ranking.** When several chests accept an item, they are ranked in this order:

1. ordinary chests before overflow chests (a stored overflow flag is kept; there is no control
   for it);
2. higher priority first;
3. a closer kind of match: an exact item before a filtered category before a plain category;
4. the more specific category (a subcategory before its umbrella);
5. the smaller category (Metals before Crafting materials, which holds far more items);
6. the chest's ID, so every player computes the same order.

Players see priority as levels 1 to 10, 1 first and 5 (Normal) the default. The first
design compared specificity before priority. It was changed because a player who sets a
priority means it to win.

**One placement decision.** The store key (AzuAutoStore's or YAVSOS's) and "Clean up nearby"
ask the same question: where does this item belong? They use the same order, so after a
stash, cleanup finds nothing to move. Always means "accepted here, ranked above categories at
the same priority"; it does not mean "kept here".

Cleanup moves three kinds of stack:

- a stack the chest's rule rejects;
- a stack in a container without a rule (chests, carts and ships the players built), when a
  ruled chest accepts it;
- a stack a ruled chest accepts, when another chest ranks strictly higher for it.

Equally ranked chests never trade items. Cleanup never moves items into containers without a
rule; the stash does fill an unruled chest that already holds the item. A chest can opt out
with "Don't clean up this chest".

**Rules survive change.** A rule naming a category, filter or filter value this version
doesn't know keeps that part unchanged. The unknown part matches nothing, so the rule can only
take less, never more, and it works fully again when the part returns. A rule stored in a
format this version can't read is kept untouched and takes nothing. Before categories are
ready (just after joining, or during a rebuild with nothing to keep), a rule's exact items
already work; only its category part waits.

**Rules apply at once.** Every change in the panel is saved immediately; there is no Save
button. Unticking the last part removes the rule, and Clear rule removes every part,
priority included.

**Who may edit.** Editing a rule uses the game's own access model, the same check as opening
the chest: a private chest is its builder's alone, and inside an active ward only the ward's
permitted players may edit. No separate builder or admin check is added. The write is made
by the player whose game owns the chest's network object, and only if the rule is still the
one the player opened. While another player has the chest open, editing is refused.

Decisions: D002, D048, D050, D051, D054, D057, D061, D064, D065, D069, D070, D072, D077, D086,
D092.

## 6. Moving items safely

These rules cover the moves YAVSOS makes itself: cleanup, and the stash without
AzuAutoStore.

**One writer per chest.** A move is made by the player's own game, and only while it owns
every chest involved (the game's network ownership). There is no server coordinator. An
earlier design added server reservations and permits on top of ownership; it was dropped
because ownership, rechecked at the write, already excludes a second writer, and the extra
coordinator added cost without changing that.

**Strict identity and explicit cells.** Plans are made from detached snapshots. At the write,
the stack must still match its planned identity exactly, including crafter, durability and
other mods' item data. Each move names its destination cell. Stackable items merge the way
the game merges them (its own "same type" check), so a mod that changes that check is
followed automatically.

**Check at the write, verify after.** Just before each write YAVSOS checks again: access,
range, ownership, protected slots, identity, room and the rule. Afterwards it checks that
the source lost exactly what the destination gained and nothing else changed. Each result is
moved, remaining, refused or unconfirmed.

**Never retry, replay or refund.** A move whose outcome is uncertain is reported as
unconfirmed and logged. YAVSOS never repeats it, gives items back or "corrects" inventories
from counts, because any of those can duplicate items. An earlier design kept journals and
save checkpoints to recover from a crash between the separate character and world saves. It
was removed: vanilla has the same exposure, and the design could lock a character out of the
feature after one rare crash. Such a crash can still duplicate or lose the moved stack,
exactly as with a vanilla move.

**Another player's chest goes through MultiUserChest.** A chest another player's game owns
is reached through MultiUserChest's request model, from the first copy found: the standalone
mod, AzuAutoStore's copy, then YAVSOS's bundled copy. That copy's transfer model is inherited:
AzuAutoStore's copy returns the item after 30 seconds without an answer; upstream
MultiUserChest and YAVSOS's copy do not. On its own side, YAVSOS checks access before sending
(the receiving side does not), counts the move only once the item is seen arriving, and says
so when it isn't. Because arrival is judged by count, a returned request can read as moved
if the other player adds the same item to that chest during the wait; items are conserved,
only the report is wrong. A hook that reports each request's answer has been proposed
upstream.

MultiUserChest copies differ in one field of the item data they send. Every YAVSOS player
publishes which format their copy uses, and YAVSOS's copy reads and writes each request in
the other player's format. When the format is unknown, the move is refused and the item
stays put. Requiring everyone to match one copy was rejected: the server is rarely part of
these requests, and it would block players over a fixable difference.

**No waiting in line.** Only a move that conflicts with a pending request (the same other
player's chest, or the same item) waits for it. Every other move, including all moves into
the player's own chests, goes ahead at once.

**Proportionate safety.** YAVSOS prevents loss and duplication on its own side only through
measures that keep the mod usable. A mechanism that can effectively disable the mod for a
player is not acceptable. When a new risk is found, it is weighed with concrete examples
before a feature is restricted.

**Protected slots.** Equipped and quest items are never moved. The hotbar and
AzuExtendedPlayerInventory's quick slots are kept by default; settings can include them.
With AzuAutoStore installed, its own settings decide.

Decisions: D004 (as amended), D005, D041, D042, D043, D057, D061, D065, D083, D093.

## 7. Server, hosted and client-only

**Shared settings come from the server.** The `Server` settings and the categories file are
synchronised from the server through ServerSync, bundled at a pinned version. While
`Server/LockSharedPolicy` is on, only admins can change them. A client's change request is
accepted only from the current connection and only for shared entries, then ServerSync's
admin and lock rules decide. While a server's values are in force, a player's own config
file keeps the player's own values, so leaving the server restores them.

**Configuration sync grants no chest authority.** Synchronised settings say what players may
do; they never let anyone write to a chest. Every move still needs the game's own access and
ownership (section 6).

**Modes are detected, not chosen.**

| Situation | Behaviour |
| --- | --- |
| Single-player, or the host of a Steam-hosted world | The game is its own server and classifies locally |
| Server and client both have YAVSOS | The server's categories and shared settings apply |
| Server without YAVSOS, client with it | The client runs on its own categories and settings, `Server` settings included |
| Server with YAVSOS, client without it (or too old) | The client can't join |

A server with YAVSOS requires it from joining players. A client doesn't require it from the
server, so it announces its own version during the connection. A server with YAVSOS
identifies itself during the handshake; if the handshake completes without that, the client
knows the server lacks YAVSOS. Until the answer is known, rules do nothing, and no timeout
turns waiting into local authority. An earlier design had a client setting for this; it was
removed because players gain nothing from deciding it.

On a server without YAVSOS, each YAVSOS player uses their own categories. If two players'
categories differ, their stashes may place an item differently; nothing is lost. Moves into
other players' chests still go through MultiUserChest, which needs no server mod.

**Limits are the server's.** `Server/MaximumRange` (default 50 m) caps the range players set
for cleanup and for YAVSOS's own stash. `Server/MaximumChests` (default 256, 16 to 256) is how
many of the nearest chests one cleanup or YAVSOS stash handles; the result says when more
were in range. Cleanup also takes items out of at most the 64 nearest chests that hold any.
With AzuAutoStore installed, its store key uses its own range and finds chests itself, so
both settings then limit cleanup only. Raising the bound to 512 was considered and not
chosen: more planning cost per press, with no need reported. `Server/AllowStashing` and
`Server/AllowCleanup` turn the two actions off.

Decisions: D008, D009, D010, D011, D044, D074, D078 (B06), D086, D095.

## 8. Versions and data formats

**Two kinds of version.** Game and mod versions never gate a feature (section 3). YAVSOS's
own compatibility version protects YAVSOS's network format: a player older than the server's
minimum is told to update when joining. The minimum is 1.0.0. It is raised only for a real
format break, not for every release.

A difference in classifier version between client and server does not reject the server's
answers; the versions stay in the log. The category settings and taxonomy must still match.
Example: a server on a later release fixes one item's category, and a client one update behind
keeps working with the corrected answer.

**Stable names.** The plugin ID `com.bastionntb.yavsos` and assembly `YAVSOS.dll` stay the
same. Everything YAVSOS stores or sends is named under that ID:

- on a chest: its rule (`com.bastionntb.yavsos.container-policy`) and the cleanup opt-out
  (`com.bastionntb.yavsos.cleanup-optout`);
- on a player: the MultiUserChest item format (`com.bastionntb.yavsos.muc-format`);
- files: `com.bastionntb.yavsos.cfg` and `com.bastionntb.yavsos.categories.xml`.

Names were settled before 1.0: `Server/CleanupMaximumRange` became `Server/MaximumRange` (the
old key is read once), and the item-format marker moved under the YAVSOS ID.

**Rule data lasts across versions.** A chest's rule is stored on the chest in a versioned
envelope. One principle applies to categories, rule filters, filter values and the values a
server sends: keep what you don't recognise, and match nothing with it. A newer rule read by
an older YAVSOS therefore takes less, never more, and nothing is lost when the chest is edited.
A rule in a format this version can't read at all is kept untouched and takes nothing. A
release that must rename a category ID ships a mapping from the old ID.

**A clean release.** Test fixtures build only in a separate test configuration and are absent
from the release DLL.

Decisions: D007, D050, D058, D077, D078, D081, D086.

## 9. Interface

**One panel, built with Jotunn.** The rule panel uses Jotunn's GUI and appears beside the
game's chest window. Jotunn is a required dependency. A fallback panel for games without
Jotunn was rejected: two panels to maintain. If Jotunn's GUI doesn't pass YAVSOS's check,
only the panels turn off; rules, stashing and cleanup keep working.

The panel is stacked in sections: **Rule** (what the chest takes, with Clear rule), **Edit**
(Categories, Items and Filters tabs), **This chest** (priority and the cleanup opt-out) and
**Clean up nearby**. The status marks sit at the top right (section 3). In the Items tab, a
click on an item cycles Always, Never and clear.

**Explanations go where the player looks.**

- An item's tooltip adds its smallest categories and facts worth knowing, such as
  "Raw food, Animal feed · eaten by Boar, Hen".
- For a stack in the inventory, the tooltip also says where storing that one stack would put
  it, from the same placement decision: "Stash stack [Middle mouse]: Chest 3 m away". It names
  the single-stack key that applies, AzuAutoStore's when AzuAutoStore does the storing.
- A chest's hover text adds its rule in short words, such as
  "Food › eaten by Boar · priority 2 (Urgent)". There is no "YAVSOS:" prefix.
- A result panel after YAVSOS's own stash or cleanup says what moved, what stayed and why.

**Search lights chests.** Searching in the Categories tab or the Items tab lights the nearby
chests whose rule could take the items found. A search has no real stack to judge, so filters
on the stack (upgrade level, damaged, rarity, crafter) are set aside for the highlight;
Always, Never, categories and item filters still decide. Chests stay lit while the search is
active. After the chest closes they stay lit for `Controls/HighlightSecondsAfterClose`
seconds (default 20, a local setting), so the player can walk to them.

**The display never writes.** Search, highlight, labels and explanations never change a
chest, an inventory or a rule. A view that fails turns off alone.

Decisions: D062, D066, D069, D070, D071, D100.

## 10. Scope kept out

- **Biome inference.** Working out which biome an item comes from was explored and moved out
  of YAVSOS. A separate optional add-on may provide it. Biome filters were removed; a rule
  that names a filter from an add-on keeps it, matching nothing, for players without the
  add-on.
- **Exhaustive item discovery.** Storage does not need a complete census of where every item
  comes from. YAVSOS uses ordinary loaded evidence and keeps uncertain items in Misc.
- **A public data API.** Sharing YAVSOS's item evidence and categories as a resource other
  mods can use and extend is a separate design question for later, together with reading
  several categories files.
- **Ground pickup in YAVSOS's own stash.** Picking up dropped items is not organisation, and
  many mods already do it. With AzuAutoStore, its ground pickup follows the rules.
- **Automatic cleanup.** Cleanup runs only when a player asks for it. A bedtime trigger was
  considered and rejected as impractical.
- **Crash consistency.** See section 6.
- **Crossplay.** YAVSOS makes no claim either way.

Decisions: D037, D041, D042, D061, D079, D084, D094.
