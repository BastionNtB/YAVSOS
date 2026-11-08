using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Core.Classification;

/// <summary>
/// X60 (D155, D160): one container piece type to read. Its answers come from the game's own <c>CanAddItem</c> on an
/// empty inventory with the piece's name and size, so two types with the same <see cref="AskKey"/> (name and size)
/// always get the same answers and are asked once.
/// </summary>
public sealed class ContainerTypeInfo
{
    /// <summary>The piece prefab's name: the type's identity on every player's game.</summary>
    public string Prefab { get; }
    /// <summary>The category's name as the player sees it ("Storehouse Timber Crib").</summary>
    public string Name { get; }
    /// <summary>What the answers depend on: the container's inventory name and size.</summary>
    public string AskKey { get; }
    /// <summary>The name of the mod that added the piece ("Storehouse"); null when unknown. D163: the tree's heading.</summary>
    public string? Mod { get; }
    /// <summary>The piece's own name ("Timber Crib"), shown under the mod's heading in the tree; null: <see cref="Name"/>.</summary>
    public string? Piece { get; }
    public ContainerTypeInfo(string prefab, string name, string askKey, string? mod = null, string? piece = null)
    {
        ClassificationValidation.Text(prefab, 2048);
        Prefab = prefab;
        Name = ContainerFilterSet.CategoryName(name, prefab);
        AskKey = askKey ?? throw new ArgumentNullException(nameof(askKey));
        Mod = string.IsNullOrWhiteSpace(mod) ? null : ContainerFilterSet.CategoryName(mod, prefab);
        Piece = string.IsNullOrWhiteSpace(piece) ? null : ContainerFilterSet.CategoryName(piece, prefab);
    }
}

/// <summary>
/// A container type's built-in filter (D160): the items an empty container of that type takes. Only types that refuse
/// some item have one. Its category (<see cref="Category"/>) has exactly these members.
/// </summary>
public sealed class ContainerFilter
{
    private readonly HashSet<string> members;
    public string Prefab { get; }
    public string Category { get; }
    public string Name { get; }
    /// <summary>The mod that added the type; null when unknown (<see cref="ContainerTypeInfo.Mod"/>).</summary>
    public string? Mod { get; }
    /// <summary>The piece's own name; null: <see cref="Name"/> (<see cref="ContainerTypeInfo.Piece"/>).</summary>
    public string? Piece { get; }
    public IReadOnlyCollection<string> Members => members;
    public int Count => members.Count;
    internal ContainerFilter(string prefab, string category, string name, IEnumerable<string> takes, string? mod = null, string? piece = null)
    {
        Prefab = prefab; Category = category; Name = name; Mod = mod; Piece = piece;
        members = new HashSet<string>(takes, StringComparer.Ordinal);
    }
    public bool Takes(string item) => item != null && members.Contains(item);

    /// <summary>
    /// D160, narrow only: a container whose type has a filter accepts what its YAVSOS rule accepts <em>and</em> its filter
    /// takes. A rule part outside the filter stays stored but doesn't count, so an Accept the filter refuses is a Reject.
    /// </summary>
    public static ChestRuleDecision Narrow(ChestRuleDecision decision, ContainerFilter? filter, string item) =>
        decision == ChestRuleDecision.Accept && filter != null && !filter.Takes(item) ? ChestRuleDecision.Reject : decision;

    /// <summary>The filter refuses the item; false without a filter.</summary>
    public static bool Refuses(ContainerFilter? filter, string item) => filter != null && !filter.Takes(item);
}

/// <summary>
/// X60 (D155, D160): every container type with a built-in filter, read once per launch and again when the server's
/// mod settings arrive. Each filter is a category, <c>store.&lt;prefab&gt;</c>, any chest's rule can tick, and a
/// filtering container with no YAVSOS rule acts as if its own category were ticked (<see cref="AutomaticPayload"/>).
/// Immutable; the game builds a new set and swaps it in.
/// </summary>
public sealed class ContainerFilterSet
{
    public const string CategoryPrefix = "store.";
    /// <summary>The source name of the evidence rows these categories add (never shown).</summary>
    public const string Source = "container-filter";
    /// <summary>The categories' display rank: after the built-in families, before Misc.</summary>
    public const int DisplayRank = 900;
    public const int MaximumFilters = 256;
    private static long generations;

    public static readonly ContainerFilterSet Empty = new ContainerFilterSet(Array.Empty<ContainerFilter>(), 0, 0);

    private readonly Dictionary<string, ContainerFilter> byPrefab = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContainerFilter> byCategory = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> byItem = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private ClassificationTaxonomy? extendedFrom, extended;
    private readonly Dictionary<(string Category, int Rank), string> automatic = new();

