using System;
using BepInEx.Logging;
using ServerSync;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Policy;

namespace YAVSOS.Networking;

/// <summary>ServerSync supplies authenticated/session-guarded delivery; this supplies whole-snapshot validation.</summary>
internal sealed class AssignmentHost : IDisposable
{
    internal const string SyncIdentifier = "authoritative-assignments-v1";
    private readonly ConfigSync sync;
    private readonly CustomSyncedValue<string> value;
    private readonly PolicyLifecycle policy;
    private readonly ManualLogSource log;
    private readonly Func<CatalogSnapshot?> getCatalog;
    private readonly Func<ClassifiedCatalog?> getClassification;
    private readonly Func<ClassificationSourceSnapshot?> getSources;
    private readonly AssignmentExchange exchange = new AssignmentExchange();
    private readonly AssignmentEnvelopeReceiver receiver = new AssignmentEnvelopeReceiver();
    private string? rejectedLogged;
    private readonly RetainedCategoryAnswers answers = new RetainedCategoryAnswers();
    private long session;
    private CatalogSnapshot? lastCatalog;
    private CategoryPolicy? lastPolicy;
    private ClassifiedCatalog? lastClassification;
    private ClassificationSourceSnapshot? lastSources;
    private AuthoritativeAssignmentSnapshot? identity;
    private CatalogSnapshot? identityCatalog;
    private CategoryPolicy? identityPolicy;
    internal bool IdentityReady => !disposed && policy.PolicyReady && identity?.IdentityReady == true &&
        ReferenceEquals(identityCatalog, getCatalog()) && ReferenceEquals(identityPolicy, policy.Effective);
    private string incoming = "", published = "", reported = "";
    private bool disposed;
    private bool hasPublished;
    private string serverSession = "";
    private long publicationSequence;
    // Support/cost diagnostic (P3-08): how long current category answers were missing, from a session start,
    // withdrawal or rebuild until categories are ready again.
    private double categoriesMissingSince = -1;
    private bool categoriesMissingInRebuild;
    internal AuthoritativeAssignmentSnapshot? Current => policy.PolicyReady &&
        ReferenceEquals(lastCatalog, getCatalog()) && ReferenceEquals(lastPolicy, policy.Effective) &&
        (policy.Mode != PolicyMode.ManagedServer && policy.Mode != PolicyMode.Standalone && policy.Mode != PolicyMode.UnmanagedLocal ||
            ReferenceEquals(lastClassification, getClassification()) && ReferenceEquals(lastSources, getSources()))
        ? exchange.Current?.Snapshot : null;

    // Category answers for chest rules (D049): kept through catalog/source rebuilds, withdrawn on a session,
    // policy/category-set change or a new server publication.
    internal ICategoryAnswers Answers => answers;
    internal int CategoryMemberCount(string category) => answers.MemberCount(category);
    internal bool CategoriesRebuilding => answers.Rebuilding;

