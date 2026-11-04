using System;
using System.Collections.Generic;
using System.Linq;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Messages;

namespace YAVSOS.Core.Interface;

/// <summary>
/// One stack in the open chest (B2-11: the chest's own contents, not the player's), for <see cref="RuleSuggestion"/>:
/// its item, the categories the item belongs to (its memberships; ancestors are added here), and whether the chest's
/// rule accepts it or marks it Never.
/// </summary>
public sealed class SuggestionStack
{
    public string Prefab { get; }
    public IReadOnlyList<string> Categories { get; }
    public bool Accepted { get; }
    public bool Never { get; }

    public SuggestionStack(string prefab, IEnumerable<string>? categories, bool accepted, bool never = false)
    {
        Prefab = prefab ?? throw new ArgumentNullException(nameof(prefab));
        Categories = (categories ?? Array.Empty<string>()).ToArray(); Accepted = accepted; Never = never;
    }
}

/// <summary>What X22 offers for one chest: a category for a chest without a rule, and the items its rule misses. Immutable.</summary>
public sealed class RuleSuggestions
{
    public static readonly RuleSuggestions None = new RuleSuggestions(null, 0, 0, Array.Empty<string>());

    /// <summary>The category "✦ Set rule" offers, or null.</summary>
    public string? Category { get; }
    /// <summary>The stacks in <see cref="Category"/>, and every stack in the chest.</summary>
    public int Stacks { get; }
    public int Total { get; }
    /// <summary>
    /// The items "Always: these items" marks Always: each different item in the chest the rule doesn't accept,
    /// in the order the chest holds them; items marked Never stay out (the player chose that). Empty: no chip.
    /// </summary>
    public IReadOnlyList<string> Missed { get; }
    public bool Any => Category != null || Missed.Count != 0;
    /// <summary>What is offered, so a dismissed offer comes back only when it changes.</summary>
    public string Signature => (Category ?? "") + "|" + string.Join(",", Missed);

    internal RuleSuggestions(string? category, int stacks, int total, IReadOnlyList<string> missed)
    { Category = category; Stacks = stacks; Total = total; Missed = missed; }
}

/// <summary>
/// X22 (P10-08, D130 variant A, D132): suggestions for the open chest's rule, from what the chest holds. "✦ Set rule:
/// {category}" on a chest without a rule when one category holds at least <see cref="Percent"/> % of its stacks (and at
/// least <see cref="MinimumStacks"/>); and "✓ Always: these items" on any chest whose rule misses some of
/// what it holds. Both save through the panel's normal rule write. Pure and Unity-free.
/// </summary>
public static class RuleSuggestion
{
    /// <summary>D130: 60 % of stacks, at least 1 stack. Stacks, not units, as a player counts them in the grid.</summary>
    public const int Percent = 60, MinimumStacks = 1;
    /// <summary>The Always chip's tooltip names this many items, then "+N more".</summary>
    public const int Listed = 12;