    public IReadOnlyList<ContainerFilter> Filters { get; }
    /// <summary>
    /// D163: the tree folds these categories under one heading per mod ("Storehouse stores (46)"), each shown by its
    /// piece's name there. The heading has no tick: ticking every store at once would accept almost everything.
    /// </summary>
    public CategoryGrouping Grouping { get; }
    /// <summary>Unique per set, so a consumer can tell sets apart without comparing members.</summary>
    public long Generation { get; }
    /// <summary>Container types read, filtering or not.</summary>
    public int Types { get; }
    /// <summary>Filters left out because the category set had no room (<see cref="ClassificationTaxonomy.MaximumNamedCategories"/>).</summary>
    public int Dropped { get; private set; }

    private ContainerFilterSet(IReadOnlyList<ContainerFilter> filters, int types, long generation)
    {
        Filters = filters; Types = types; Generation = generation;
        Grouping = filters.Count == 0 ? CategoryGrouping.None : new CategoryGrouping(filters.Select(x => (x.Category, x.Mod, x.Name, x.Piece)));
        foreach (var filter in filters)
        {
            byPrefab[filter.Prefab] = filter; byCategory[filter.Category] = filter;
        }
        foreach (var group in filters.SelectMany(f => f.Members.Select(m => (Item: m, f.Category))).GroupBy(x => x.Item, StringComparer.Ordinal))
            byItem[group.Key] = group.Select(x => x.Category).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// The set from each type's answers: a type that refuses at least one item is a filter (Emergency Shelves, plain
    /// chests and stores of a mod that isn't fully loaded take everything, so they are not). <paramref name="accepted"/>
    /// lists, per type, the items it took out of <paramref name="asked"/> items.
    /// </summary>
    public static ContainerFilterSet From(IEnumerable<(ContainerTypeInfo Type, IReadOnlyCollection<string> Accepted)> answers, int asked)
    {
        if (answers == null) throw new ArgumentNullException(nameof(answers));
        var rows = answers.OrderBy(x => x.Type.Prefab, StringComparer.Ordinal).ToList();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var prefabs = new HashSet<string>(StringComparer.Ordinal);
        var filters = new List<ContainerFilter>();
        foreach (var (type, accepted) in rows)
        {
            if (!prefabs.Add(type.Prefab)) continue;
            if (accepted.Count >= asked) continue; // Takes everything: no filter.
            if (filters.Count == MaximumFilters) break;
            var id = CategoryId(type.Prefab);
            if (!ids.Add(id)) { id = CategoryId(type.Prefab, hashed: true); if (!ids.Add(id)) continue; }
            filters.Add(new ContainerFilter(type.Prefab, id, type.Name, accepted, type.Mod, type.Piece));
        }
        return new ContainerFilterSet(filters.AsReadOnly(), prefabs.Count, Interlocked.Increment(ref generations));
    }

    public ContainerFilter? ForPrefab(string prefab) => prefab != null && byPrefab.TryGetValue(prefab, out var filter) ? filter : null;
    public ContainerFilter? ForCategory(string category) => category != null && byCategory.TryGetValue(category, out var filter) ? filter : null;
    /// <summary>The filter categories whose type takes <paramref name="item"/>, in ID order; empty for none.</summary>
    public IReadOnlyList<string> CategoriesOf(string item) => item != null && byItem.TryGetValue(item, out var ids) ? ids : Array.Empty<string>();

    public static bool IsFilterCategory(string id) => id != null && id.StartsWith(CategoryPrefix, StringComparison.Ordinal);

    /// <summary>
    /// The category ID of a piece prefab: <c>store.</c> and the prefab name in lower case, other characters as <c>_</c>.
    /// Every player derives the same ID from the same prefab. A name that is too long or collides gets a hash.
    /// </summary>
    public static string CategoryId(string prefab, bool hashed = false)
    {
        if (prefab == null) throw new ArgumentNullException(nameof(prefab));
        var text = new StringBuilder(prefab.Length);
        foreach (var c in prefab.ToLowerInvariant()) text.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' ? c : '_');
        var name = text.ToString();
        const int Room = 64 - 6; // CategoryPrefix
        if (!hashed && name.Length != 0 && name.Length <= Room) return CategoryPrefix + name;
        var hash = Fnv(prefab).ToString("x8");
        return CategoryPrefix + (name.Length > Room - 9 ? name.Substring(0, Room - 9) : name) + "-" + hash;
    }

    /// <summary>A display name the category set accepts: trimmed, one line, at most 128 characters; the prefab when empty.</summary>
    internal static string CategoryName(string? name, string prefab)
    {
        var text = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (text.Length > 128) text = text.Substring(0, 128).TrimEnd();
        if (text.Length != 0 && char.IsHighSurrogate(text[text.Length - 1])) text = text.Substring(0, text.Length - 1);
        return text.Length == 0 ? (prefab.Length <= 128 ? prefab : prefab.Substring(0, 128)) : text;
    }

    private static uint Fnv(string text)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(text)) hash = (hash ^ b) * 16777619u;
        return hash;
    }

    /// <summary>
    /// <paramref name="baseTaxonomy"/> with a category per filter, as roots after the built-in families. An ID the base
    /// set already has (a server's category file) stays the server's; filters beyond the category limit are left out
    /// (<see cref="Dropped"/>). The same base gives the same object, so caches keyed on the taxonomy hold.
    /// </summary>
    public ClassificationTaxonomy Extend(ClassificationTaxonomy baseTaxonomy)
    {
        if (baseTaxonomy == null) throw new ArgumentNullException(nameof(baseTaxonomy));
        if (Filters.Count == 0) return baseTaxonomy;
        lock (gate)
        {
            if (ReferenceEquals(extendedFrom, baseTaxonomy)) return extended!;
            var rows = baseTaxonomy.Categories.Values.Where(x => !x.Generated).ToList();
            var room = ClassificationTaxonomy.MaximumNamedCategories - rows.Count;
            var dropped = 0;
            foreach (var filter in Filters)
            {
                if (baseTaxonomy.Categories.ContainsKey(filter.Category)) continue;
                if (room <= 0) { dropped++; continue; }
                rows.Add(new ClassificationCategory(filter.Category, filter.Name, DisplayRank)); room--;
            }
            Dropped = dropped;
            extended = new ClassificationTaxonomy(rows); extendedFrom = baseTaxonomy;
            automatic.Clear();
            return extended;
        }
    }

    /// <summary>
    /// D155: the rule a filtering container with no YAVSOS rule acts as if it had: its own category ticked, at
    /// <paramref name="rank"/> (the stored priority; the panel's level is <c>ChestPriority.Level</c>). Nothing is saved;
    /// a player's first edit saves a normal rule starting from it. Null when the category isn't in the set.
    /// </summary>
    public string? AutomaticPayload(ContainerFilter filter, int rank, ClassificationTaxonomy baseTaxonomy)
    {
        if (filter == null) throw new ArgumentNullException(nameof(filter));
        var taxonomy = Extend(baseTaxonomy);
        if (!taxonomy.Categories.ContainsKey(filter.Category) || !ReferenceEquals(ForCategory(filter.Category), filter)) return null;
        lock (gate)
        {
            if (automatic.TryGetValue((filter.Category, rank), out var known)) return known;
            var payload = ContainerPolicyEnvelope.Serialize(0, new ChestRules(taxonomy, ChestRuleState.Configured,
                new[] { new ClassificationFilterRule(taxonomy, new[] { filter.Category }) }, priority: rank));
            automatic[(filter.Category, rank)] = payload;
            return payload;
        }
    }
}

