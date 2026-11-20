# YAVSOS design

This document explains why YAVSOS works the way it does. Each section gives the current
answer, the reason for it, and the main alternative that was considered. Sections end with
the IDs of the decisions they come from (for example "Decisions: D042, D058"). The IDs come
from the project's decision log and are kept so later changes can be traced; they are plain
labels, not links.

For how to install and use YAVSOS, see the player guide (the README).

## 1. Goals and the four pillars

YAVSOS gives chests rules and puts items where the rules say. A rule names categories, Always
and Never items, and filters, and a chest has a priority. The stash key moves items from the
player's inventory into nearby chests. "Clean up nearby" moves items between nearby chests so
each one ends up where it belongs.

Four pillars guide every change:

- **Efficiency.** Work happens when its inputs change, not every frame. Categories are built
  when items, recipes or the category settings change. Every scan and run has a bound: one
  stash or cleanup looks at the nearest 256 chests by default, 512 at most (section 7).
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
- Not a replacement chest screen. The YAVSOS panel sits beside the game's own chest window.
- Not an item database for other mods. Its categories serve storage (section 10).

YAVSOS is licensed GPL-3.0-or-later. The bundled ServerSync and MultiUserChest code is MIT
and ships with its notices.

Decisions: D042, D082.

## 2. Compatibility first

**AzuAutoStore does the stashing.** When AzuAutoStore is installed, its stash key, its key for
a single stack and its ground pickup stay in charge. YAVSOS hooks the check AzuAutoStore makes
before it stashes into a chest. On a chest with a rule, the rule replaces AzuAutoStore's "the
chest must already hold this item" check. YAVSOS also runs AzuAutoStore's stash key item by
item in YAVSOS's chest order (section 5). Every item still goes in through AzuAutoStore's own
store call, so its messages, skip lists, exclusions and multiplayer handling are unchanged.
Without AzuAutoStore, YAVSOS stashes by itself, in the same order.

Why: players already know and trust AzuAutoStore. A storage mod that replaces it forces a
choice between the two. The alternative, a YAVSOS stasher that is always active, was
rejected for that reason.

**A partner's behavior is accepted.** A job handed to another mod follows that mod's model,
and a fault inside it is that mod's to fix. YAVSOS documents such behavior as inherited
instead of rebuilding around it. Two examples:

- AzuAutoStore's pause key stops only its ground pickup; its own stash key ignores the pause.
  YAVSOS does the same: cleanup and the ordered stash key ignore the pause, and YAVSOS's
  ground-pickup choice respects it.
- A setting that belongs to a partner stays there. With AzuAutoStore installed, YAVSOS's
  `Stash` settings are shown read-only, with a note that AzuAutoStore controls them. Their
  values are kept for when AzuAutoStore is removed.

**Keys and mouse actions don't collide with partners.** Before a new YAVSOS key or mouse
action gets its default, check the defaults of the production pack's partner mods (D162). The
stack key defaults to Shift + middle mouse because ZenSign's inventory search uses middle
mouse. ZenSign's search ignores modifiers, so when ZenSign is installed and its search button
is the stack key's main key, a stack-key click that YAVSOS stashes with is kept from ZenSign:
YAVSOS resets that button in the game's input until the click is released. Plain middle click
stays ZenSign's. The adapter finds ZenSign's button and key setting by their shape; if it
can't, it turns itself off, logs once, and both actions run on that click again. A saved
stack key equal to the old default (plain middle mouse) moves once to the new default.

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
get the same behavior as from the standalone mods. When upstream already tracks a needed
change, YAVSOS waits for upstream's fix instead of patching its copy. Apart from a renamed
namespace and YAVSOS's own loader in place of its plugin class, the one change to the
MultiUserChest copy is how it handles each player's item format (section 6). The bundled
MultiUserChest steps aside when the standalone mod or AzuAutoStore's copy is installed, and stays
off with QuickStack, SimpleSort or QuickStore, which conflict with it.

Decisions: D030, D042, D043, D047, D061, D070, D079, D090, D097, D162.

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

