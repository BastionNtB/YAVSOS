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
    public IReadOnlyList<string> Memberships { get; }
    public string? Primary { get; }
    internal CategoryAssignment(string match, string selector, IReadOnlyList<string> memberships, string? primary)
        => (Match, Selector, Memberships, Primary) = (match, selector, memberships, primary);
}

public sealed class CategoryPolicy
{
    public long Revision { get; }
    public IReadOnlyList<CategoryDefinition> Categories { get; }
    public IReadOnlyList<CategoryAssignment> Assignments { get; }
    public string CanonicalXml { get; }
    public string Fingerprint { get; }
    internal CategoryPolicy(long revision, IReadOnlyList<CategoryDefinition> categories,
        IReadOnlyList<CategoryAssignment> assignments, string canonicalXml, string fingerprint)
        => (Revision, Categories, Assignments, CanonicalXml, Fingerprint) =
            (revision, categories, assignments, canonicalXml, fingerprint);
}