    internal AssignmentHost(ConfigSync sync, PolicyLifecycle policy, ManualLogSource log,
        Func<CatalogSnapshot?> getCatalog, Func<ClassifiedCatalog?> getClassification, Func<ClassificationSourceSnapshot?> getSources)
    {
        this.sync = sync; this.policy = policy; this.log = log; this.getCatalog = getCatalog;
        this.getClassification = getClassification; this.getSources = getSources;
        value = new CustomSyncedValue<string>(sync, SyncIdentifier, ""); value.ValueChanged += OnRemoteChanged;
    }
    internal void BeginSession(bool active)
    {
        identity = null; identityCatalog = null; identityPolicy = null;
        exchange.BeginSession(active); incoming = published = reported = "";
        receiver.Reset(); serverSession = Guid.NewGuid().ToString("N"); publicationSequence = 0; hasPublished = false;
        lastCatalog = null; lastPolicy = null; lastClassification = null; lastSources = null;
        session++; answers.SetScope(session, ""); categoriesMissingSince = -1;
        value.AssignLocalValue(""); policy.SetAssignmentEvidence(null, false);
    }
    internal void Invalidate() { identity = null; InvalidateCategories(); }
    internal void InvalidateCategories()
    {
        exchange.Invalidate(); policy.SetAssignmentEvidence(null, false); answers.BeginRebuild();
        // Withdraw the current snapshot promptly; chest rules keep the last answers until the rebuild
        // publishes (D049). Publishing is coalesced in Update.
        lastCatalog = null; lastPolicy = null;
    }
    private void OnRemoteChanged()
    {
        if (disposed || sync.IsSourceOfTruth || policy.Mode is PolicyMode.Offline or PolicyMode.Standalone or PolicyMode.ManagedServer) return;
        if (!receiver.Receive(value.Value)) return;
        // B4.8, D087: a malformed server value withdraws every category; say why, once per distinct cause.
        if (receiver.Rejected && receiver.Diagnostic != rejectedLogged)
        {
            rejectedLogged = receiver.Diagnostic;
            log.LogWarning(receiver.Diagnostic + ". Categories are withdrawn until the server sends a valid value; a server and client on different YAVSOS versions can cause this.");
        }
        else if (!receiver.Rejected) rejectedLogged = null;
        identity = null; incoming = receiver.Payload; exchange.Invalidate(); policy.SetAssignmentEvidence(null, false);
        answers.Withdraw();
    }
    internal void Update()
    {
        if (disposed) return;
        var local = policy.Mode is PolicyMode.ManagedServer or PolicyMode.Standalone or PolicyMode.UnmanagedLocal;
        lastCatalog = getCatalog(); lastPolicy = policy.PolicyReady ? policy.Effective : null;
        lastClassification = local ? getClassification() : null; lastSources = local ? getSources() : null;
        exchange.SetInputs(local, lastCatalog, lastPolicy, local ? lastClassification : null, local ? "" : incoming);
        exchange.Tick();
        var accepted = Current;
        answers.SetScope(session, lastPolicy?.Fingerprint ?? "");
        // D086 M6 (B2.1): while the new catalog is current but classification is not, the exchange holds an identity-only
        // snapshot. With retained answers, it neither replaces them nor goes out to clients; retention ends when
        // classification is ready (D049).
        var interim = accepted != null && !accepted.CategoriesReady && answers.Rebuilding && answers.CategoriesReady;
        if (accepted != null && lastCatalog != null)
        {
            identity = accepted; identityCatalog = lastCatalog; identityPolicy = lastPolicy;
            var update = interim ? CategoryAnswerUpdate.Unchanged : answers.Accept(accepted, lastCatalog, session);
            if (accepted.CategoriesReady && categoriesMissingSince >= 0)
            {
                log.LogInfo($"Categories ready {(UnityEngine.Time.realtimeSinceStartupAsDouble - categoriesMissingSince) * 1000.0:F0} ms after " +
                    (categoriesMissingInRebuild ? "the rebuild started" : "they were withdrawn or the session started") + $" ({update}).");
                categoriesMissingSince = -1;
            }
        }
        else
        {
            // Any rebuild path, including registry count changes. A stage that fails keeps the retained answers until
            // the session or policy changes (D086 D7), instead of withdrawing them.
            answers.BeginRebuild();
        }
        if (accepted?.CategoriesReady != true && categoriesMissingSince < 0)
        { categoriesMissingSince = UnityEngine.Time.realtimeSinceStartupAsDouble; categoriesMissingInRebuild = answers.Rebuilding; }
        policy.SetAssignmentEvidence(accepted?.Fingerprint, accepted?.CategoriesReady == true);
        if (policy.Mode == PolicyMode.ManagedServer && sync.IsSourceOfTruth && !ConfigSync.ProcessingServerUpdate)
        {
            // While a rebuild runs, clients keep the last publication; an identical result republishes nothing.
            var payload = accepted != null && !interim ? exchange.Current!.Wire : answers.Rebuilding ? published : "";
            if (!hasPublished || published != payload)
            {
                published = payload; hasPublished = true;
                value.Value = AssignmentEnvelope.Encode(serverSession, checked(++publicationSequence), payload);
            }
        }
        var reportIdentity = accepted?.Fingerprint ?? exchange.Diagnostic;
        if (reportIdentity == reported) return;
        reported = reportIdentity;
        var diagnostic = accepted != null ? "Assignment evidence ready: " + accepted.Fingerprint : exchange.Diagnostic;
        if (accepted != null) log.LogInfo(diagnostic + ". Capabilities=" + accepted.Capabilities + ".");
        else if (diagnostic.Contains("rejected") || diagnostic.Contains("deadline")) log.LogWarning(diagnostic);
    }
    public void Dispose()
    { if (disposed) return; disposed = true; identity = null; answers.Withdraw(); value.ValueChanged -= OnRemoteChanged; exchange.Dispose(); policy.SetAssignmentEvidence(null, false); }
}
