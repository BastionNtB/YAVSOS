using System;
using System.Collections.Generic;
using YAVSOS.Core.Catalog;

namespace YAVSOS.Core.Classification;

public enum CategoryAnswerUpdate { Replaced, Unchanged, RejectedLate }

/// <summary>
/// D049: the last accepted category answers stay in use while a catalog or source rebuild runs. An identical
/// result changes nothing. A session, policy or category-set change (the scope) and a new server publication
/// withdraw them immediately. A rebuild stage that fails keeps them until one of those happens (D086 D7); there is no
/// time limit. Answers never exceed the last authoritative snapshot; items absent from the retained local catalog
/// resolve to nothing until a rebuild publishes. Single-threaded use only.
/// </summary>
public sealed class RetainedCategoryAnswers : ICategoryAnswers
{
    private readonly Dictionary<string, ClassificationResult?> resolved = new Dictionary<string, ClassificationResult?>(StringComparer.Ordinal);
    private AuthoritativeAssignmentSnapshot? snapshot;
    private CatalogSnapshot? catalog;
    private long session = long.MinValue;
    private string policy = "";
    private Dictionary<string, int>? counts;

    public long Generation { get; private set; }
    public bool CategoriesReady => snapshot?.CategoriesReady == true;
    /// <summary>Retained answers are in use while a rebuild runs.</summary>
    public bool Rebuilding { get; private set; }
    public string Fingerprint => snapshot?.Fingerprint ?? "";

    /// <summary>The withdrawal scope. Any change withdraws the retained answers.</summary>
    public void SetScope(long currentSession, string policyFingerprint)
    {
        if (policyFingerprint == null) throw new ArgumentNullException(nameof(policyFingerprint));
        if (session == currentSession && StringComparer.Ordinal.Equals(policy, policyFingerprint)) return;
        session = currentSession; policy = policyFingerprint; Withdraw();
    }

    public void Withdraw()
    {
        Rebuilding = false;
        if (snapshot == null) return;
        snapshot = null; catalog = null; Changed();
    }

    /// <summary>
    /// A catalog, source or classification rebuild is running, or one of its stages failed; keep answering from the
    /// retained snapshot until a result is accepted or the scope changes (D049, D086 D7).
    /// </summary>
    public void BeginRebuild()
    {
        if (snapshot != null) Rebuilding = true;
    }

    public CategoryAnswerUpdate Accept(AuthoritativeAssignmentSnapshot next, CatalogSnapshot local, long expectedSession)
    {
        if (next == null) throw new ArgumentNullException(nameof(next));
        if (local == null) throw new ArgumentNullException(nameof(local));
        if (expectedSession != session || policy.Length == 0 || !StringComparer.Ordinal.Equals(next.PolicyFingerprint, policy))
            return CategoryAnswerUpdate.RejectedLate;
        Rebuilding = false;
        if (snapshot != null && StringComparer.Ordinal.Equals(snapshot.Fingerprint, next.Fingerprint) &&
            StringComparer.Ordinal.Equals(catalog!.Fingerprint, local.Fingerprint))
        {
            // Same content: adopt the new instances so the old ones can be released, with no visible change.
            snapshot = next; catalog = local; counts = null;
            return CategoryAnswerUpdate.Unchanged;
        }
        snapshot = next; catalog = local; Changed();
        return CategoryAnswerUpdate.Replaced;
    }

    /// <summary>Server-classified members of a category, closure included; 0 while no answers are held (D054).</summary>
    public int MemberCount(string category)
    {
        if (category == null) throw new ArgumentNullException(nameof(category));
        if (snapshot == null) return 0;
        counts ??= CategorySelection.MemberCounts(snapshot.Items.Values);
        return counts.TryGetValue(category, out var count) ? count : 0;
    }

    public ClassificationResult? Resolve(string prefab)
    {
        if (prefab == null) throw new ArgumentNullException(nameof(prefab));
        if (snapshot == null) return null;
        if (resolved.TryGetValue(prefab, out var answer)) return answer;
        answer = snapshot.Resolve(prefab, catalog!);
        // Only loaded item identities are remembered, which bounds the memo by the catalog.
        if (answer != null || catalog!.Items.ContainsKey(prefab)) resolved.Add(prefab, answer);
        return answer;
    }

    private void Changed() { Generation++; resolved.Clear(); counts = null; }
}