    /// <param name="hasRule">The chest has a rule: no category is suggested (the wireframe's choice; D130 left it open).</param>
    /// <param name="memberCount">How many items a category has, for ties (D054: the smaller is more specific); null: no such tie-break.</param>
    public static RuleSuggestions Find(ClassificationTaxonomy taxonomy, IReadOnlyList<SuggestionStack> stacks, bool hasRule, Func<string, int>? memberCount = null)
    {
        if (taxonomy == null) throw new ArgumentNullException(nameof(taxonomy));
        if (stacks == null) throw new ArgumentNullException(nameof(stacks));
        var missed = stacks.Where(x => !x.Accepted && !x.Never).Select(x => x.Prefab).Distinct(StringComparer.Ordinal).ToList();
        string? best = null; var bestCount = 0;
        if (!hasRule && stacks.Count >= MinimumStacks)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var stack in stacks)
            {
                // A stack counts once for each category it belongs to, directly or through a subcategory.
                var held = new HashSet<string>(StringComparer.Ordinal);
                foreach (var category in stack.Categories)
                    for (string? at = category; at != null && taxonomy.Categories.ContainsKey(at) && held.Add(at); at = taxonomy.ParentOf(at)) { }
                foreach (var category in held) counts[category] = counts.TryGetValue(category, out var n) ? n + 1 : 1;
            }
            foreach (var pair in counts)
            {
                if (pair.Value < MinimumStacks || (long)pair.Value * 100 < (long)stacks.Count * Percent) continue;
                if (best == null || Better(taxonomy, pair.Key, pair.Value, best, bestCount, memberCount)) { best = pair.Key; bestCount = pair.Value; }
            }
        }
        return best == null && missed.Count == 0 ? RuleSuggestions.None : new RuleSuggestions(best, bestCount, stacks.Count, missed.AsReadOnly());
    }

    // The deepest category wins; then the one holding more stacks; then the smaller category (D054); then the ID.
    private static bool Better(ClassificationTaxonomy taxonomy, string id, int count, string best, int bestCount, Func<string, int>? memberCount)
    {
        var depth = taxonomy.DepthOf(id); var bestDepth = taxonomy.DepthOf(best);
        if (depth != bestDepth) return depth > bestDepth;
        if (count != bestCount) return count > bestCount;
        if (memberCount != null)
        {
            var size = memberCount(id); var bestSize = memberCount(best);
            if (size != bestSize) return size < bestSize;
        }
        return StringComparer.Ordinal.Compare(id, best) < 0;
    }

    /// <summary>"Set rule: {category}" (D130: no a/an choice for translators); the panel draws ✦ as an icon.</summary>
    public static Message SetText(string category) => Message.Of("yavsos_suggest_set").With("category", category ?? "");

    public static Tooltip SetTip(string category, int stacks, int total) => Tooltip.Of(SetText(category),
        Message.Of("yavsos_suggest_set_tip").With("category", category ?? "").With("stacks", stacks).Count(total));

    /// <summary>"Always: these items" (D139, was "Always accept current contents" in D132); the panel draws ✓ as an icon.</summary>
    public static Message AlwaysText => Message.Of("yavsos_suggest_always");

    /// <param name="names">The missed items' names in the player's language, escaped for rich text by the caller.</param>
    public static Tooltip AlwaysTip(IReadOnlyList<string> names, bool hasRule)
    {
        if (names == null) throw new ArgumentNullException(nameof(names));
        var shown = names.Distinct(StringComparer.CurrentCulture).ToList();
        var list = MessageList.Of(ListKind.Comma, shown.Take(Listed)).WithMore(Math.Max(0, shown.Count - Listed));
        return Tooltip.Of(AlwaysText, Message.Of(hasRule ? "yavsos_suggest_always_tip_rule" : "yavsos_suggest_always_tip").With("items", list, Tone.Label));
    }

    /// <summary>The ✕ chip: hides the suggestions on this chest until the player leaves the world, or until they change.</summary>
    public static Tooltip DismissTip => Tooltip.Of(Message.Of("yavsos_suggest_dismiss"), Message.Of("yavsos_suggest_dismiss_tip"));

    /// <summary>
    /// The two reserved suggestion rows (Q12, D139, variant B): with a Set rule chip, Set rule and ✕ share the first row
    /// and Always has the second to itself; without one, Always and ✕ share the first and the second stays empty. Each
    /// chip keeps its width unless it is wider than its row; the panel then cuts its text with "…", the tooltip holding
    /// the whole text. Zero: no chip. <c>AlwaysRow</c> is 0 or 1.
    /// </summary>
    public static (float Set, float Always, int AlwaysRow) Rows(float set, float always, float dismiss, float row, float gap)
    {
        var beside = Math.Max(0, row - dismiss - gap); // A row that also holds ✕.
        if (set > 0) return (Math.Min(set, beside), always > 0 ? Math.Min(always, row) : 0, 1);
        return (0, always > 0 ? Math.Min(always, beside) : 0, 0);
    }
}