**The runtime backstop refuses one write, not the mod.** If AzuAutoStore writes into a chest
with a rule by a path the rule did not approve, YAVSOS refuses that write and the item stays
with the player. It logs once per path and keeps the integration running. A write path that AzuAutoStore
added since the reviewed version is watched too, but its writes into a chest whose rule
doesn't accept the item are only logged: YAVSOS can't tell whether refusing would lose the
item on a path nobody has checked, and a misplaced item is the safer failure.
Disabling the integration on the first surprise was rejected: one unusual path would make
the whole mod useless.

**Each AzuAutoStore hook has its own startup check.** When AzuAutoStore's code changes, only
the hook whose check fails turns off: empty chests stop attracting items, or ground pickup
stops asking rules, while rules keep other items out. Only a missing store call turns the
whole integration off. A list of AzuAutoStore's write paths and chest types is compared for
the log only. Turning everything off on any difference was rejected (D171): it pinned one
AzuAutoStore release, and a missed path can misplace an item but not lose one.

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
- if a category build step fails (reading items and their sources first gets 3 retries,
  3 seconds apart), the Categories mark says so, and Always and Never items keep working.
  YAVSOS tries the step again after a minute, then less often, up to every 5 minutes, and at
  once when items or settings change. On a dedicated server the step runs on the server, so
  the server shares its stage state with clients in a separate synced value: a client's
  Categories mark and its stash and cleanup messages then say that the server couldn't build
  the categories, that it tries again every few minutes and that the server's log says why.
  The client's log names the failed stage and the server's reason. A server that sends no
  state, or one the client can't read, leaves today's "still loading" text.
