using System;
using System.Collections.Generic;

namespace YAVSOS.Interface;

/// <summary>
/// P6-21 (maintainer, 2026-10-23): whether a recipe can upgrade an item, from the game's own data: an enabled recipe makes
/// it and the item has more than one quality level. Eggs and other items whose quality grows another way do not count.
/// Built once per item registry (ObjectDB instance and recipe count). Unity game thread only.
/// </summary>
internal static class UpgradeRecipes
{
    private static readonly HashSet<string> Items = new(StringComparer.Ordinal);
    private static ObjectDB? built;
    private static int recipes = -1;

    internal static bool Can(string prefab)
    {
        var db = ObjectDB.instance;
        if (!db) return false;
        if (!ReferenceEquals(db, built) || db!.m_recipes.Count != recipes)
        {
            Items.Clear(); built = db; recipes = db!.m_recipes.Count;
            foreach (var recipe in db.m_recipes)
                if (recipe && recipe.m_enabled && recipe.m_item && recipe.m_item.m_itemData?.m_shared?.m_maxQuality > 1)
                    Items.Add(recipe.m_item.gameObject.name);
        }
        return Items.Contains(prefab);
    }
}
