using System;
using System.Collections.Generic;
using System.Linq;

namespace YAVSOS.Core.Classification;

/// <summary>
/// Completes an item's final memberships against the taxonomy hierarchy (D051): every ancestor of a membership is
/// added, and every umbrella with no named child among them receives its Other subcategory. Afterwards an umbrella
/// holds everything in its subcategories, and the subcategories (Other included) together hold everything in it.
/// A fixed function of memberships and taxonomy: the server and each client apply it, and it never travels on the wire.
/// </summary>
public static class CategoryClosure
{
    /// <summary>Reserved source for derived rows. Classifier, policy and wire input must not use it.</summary>
    public const string Source = "taxonomy:closure";

    public static bool IsDerived(MembershipEvidence row) => row != null && StringComparer.Ordinal.Equals(row.Source, Source);

    /// <summary>Memberships plus the derived rows; input rows are returned unchanged and none of them may be derived.</summary>
    public static IReadOnlyList<MembershipEvidence> Apply(IReadOnlyList<MembershipEvidence> memberships, ClassificationTaxonomy taxonomy)
    {
        if (memberships == null || taxonomy == null) throw new ArgumentNullException(nameof(memberships));
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in memberships)
        {
            if (row == null || IsDerived(row)) throw new ArgumentException("Closure input cannot contain derived memberships.");
            taxonomy.Require(row.Category); present.Add(row.Category);
        }
        var derived = new List<MembershipEvidence>();
        foreach (var category in present.OrderBy(x => x, StringComparer.Ordinal).ToArray())
            for (var at = taxonomy.ParentOf(category); at != null; at = taxonomy.ParentOf(at))
                if (present.Add(at)) derived.Add(new MembershipEvidence(at, Source, "Umbrella of " + category + "."));
        // An Other is a leaf unless a policy gives an adopted Other row children, so this settles in a few passes.
        var pending = new Queue<string>(present.OrderBy(x => x, StringComparer.Ordinal));
        while (pending.Count > 0)
        {
            var umbrella = pending.Dequeue();
            var other = taxonomy.OtherOf(umbrella);
            if (other == null || taxonomy.ChildrenOf(umbrella).Any(present.Contains)) continue;
            present.Add(other); pending.Enqueue(other);
            derived.Add(new MembershipEvidence(other, Source, "No named subcategory of " + umbrella + "."));
        }
        if (derived.Count == 0) return memberships;
        var all = new List<MembershipEvidence>(memberships.Count + derived.Count);
        all.AddRange(memberships); all.AddRange(derived);
        return all;
    }

    /// <summary>The rows that were not derived; what the wire carries.</summary>
    public static IEnumerable<MembershipEvidence> Underived(IEnumerable<MembershipEvidence> memberships) => memberships.Where(x => !IsDerived(x));
}
