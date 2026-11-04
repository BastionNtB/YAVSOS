# YAVSOS for server admins, pack makers and modders

YAVSOS works out its categories by itself, from the game's own item data, recipes and the
definitions mods register. Nothing here is needed to play. This page covers the categories
file, which renames categories, adds your own and puts specific items into them. Settings
are listed in [SETTINGS.md](SETTINGS.md).

## The categories file

`BepInEx/config/com.bastionntb.yavsos.categories.xml`

YAVSOS creates an empty one the first time it starts and never overwrites it.

**Whose file counts.** On a server with YAVSOS, the server's file applies to every player;
their own files aren't used while they're connected. If you host a world, your file applies
to everyone in it. In single-player and on a server without YAVSOS, your own file applies.

**Changes apply live.** Save the file and YAVSOS picks it up within a few seconds, without a
restart; a server sends the new version to everyone connected. Raise `revision` every time
you change the file: YAVSOS refuses a changed file with the same or a lower revision.

**A file with a mistake is ignored.** If the file can't be read, is invalid or is larger than
256 KiB, the last version that worked stays in use and the BepInEx log says what's wrong.
Nothing is half-applied.

### Example

```xml
<policy xmlns="urn:com.bastionntb.yavsos:categories" schema="1" revision="2">
  <categories>
    <category id="materials.metal" displayName="Bars" />
    <category id="materials.metal.boss" displayName="Boss metals" />
    <category id="trophies" displayName="Trophies" />
  </categories>
  <assignments>
    <assignment match="exact" selector="BlackMetal" primary="materials.metal.boss">
      <member category="materials.metal.boss" />
    </assignment>
    <assignment match="glob" selector="Trophy*">
      <member category="trophies" />
    </assignment>
  </assignments>
</policy>
```

- **`<category>`** with a built-in ID renames that category and keeps its place. A new ID adds
  a category. IDs use lowercase letters, digits, `.`, `_` and `-`. A category whose ID starts
  with another category's ID and a `.` is a subcategory of it (of the longest, if several
  match): `materials.metal.boss` sits under `materials.metal`, which the example renames
  Bars. The built-in IDs are in
  [`DefaultClassificationTaxonomy.cs`](../src/YAVSOS.Core/Classification/DefaultClassificationTaxonomy.cs).
- **`<assignment>`** chooses items by prefab name (the name the game's `spawn` command uses),
  either one item (`match="exact"`) or a pattern with `*` and `?` (`match="glob"`). Names are
  case-sensitive. Its `<member>` lines **replace** the automatic categories of those items;
  `primary`, if given, is the category the item shows under.
- When several assignments match an item, an exact one wins; among patterns, the most
  specific one (the most literal characters) wins.
- Both sections must be there, even when empty. List every category an assignment names
  under `<categories>`, built-in ones too: list a built-in category with its own name (like
  `trophies` above) to use it unchanged.
- A name you set shows as written, in every language. A built-in category listed with its
  name from `DefaultClassificationTaxonomy.cs` keeps its built-in name, which follows the
  player's language.

Players' own choices in chest rules (Always and Never items, filters) still apply on top of
the categories the file sets.

### Adding and removing categories

`<member>` lines replace all of an item's categories. To keep them and change only some, use
`<add>` and `<remove>` lines instead (YAVSOS 1.2 or later):

```xml
<policy xmlns="urn:com.bastionntb.yavsos:categories" schema="2" revision="3">
  <categories>
    <category id="materials.ore.bay" displayName="Ore bay" />
    <category id="weapons.swords" displayName="Swords" />
    <category id="drinkware" displayName="Drinkware" />
  </categories>
  <assignments>
    <!-- Copper and tin ore keep their categories and join the ore bay. -->
    <assignment match="exact" selector="CopperOre"><add category="materials.ore.bay" /></assignment>
    <assignment match="exact" selector="TinOre"><add category="materials.ore.bay" /></assignment>
    <!-- Tankards leave Swords and go to Drinkware; their other categories stay. -->
    <assignment match="glob" selector="Tankard*" primary="drinkware">
      <remove category="weapons.swords" />
      <add category="drinkware" />
    </assignment>
  </assignments>
</policy>
```

- One assignment uses either `<member>` lines or `<add>` and `<remove>` lines, not both, and
  names each category once.
- `<remove>` lines apply first. Removing a category the item doesn't have does nothing; if no
  category is left, the item goes to Misc.
- `primary` must be one of the assignment's `<add>` lines.
- Removing a parent category has no effect while the item stays in one of its subcategories:
  removing `materials` from an item still in `materials.metal` keeps it in Materials.
- Only one assignment applies to an item, as before. If an exact assignment names the item,
  no pattern applies to it, even one that only adds.

**Older YAVSOS versions.** A file with an `<add>` or `<remove>` line is schema 2; one with
only `<member>` lines is schema 1, whatever its `schema` attribute says (which must be `1` or
`2`). YAVSOS 1.1 and earlier can't read schema 2. On your server, a player with one of those
versions finds that categories in rules accept nothing (Always and Never items still work)
until they update, and the server's BepInEx log names each such player. A file with only
`<member>` lines works with every version.

### Limits

| | Most |
| --- | --- |
| File size | 256 KiB |
| Categories in the file | 256 |
| Assignments | 2,048 |
| Category ID | 64 characters |
| Display name | 128 characters |
| Item selector | 128 characters, no spaces |