/// <summary>
/// X60: an incremental read of every container type, a few asks per call, so the game spreads it over frames (D155).
/// Types with the same <see cref="ContainerTypeInfo.AskKey"/> are asked once. <c>ask(type, item)</c> is the game's
/// answer for type index and item index; null means it couldn't ask (the capability check failed), which stops the
/// read: <see cref="Failed"/>, no set (D058: the categories and automatic rules turn off together).
/// </summary>
public sealed class ContainerFilterBuild
{
    private readonly IReadOnlyList<ContainerTypeInfo> types;
    private readonly IReadOnlyList<string> items;
    private readonly Func<int, int, bool?> ask;
    private readonly List<int> representatives = new();
    private readonly Dictionary<int, List<string>> accepted = new();
    private readonly int[] representativeOf;
    private int type, item;
    public bool Done { get; private set; }
    public bool Failed { get; private set; }
    public long Asks { get; private set; }
    public ContainerFilterSet? Result { get; private set; }
    /// <summary>Distinct name-and-size groups asked.</summary>
    public int Groups => representatives.Count;

    public ContainerFilterBuild(IReadOnlyList<ContainerTypeInfo> types, IReadOnlyList<string> items, Func<int, int, bool?> ask)
    {
        this.types = types ?? throw new ArgumentNullException(nameof(types));
        this.items = items ?? throw new ArgumentNullException(nameof(items));
        this.ask = ask ?? throw new ArgumentNullException(nameof(ask));
        representativeOf = new int[types.Count];
        var first = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < types.Count; i++)
        {
            if (!first.TryGetValue(types[i].AskKey, out var at)) { first.Add(types[i].AskKey, at = i); representatives.Add(i); accepted.Add(i, new List<string>()); }
            representativeOf[i] = at;
        }
        if (items.Count == 0 || representatives.Count == 0) Finish();
    }

    /// <summary>Asks while <paramref name="more"/> says so (checked before each ask); true once done or failed.</summary>
    public bool Step(Func<bool> more)
    {
        if (more == null) throw new ArgumentNullException(nameof(more));
        while (!Done && more())
        {
            var at = representatives[type];
            var answer = ask(at, item);
            Asks++;
            if (answer == null) { Failed = true; Done = true; return true; }
            if (answer.Value) accepted[at].Add(items[item]);
            if (++item == items.Count) { item = 0; if (++type == representatives.Count) Finish(); }
        }
        return Done;
    }

    private void Finish()
    {
        Done = true;
        Result = ContainerFilterSet.From(Enumerable.Range(0, types.Count)
            .Select(i => (types[i], (IReadOnlyCollection<string>)(accepted.TryGetValue(representativeOf[i], out var list) ? list : new List<string>()))), items.Count);
    }
}