- every place that says why categories aren't ready tells the same story as the Categories
  mark: the messages, the reason in brackets after a count that stays ("2 stay in your
  inventory (the server couldn't build the categories)") and the item tooltip's where-line say
  still loading, the server couldn't build them, or (on a hosted world or in single player)
  YAVSOS couldn't build them. This game's own failure comes first, as on the mark.
- a failed rebuild keeps the last good categories. The Categories mark then turns amber with a
  line saying the last update failed: on a hosted world or in single player it names this
  game's log; on a client, when the server reports the failed rebuild, it names the server's
  log. Both say the build is tried again every few minutes.

The status marks at the top right of the panel use four states: a tick (working), amber
("This chest:" a caution about the open chest, or "Partly working:" one optional part is
off), a cross ("Not working:") and a gray dash ("Off:" a setting turned it off). A mod that
is not installed gets no mark.

Decisions: D045, D046, D058, D068, D086, D087, D144, D145, D159.

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

**The admin's categories file has the last word.** `com.bastionntb.yavsos.categories.xml` in
the config folder can add categories and assign items. An assignment's `<member>` lines replace
that item's automatic categories, so no unexpected automatic membership remains; since 1.2,
`<add>` and `<remove>` lines change only the categories they name. YAVSOS creates the file
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

**What a rule holds.** A chest's rule lists categories, Always and Never items, and
filters. Filters judge either the item (food's main stat, eaten by a tame, can go
through portals, item origin, used for, can be upgraded) or the actual stack (upgrade level,
damaged, EpicLoot rarity, crafted by). Never always wins. Always accepts the item even when
no ticked category or filter does. The editor offers only the filters and values that the
ticked categories' items can have. A stack filter that can't read its evidence at that moment
rejects the stack, so a "plain" rarity rule never accepts an item it could not check.

**EpicLoot's rarities come from EpicLoot.** The rarity filter offers the rarities the loaded
EpicLoot declares, so a rarity a later EpicLoot adds becomes a filter value by itself, and its
dust, shards and runestones keep their categories. EpicLoot's save-data version is not
checked. A rarity EpicLoot no longer declares stays in saved rules, marked as no longer
applying. A fixed list of six names was replaced (D171, #104): a seventh rarity turned the
rarity filters and EpicLoot's item roles off, and a new data version made every magic item
unreadable. If a later EpicLoot renumbered its rarities, magic items could go to the wrong
rarity's chest; they would be misplaced, never lost.

**One ranking.** When several chests accept an item, they are ranked in this order:

1. ordinary chests before overflow chests (a stored overflow flag is kept; there is no control
   for it);
2. higher priority first;
3. a closer kind of match: an Always item before a filtered category before a plain category;
4. the more specific category (a subcategory before its umbrella);
5. the smaller category (Metals before Crafting materials, which holds far more items);
6. the chest's ID, so every player computes the same order.

Players see priority as levels 1 to 10, 1 first and 5 (Normal) the default. The first
design compared specificity before priority. It was changed because a player who sets a
priority means it to win.

**One placement decision.** The stash key (AzuAutoStore's or YAVSOS's) and "Clean up nearby"
ask the same question: where does this item belong? They use the same order, so after a
stash, cleanup finds nothing to move. Always means "accepted here, ranked above categories at
the same priority"; it does not mean "kept here".

Cleanup moves three kinds of stack:

- a stack the chest's rule rejects;
- a stack in a chest without a rule (chests, carts and ships the players built), when a chest
  with a rule accepts it;
- a stack in a chest with a rule that accepts it, when another chest ranks strictly higher
  for it.

Equally ranked chests never trade items. Cleanup never moves items into chests without a rule;
the stash does fill a chest without a rule that already holds the item. A chest can opt out
with "Don't clean up this chest".

**Which chests one cleanup works on.** A cleanup considers the nearest `Server/MaximumChests`
chests (default 256, up to 512) and takes items out of at most `Server/MaximumSources` of them
(default 64, up to 256). When more chests hold items than that, the chests with work that is
cheap to find come first, nearest first: a stack its own rule rejects, or a stack in a chest
without a rule, that some chest's rule accepts. The test is the rule check alone, cached per
item. The slots left go to the nearest other chests, for re-sorts, which need the full
ranking. Each press moves the misplaced items out, those chests drop off the list, and the
next press reaches further. A chest whose accepting chests are full keeps its place, and the
result says "no room". Re-sorts beyond the nearest chests wait until the player moves closer.

**Containers with their own filter.** Some mods' containers accept only some items: each
Storehouse store accepts only its own kind. YAVSOS reads every container type's filter the
way the game would answer: once categories are ready, it asks the game's own "can this
inventory take one unit?" on an empty inventory with each type's name and size, for every
item, a little each frame. A type that refuses some item has a filter; one that accepts
everything (a plain chest, Storehouse's Emergency Shelves, any store while Storehouse isn't
fully loaded) has none. No mod types are used. The lists are read again when another mod's
settings arrive from the server, when the item catalog changes, and each session; until they
are read, stores act as before (a chest without a rule). Each game reads the lists itself:
they are never sent, and the server's per-item answers (section 4) don't change.

- Each filtering type is a category, such as "Storehouse Timber Crib", whose members are the
  items its filter accepts. Any chest's rule can tick it. Item tooltips and the panel's
  suggestions leave these categories out.
- In the Categories tree these categories fold under one heading per mod, such as "Storehouse
  stores (46)", folded by default and open while one of them is ticked. Under the heading each
  shows by its piece's name ("Timber Crib"); elsewhere it keeps the full name. The heading is
  not a category and has no tick: ticking every store at once would accept almost everything.
  A search finds each store's category and opens its heading.
- A store without a YAVSOS rule acts as if its own category were ticked, at
  `Storehouse/DefaultPriority` (default 4), while `Storehouse/AutomaticRules` is on. Nothing is
  saved on the chest; the panel shows the rule with an "Automatic rule" chip, and the first
  change saves it as the chest's own. Every reader counts it: cleanup, YAVSOS's stash,
  AzuAutoStore's hooks, its stash order and ground pickup, and the search's "accepts" light.
- The filter wins. A container whose type has a filter accepts what its rule accepts and its
  filter accepts: a rule can narrow the filter, never widen it. Rule parts outside the filter
  stay saved but do nothing, and the panel grays them out with a tooltip, as it grays the
  categories and items the container can't hold. Because the planner knows the filter in
  advance, no plan sends a store an item it refuses, full or not.
- A rule part naming a filter category waits while the lists are unread, as a category part
  waits for categories, so nothing moves on its account. If the game's check stops working,
  the categories and the automatic rules turn off together for the session; such parts keep
  waiting. Once the lists are read, a filter category that no longer exists (the mod was
  removed) is an ordinary unknown category.

**Rules survive change.** A rule naming a category, filter or filter value this version
doesn't know keeps that part unchanged. The unknown part matches nothing, so the rule can only
accept less, never more, and it works fully again when the part returns. A rule stored in a
format this version can't read is kept untouched and accepts nothing. Before categories are
ready (just after joining, or during a rebuild with nothing to keep), a rule's Always and
Never items already work; only its category part waits.

**Rules apply at once.** Every change in the panel is saved immediately; there is no Save
button. Unticking the last part removes the rule, and Clear rule removes every part,
priority included. Undo puts back the chest's rule from before the last change; pressed
again, it redoes the change.

**Who may edit.** Editing a rule uses the game's own access model, the same check as opening
the chest: a private chest is its builder's alone, and inside an active ward only the ward's
permitted players may edit. No separate builder or admin check is added. The write is made
by the game that owns the chest's network object, and only if the rule is still the one the
player opened.

**Editing a chest another game holds.** With MultiUserChest, a second player can open a chest
whose network object belongs to the first player's game. The second player's game builds the
new rule and sends it, in one checked request, to the game that holds the chest (another
player's, or the server's when it has the chest loaded). That game checks that it still holds
the chest, the chest's privacy for the requesting player, a limit of 20 requests a second per
player, that the rule is still the one the requester saw, and that the new rule is a newer
envelope; then it writes, reads back and answers. The requester checks the ward, as vanilla
does when opening a chest. The panel shows "saving" and takes no clicks for up to 5 seconds;
seeing the new rule arrive on the chest also counts as saved, until 40 seconds in all. Undo,
redo and "Don't clean up this chest" go the same way. Considered and not chosen: taking over
the chest's ownership (it would pull the chest away from the game moving items in and out of
it) and sending the edit itself (both games would need the same categories to agree on the
saved rule). Every game on a YAVSOS server runs 1.3 or later, so every owner can answer; on a
server without YAVSOS, an owner without it never answers and the wait covers it.

Decisions: D002, D048, D050, D051, D054, D057, D061, D064, D065, D069, D070, D072, D077, D086,
D092, D153, D154, D155, D156, D160.

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

**Ask the chest itself.** Before a chest becomes a destination, the plan asks the game
whether it takes one unit of the item, the same check the game makes before adding an item.
Mods that filter chests answer through it: a Storehouse store refuses items of other kinds.
A chest that refuses drops out of the plan, and the item goes to the next chest or stays.
Only a chest with room is asked, so a full chest still counts as full. A container whose type
has a filter (section 5) is known to refuse an item before any asking, full or not; the live
question still covers filters a mod sets on one container rather than its type.

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
the other player's format. For a player who publishes none (AzuAutoStore without YAVSOS),
YAVSOS's copy sends their copy a request for a chest that doesn't exist. Every copy answers
it without touching an inventory, echoing a test item in its own format, and the echo's
length gives the format (D116). When the format is still unknown, the move is refused and
the item stays put. Requiring everyone to match one copy was rejected: the server is rarely part of
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
Other mods' favorites always stay: a stack that ItemFavoriteFramework, Quick Stack Store or
AzuExtendedPlayerInventory marks, as an item or by its slot. A favorites mod YAVSOS can't
read leaves stashing working; the next stash result names it once. With AzuAutoStore
installed, its own settings and favorites decide.

Decisions: D004 (as amended), D005, D041, D042, D043, D057, D061, D065, D083, D093, D151.

## 7. Server, hosted and client-only

**Shared settings come from the server.** The `Server` settings and the categories file are
synchronized from the server through ServerSync, bundled at a pinned version. While
`Server/LockSharedPolicy` is on, only server admins can change them. A client's change request
is accepted only from the current connection and only for shared entries, then ServerSync's
admin and lock rules decide. While a server's values are in force, a player's own settings file
keeps the player's own values, so leaving the server restores them.

**Configuration sync grants no chest authority.** Synchronized settings say what players may
do; they never let anyone write to a chest. Every move still needs the game's own access and
ownership (section 6).

**Modes are detected, not chosen.**

| Situation | Behavior |
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
for cleanup and for YAVSOS's own stash. `Server/MaximumChests` (default 256, 16 to 512) is how
many of the nearest chests one cleanup or YAVSOS stash handles; the result says when more
were in range. `Server/MaximumSources` (default 64, 16 to 256) is how many of them one cleanup
takes items out of, chests with misplaced items first (section 5). With AzuAutoStore
installed, its stash key uses its own range and finds chests itself, so these settings then
limit cleanup only. The bounds were 256 chests and a fixed 64 sources until bases near 256
chests needed more (D156): admins may raise a limit and accept the planning cost.
`Server/AllowStashing` and `Server/AllowCleanup` turn the two actions off. The `Storehouse`
settings (section 5) are server settings too, so every player's game ranks the stores the
same way.

**Server templates are the server's.** A server keeps its rule templates in
`BepInEx/config/com.bastionntb.yavsos.templates.xml` and sends the list to every player through
ServerSync, as it sends the categories file. Players can apply a server template or keep a copy
of it. In 1.3 only server admins share, update, rename or delete one (D157). ServerSync could
carry an admin's push of the whole list, but YAVSOS refuses pushed values (shared entries only,
above), and a push would skip the checks. So each change is one small request on the admin's
own server connection: the server reads who sent it from that connection, checks the game's
admin list, a rate limit and the revision the admin saw, saves the file and sends the new list.
A change made against an older revision is refused, so two admins can't overwrite each other.
Which buttons a player sees comes from the same server check: the panel asks the server, on the
same connection, whether the player is an admin, and asks again a minute after each answer.
Valheim's own client check stands in only until the first answer, because a joining player
rarely receives the admin list. A hosted world's host is the server and changes the list directly. The answer is awaited for
10 seconds; a late one is dropped. A server without YAVSOS sends no list, so its players see
no Server templates section.

Decisions: D008, D009, D010, D011, D044, D074, D078 (B06), D086, D095, D155, D156, D157.

## 8. Versions and data formats

**Two kinds of version.** Game and mod versions never gate a feature (section 3). YAVSOS's
own compatibility version protects YAVSOS's network format: a player older than the server's
minimum is told to update when joining. The minimum is 1.3.0, raised from 1.0.0 because
edits on another player's chest need every game to answer them (D153, D154). A release raises
it when it needs to; an update never loses data that can't be recreated (items, chest rules,
the categories file).

