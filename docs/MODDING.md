# YAVSOS for server admins, pack makers and modders

YAVSOS works out its categories by itself, from the game's own item data, recipes and the
definitions mods register. Nothing here is needed to play. This page covers the categories
file, for when you want to rename a category, add your own, or put specific items into a
category. Settings are listed in [SETTINGS.md](SETTINGS.md).

## The categories file

`BepInEx/config/com.bastionntb.yavsos.categories.xml`

YAVSOS creates an empty one the first time it starts and never overwrites it.

**Whose file counts.** On a server with YAVSOS, the server's file applies to every player, and
players' own files are not used while they're connected. If you host a world, your file
applies to everyone in it. In single-player and on a server without YAVSOS, your own file
applies.

**Changes apply live.** Save the file and YAVSOS picks it up within a few seconds, without a
restart; a server sends the new version to everyone connected. Raise `revision` every time
you change the file: a changed file with the same or a lower revision is refused.

**A file with a mistake is ignored.** If the file can't be read, is invalid or is larger than
256 KiB, the last version that worked stays in use, and the BepInEx log says what's wrong.
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
  a category. IDs use lowercase letters, digits, `.`, `_` and `-`; the part before the last
  `.` makes it a subcategory, so `materials.metal.boss` sits under Metals. The built-in IDs
  are in
  [`DefaultClassificationTaxonomy.cs`](../src/YAVSOS.Core/Classification/DefaultClassificationTaxonomy.cs).
- **`<assignment>`** chooses items by prefab name (the name the game's `spawn` command uses),
  either one item (`match="exact"`) or a pattern with `*` and `?` (`match="glob"`). Names are
  case-sensitive. Its `<member>` lines **replace** the automatic categories of those items;
  `primary`, if given, is the category the item is shown under.
- When several assignments match an item, an exact one wins; among patterns, the most
  specific one (the most literal characters) wins.
- Both sections must be there, even when empty. Every category an assignment names must be
  listed under `<categories>`, built-in ones too: list a built-in category with its own name
  (like `trophies` above) to use it unchanged.

Players' own choices on chests (Always, Never, filters) still apply on top of the categories
the file sets.

### Limits

| | Most |
| --- | --- |
| File size | 256 KiB |
| Categories in the file | 256 |
| Assignments | 2,048 |
| Category ID | 64 characters |
| Display name | 128 characters |
| Item selector | 128 characters, no spaces |