/// <summary>Category answers that also say whether the container filters' categories are still pending (X60).</summary>
public interface IFilterCategoryState
{
    /// <summary>
    /// The filter categories aren't known yet (the read hasn't finished this session) or can't be (the game's check is
    /// off): a rule part naming one waits, as category parts do while categories load (D048), so nothing moves on its
    /// account. False once the set is read, when an ID it lacks is an ordinary unknown category (D050).
    /// </summary>
    bool FilterCategoriesPending { get; }
}

/// <summary>
/// X60: the category answers with each item's filter categories added, so rules, ranking, search and the planners see
/// "Storehouse Timber Crib" as an ordinary category. The answer for an item is built once per generation, so callers
/// that compare answer objects within one generation still can. <see cref="Generation"/> changes whenever the inner
/// answers' generation, the filter set or the pending state changes. Single-threaded (Unity game thread) use only.
/// </summary>
public sealed class ContainerFilterAnswers : ICategoryAnswers, IFilterCategoryState
{
    private static long generations = long.MinValue / 2;
    private ICategoryAnswers? inner;
    private ContainerFilterSet filters = ContainerFilterSet.Empty;
    private bool pending = true;
    private long innerGeneration, generation = Interlocked.Increment(ref generations);
    private readonly Dictionary<string, ClassificationResult?> results = new(StringComparer.Ordinal);

    /// <summary>Points this view at the current answers and filters; returns itself.</summary>
    public ContainerFilterAnswers Over(ICategoryAnswers inner, ContainerFilterSet? filters, bool pending)
    {
        filters ??= ContainerFilterSet.Empty;
        if (!ReferenceEquals(this.inner, inner) || !ReferenceEquals(this.filters, filters) || this.pending != pending)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            this.filters = filters; this.pending = pending;
            Renew();
        }
        return this;
    }

    public ICategoryAnswers? Inner => inner;
    public ContainerFilterSet Filters => filters;

    private void Renew()
    {
        innerGeneration = inner?.Generation ?? 0;
        generation = Interlocked.Increment(ref generations);
        results.Clear();
    }

    private void Fresh() { if (inner != null && inner.Generation != innerGeneration) Renew(); }

    public long Generation { get { Fresh(); return generation; } }
    public bool CategoriesReady => inner?.CategoriesReady == true;
    public bool FilterCategoriesPending => pending;

    public ClassificationResult? Resolve(string prefab)
    {
        if (inner == null) return null;
        Fresh();
        if (filters.Filters.Count == 0) return inner.Resolve(prefab);
        if (results.TryGetValue(prefab, out var known)) return known;
        var result = inner.Resolve(prefab);
        var categories = filters.CategoriesOf(prefab);
        if (result != null && categories.Count != 0)
            result = new ClassificationResult(result.Prefab, result.Primary, result.Quest, result.HasIcon, result.CatalogOnly, result.InstanceRarity,
                result.SelectedOverride, result.Memberships.Concat(categories.Select(x => new MembershipEvidence(x, ContainerFilterSet.Source,
                    "Taken by container type " + filters.ForCategory(x)!.Prefab))), result.Facets);
        if (results.Count < 65536) results[prefab] = result;
        return result;
    }

    /// <summary>Current members of <paramref name="category"/> when it is a filter category; null otherwise.</summary>
    public int? FilterMembers(string category) => filters.ForCategory(category)?.Count;
}
