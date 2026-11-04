using System.Collections.Generic;

namespace YAVSOS.Core.Policy;

public sealed class CategoryDefinition
{
    public string Id { get; }
    public string DisplayName { get; }
    internal CategoryDefinition(string id, string displayName) => (Id, DisplayName) = (id, displayName);
}

// Policy data only; match precedence and classification are in YAVSOS.Core.Classification.
public sealed class CategoryAssignment
{
    public string Match { get; }
    public string Selector { get; }
    /// <summary>True for `&lt;member&gt;` lines, which replace the automatic categories; false for an `&lt;add&gt;`/`&lt;remove&gt;` edit (X48).</summary>
    public bool Replaces { get; }
    /// <summary>The `&lt;member&gt;` categories when it replaces, the `&lt;add&gt;` categories when it edits; sorted.</summary>
    public IReadOnlyList<string> Memberships { get; }
    /// <summary>The `&lt;remove&gt;` categories of an edit, applied before its adds; sorted, empty when it replaces.</summary>
    public IReadOnlyList<string> Removals { get; }
    public string? Primary { get; }
    internal CategoryAssignment(string match, string selector, bool replaces, IReadOnlyList<string> memberships,
        IReadOnlyList<string> removals, string? primary)
        => (Match, Selector, Replaces, Memberships, Removals, Primary) = (match, selector, replaces, memberships, removals, primary);
}

public sealed class CategoryPolicy
{
    /// <summary>
    /// The canonical form's schema, chosen by content (X48 option A): 2 when an assignment adds or removes, else 1, so a
    /// replace-only file keeps the bytes and fingerprint every 1.x reader accepts.
    /// </summary>
    public int Schema { get; }
    public long Revision { get; }
    public IReadOnlyList<CategoryDefinition> Categories { get; }
    public IReadOnlyList<CategoryAssignment> Assignments { get; }
    public string CanonicalXml { get; }
    public string Fingerprint { get; }
    internal CategoryPolicy(int schema, long revision, IReadOnlyList<CategoryDefinition> categories,
        IReadOnlyList<CategoryAssignment> assignments, string canonicalXml, string fingerprint)
        => (Schema, Revision, Categories, Assignments, CanonicalXml, Fingerprint) =
            (schema, revision, categories, assignments, canonicalXml, fingerprint);
}