A difference in classifier version between client and server does not reject the server's
answers; the versions stay in the log. The category settings and taxonomy must still match.
Example: a server on a later release fixes one item's category, and a client one update behind
keeps working with the corrected answer.

**Stable names.** The plugin ID `com.bastionntb.yavsos` and assembly `YAVSOS.dll` stay the
same. Everything YAVSOS stores or sends is named under that ID:

- on a chest: its rule (`com.bastionntb.yavsos.container-policy`) and the cleanup opt-out
  (`com.bastionntb.yavsos.cleanup-optout`);
- on a player: the MultiUserChest item format (`com.bastionntb.yavsos.muc-format`);
- network messages: the rule write to a chest's owner (`com.bastionntb.yavsos.rule-write`) and
  its answer (`com.bastionntb.yavsos.rule-write-reply`); an admin's template change
  (`com.bastionntb.yavsos.template-change`) and its answer
  (`com.bastionntb.yavsos.template-change-reply`); the question whether a player is a server
  admin (`com.bastionntb.yavsos.template-admin-query`) and its answer
  (`com.bastionntb.yavsos.template-admin`); the synced template list (`rule-templates-v1`) and
  the server's category stage state (`server-stage-state-v1`);
- files, all in the config folder: `com.bastionntb.yavsos.cfg`, `com.bastionntb.yavsos.categories.xml`,
  `com.bastionntb.yavsos.templates.xml` (on a server) and
  `com.bastionntb.yavsos.personal-templates.xml` (personal rule templates, D166).

