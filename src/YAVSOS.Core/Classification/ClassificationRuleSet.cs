using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using YAVSOS.Core.Policy;

namespace YAVSOS.Core.Classification;

/// <summary>A single winning override applies to automatic evidence; selectors never cascade accidentally.</summary>
public sealed class ClassificationRuleSet
{
    private readonly IReadOnlyDictionary<string, ClassificationOverride> exact;
    private readonly ClassificationOverride[] globs;
    public ClassificationTaxonomy Taxonomy { get; }
    public string Fingerprint { get; }
    public string PolicyFingerprint { get; }
    public IReadOnlyList<ClassificationOverride> Overrides { get; }
    public ClassificationRuleSet(ClassificationTaxonomy taxonomy, IEnumerable<ClassificationOverride>? overrides = null, string policyFingerprint = "none")
    {
        Taxonomy = taxonomy ?? throw new ArgumentNullException(nameof(taxonomy));
        ClassificationValidation.Text(policyFingerprint, 128); PolicyFingerprint = policyFingerprint;
        var rows = ClassificationValidation.Bounded(overrides ?? Array.Empty<ClassificationOverride>(), 2048);
        if (rows.Any(x => x == null) || rows.Select(x => (x.Match, x.Selector)).Distinct().Count() != rows.Length)
            throw new ArgumentException("Duplicate or null classification override.");
        foreach (var row in rows)
        {
            foreach (var id in row.Memberships) taxonomy.Require(id);
            if (row.Primary != null) taxonomy.Require(row.Primary);
        }
        var ordered = rows.OrderBy(x => x.Match, StringComparer.Ordinal).ThenBy(x => x.Selector, StringComparer.Ordinal).ToArray();
        Overrides = Array.AsReadOnly(ordered);
        exact = new ReadOnlyDictionary<string, ClassificationOverride>(rows.Where(x => x.Match == "exact").ToDictionary(x => x.Selector, StringComparer.Ordinal));
        globs = rows.Where(x => x.Match == "glob").OrderByDescending(x => x.Pattern.Literals)
            .ThenBy(x => x.Pattern.Stars).ThenBy(x => x.Pattern.Questions).ThenBy(x => x.Selector, StringComparer.Ordinal).ToArray();
        Fingerprint = ClassificationValidation.Hash(writer => {
            writer.Write("yavsos-classification-rules-1"); writer.Write(taxonomy.Fingerprint); writer.Write(policyFingerprint); writer.Write(ordered.Length);
            foreach (var row in ordered)
            {
                writer.Write(row.Match); writer.Write(row.Selector); writer.Write((int)row.Operation); writer.Write(row.Primary ?? "");
                writer.Write(row.Memberships.Count); foreach (var id in row.Memberships) writer.Write(id);
                writer.Write(row.Facets.Count);
                foreach (var edit in row.Facets)
                {
                    writer.Write(edit.Dimension); writer.Write((int)edit.Operation); writer.Write(edit.Values.Count);
                    foreach (var value in edit.Values) writer.Write(value);
                }
            }
        });
    }
    // Schema 1 stores a complete assignment; add/remove/facet edits are detached contracts only.
    public static ClassificationRuleSet FromSchema1(CategoryPolicy policy, ClassificationTaxonomy? defaults = null)
    {
        if (policy == null) throw new ArgumentNullException(nameof(policy));
        var taxonomy = (defaults ?? DefaultClassificationTaxonomy.Create()).WithPolicy(policy);
        return new ClassificationRuleSet(taxonomy, policy.Assignments.Select(row =>
            new ClassificationOverride(row.Match, row.Selector, OverrideOperation.Replace, row.Memberships, row.Primary)), policy.Fingerprint);
    }
    internal ClassificationOverride? Select(string prefab, ClassificationWork work)
    {
        work.Spend();
        if (exact.TryGetValue(prefab, out var assignment)) return assignment;
        foreach (var glob in globs) if (glob.Pattern.Matches(prefab, work)) return glob;
        return null;
    }
}