**Two folders.** "The config folder" (the maintainer also says "the mod config folder") is
`BepInEx/config` in the mod-manager profile, where every mod keeps its configuration; all of
YAVSOS's files are there. "The save folder" is the game's own folder for characters and worlds
(`~/.config/unity3d/IronGate/Valheim/` on Linux); YAVSOS keeps nothing there (D168).

Names were settled before 1.0: `Server/CleanupMaximumRange` became `Server/MaximumRange` (the
old key is read once), and the item-format marker moved under the YAVSOS ID.

**Rule data lasts across versions.** A chest's rule is stored on the chest in a versioned
envelope. One principle applies to categories, rule filters, filter values and the values a
server sends: keep what you don't recognize, and match nothing with it. A newer rule read by
an older YAVSOS therefore accepts less, never more, and nothing is lost when the chest is
edited. A rule in a format this version can't read at all is kept untouched and accepts
nothing. A
release that must rename a category ID ships a mapping from the old ID. Template lists (personal
and the server's) are strict and versioned: a list this version can't read, a newer one or a
damaged one, is kept exactly as stored and never written over; saving to it is refused until it
can be read (D153).

**A clean release.** Test fixtures build only in a separate test configuration and are absent
from the release DLL.

Decisions: D007, D050, D058, D077, D078, D081, D086, D153, D157.

## 9. Interface

**One panel, built with Jotunn.** The YAVSOS panel uses Jotunn's GUI and appears beside the
game's chest window. Jotunn is a required dependency. A fallback panel for games without
Jotunn was rejected: two panels to maintain. If Jotunn's GUI doesn't pass YAVSOS's check,
only the panels turn off; rules, stashing and cleanup keep working.

Under the title, a strip shows the chest label, or the message from the player's last
action. Below it the panel is stacked in sections: **Rule** (what the chest accepts, with
undo, Copy, Paste, Clear rule and rule suggestions), **Edit** (Categories, Items, Filters and
Templates tabs), **This chest** (priority) and a last row with the cleanup opt-out and **Clean up
nearby**. The status marks sit at the top right (section 3). In the Items tab, a click on an
item's icon cycles Always, Never and clear; a right-click cycles the other way. The items the
rule accepts show in one section per ticked category, or in one section for the whole rule; a
button beside the search box switches between the two, and the choice is kept on the player's
own PC for every chest, like the fold. Each section has Never all and Clear, except the section
of items marked outside the categories, which has Clear only. Never all marks at most 512 items
per click: above that it is grayed out, and its tooltip says to search or untick a category.

**A button hides the panel.** A "YAVSOS" button below the chest window shows and hides the panel
on every chest. It is the `Controls/ShowRuleControls` setting: lit while the panel shows, dimmed
while it is hidden. Hidden means only the panel is gone; the button stays, and the item
tooltip's where-line, the stash and cleanup keys and the result panel keep working. Undo
(Ctrl+Z) and the chest search need the panel. The button's place is a local setting: a preset
(left, middle or right end of the window, left by default) plus offsets, since other mods put
buttons there too. New installs start with the panel hidden; an existing settings file keeps
its value.

**Templates keep rules for later.** The Templates tab saves the open chest's rule under a name
in one list that every character shares (a file in the config folder, D166), so it is there
in every world and on every server, and lists the server's
templates under it. A template holds what Copy takes: categories by ID, Always and Never items,
filters and priority. Select one, then Apply: it replaces the chest's rule as Paste does, saves
the same way (through the owner's game on another player's chest), and undo takes it back.
Update from this chest, Rename and Delete change a template; Share (server admins) copies a
personal one to the server; Keep a copy takes a server one into the personal list. Copies are
snapshots. A template that names a category, item or filter this game doesn't have says so on
its row and still applies; the missing parts stay stored, unused. Limits: 64 templates per
list, 32 KiB each, 256 KiB for the server's list and 512 KiB for personal ones. The details
area keeps its size, and a change with no undo (any change to a server template, deleting a
personal one) asks first in place of the buttons. Applying a template to many chests at once
is planned for 1.5 (#82).

**Explanations go where the player looks.**

- An item's tooltip adds its smallest categories and facts worth knowing, such as
  "Food ingredients, Animal feed · eaten by Boar, Hen".
- For a stack in the inventory, the tooltip also says where stashing that one stack would put
  it, from the same placement decision: "Stash stack [Shift + Middle mouse]: Chest 3 m away". It names
  the stack key that applies, AzuAutoStore's when AzuAutoStore does the stashing.
- A chest's hover text adds its chest label, the rule in short words, such as
  "Food › eaten by Boar · priority 2 (Urgent)". There is no "YAVSOS:" prefix.
- After YAVSOS's own stash or cleanup, a result panel says what moved, where it went, what
  stayed and why. After AzuAutoStore's stash key, it says what moved and where.
- **Chest names.** Wherever YAVSOS names a chest other than the open one (the where-line, the
  result panel's place lines, the search status line's tooltip), it gives the game's name and
  the distance across the ground, so every chest of a stack reads the same distance. A chest
  standing in a stack also gets its place in it: "Reinforced chest 6 m away, 2 of 3 from the
  bottom", "…, bottom of a stack of 3", "…, top of a stack of 3". A chest standing alone reads
  "Reinforced chest 6 m away". A stack is the chests standing in one column, each within 0.35 m
  across the ground of the chest below it, so a stack that leans a little stays one stack. It
  splits where two are more than 2.5 m apart in height.
  Every container counts, with or without a rule.

**The Categories tab's search matches at word starts.** "ores" finds the "Ores & metal scrap"
category, not "Heart of the Forest". Only when nothing matches at the start of a word does it
match inside words, and only when no name the player can see matches
does it match the game's internal item IDs. In languages written without spaces between
words, such as Chinese, every letter starts a word. Matching anywhere in a name was replaced (D171,
#118): a short query filled the eight item places with items whose hidden IDs happened to
contain it.

**Search lights chests.** Searching in the Categories tab or the Items tab lights the nearby
chests that hold the items found. With "Also show chests that accept it" on, chests whose rules
could accept them light up too, in another color. A search has no real stack to judge, so
filters on the stack (upgrade level, damaged, rarity, crafter) are set aside for that test;
Always, Never, categories and item filters still decide. Chests stay lit while the search is
active. Once the player clears the search or closes the chest, they stay lit for
`Controls/HighlightSecondsAfterClose` seconds (default 20, a local setting), so the player can
walk to them; closing the chest after clearing the search starts that time over (D169). A new
search that finds something replaces them, and opening another chest ends them.

**Glowing chests show through walls.** The game's glow is hidden by walls and by the chests
around a chest, so every chest YAVSOS lights (a search's, the chests a stash or cleanup put
items into, and cleanup's stays mark) is also shown through them. With Zen.ModLib installed and
its highlight in the shape YAVSOS checks for, the chest gets Zen.ModLib's blinking outline, the
look ZenSign's search shows. Otherwise, or when Zen.ModLib's own setting turns outlines off, a
marker over the chest on the screen shows it, with its place in its stack ("2 of 3"). The glow
stays as well. `Controls/ShowThroughWalls` (on by default, a local setting) turns both off.
Zen.ModLib keeps one highlight per chest for every mod that uses it, so YAVSOS never ends an
outline another mod (ZenSign) has taken over: that chest gets the marker until no other mod
highlights anything, then its outline again. YAVSOS removes the outline component it added
when its outline ends.

**The display never writes.** Search, highlight, labels and explanations never change a
chest, an inventory or a rule. A view that fails turns off alone.

Decisions: D062, D066, D069, D070, D071, D100, D141, D143, D148, D149, D157, D158, D163.

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
- **Ground pickup in YAVSOS's own stash.** Picking up dropped items is not organization, and
  many mods already do it. With AzuAutoStore, its ground pickup follows the rules.
- **Automatic cleanup.** Cleanup runs only when a player asks for it. A bedtime trigger was
  considered and rejected as impractical.
- **Crash consistency.** See section 6.
- **Crossplay.** YAVSOS makes no claim either way.

Decisions: D037, D041, D042, D061, D079, D084, D094.
