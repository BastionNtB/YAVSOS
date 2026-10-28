using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Core.Tests;

public sealed class ContainerPolicyEnvelopeTests
{
    private static readonly ClassificationTaxonomy Taxonomy = DefaultClassificationTaxonomy.Create();
    private static string Payload(ulong revision = 1) => ContainerPolicyEnvelope.Serialize(revision,
        new ChestRules(Taxonomy, ChestRuleState.Configured, includes: new[] { "Wood" }));
    private static ChestRuleEdit Exact(string prefab) => new ChestRuleEdit(null, new[] { prefab }, null);
    [Fact]
    public void Envelope_RoundTrip_ReusesDetachedRuleSemanticsAndExactOrdinalPrefab()
    {
        var rules = new ChestRules(Taxonomy, ChestRuleState.Configured, includes: new[] { "Quoted<&\"Identifier" });
        var payload = ContainerPolicyEnvelope.Serialize(ulong.MaxValue, rules);
        var read = ContainerPolicyEnvelope.Read(payload, Taxonomy);
        Assert.Equal(ContainerPolicyReadState.Supported, read.State); Assert.Equal(ulong.MaxValue, read.Revision);
        Assert.Equal(rules.Fingerprint, read.Rules!.Fingerprint); Assert.Equal(payload, read.OriginalPayload);
        Assert.True(read.HasRule); Assert.Equal(new[] { "Quoted<&\"Identifier" }, read.Rules.Includes);
    }
    [Fact]
    public void Envelope_SetAndClear_RequireExactObservedPayloadAndIncrementRevision()
    {
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit("", "", Taxonomy, Exact("Wood"), out var first));
        var read = ContainerPolicyEnvelope.Read(first!, Taxonomy); Assert.Equal(1UL, read.Revision); Assert.True(read.HasRule);
        Assert.Equal(new[] { "Wood" }, read.Rules!.Includes);
        Assert.Equal(TransferRefusal.PolicyChanged, ContainerPolicyEnvelope.TryEdit(first!, "", Taxonomy, Exact("Stone"), out var stale)); Assert.Null(stale);
        // D086 M1: clearing removes the rule, so every reader sees an unruled chest.
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit(first!, first!, Taxonomy, null, out var cleared));
        Assert.Equal("", cleared); Assert.Equal(ContainerPolicyReadState.Absent, ContainerPolicyEnvelope.Read(cleared!, Taxonomy).State);
        Assert.Equal(TransferRefusal.PolicyChanged, ContainerPolicyEnvelope.TryEdit(cleared!, first!, Taxonomy, Exact("Stone"), out stale)); Assert.Null(stale);
    }
    [Fact]
    public void Envelope_PriorityIsEditedOrKept_AndBounded()
    {
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit("", "", Taxonomy, new ChestRuleEdit(null, new[] { "Wood" }, null, priority: 3), out var first));
        Assert.Equal(3, ContainerPolicyEnvelope.Read(first!, Taxonomy).Rules!.Priority);
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit(first!, first!, Taxonomy, Exact("Stone"), out var kept));
        var read = ContainerPolicyEnvelope.Read(kept!, Taxonomy).Rules!;
        Assert.Equal(3, read.Priority); Assert.Equal(3, ChestRuleEdit.From(read).Priority);
        // D086 D5: Clear removes every part, the priority too.
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit(kept!, kept!, Taxonomy, null, out var cleared));
        Assert.Equal("", cleared);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChestRuleEdit(null, null, null, priority: ChestRuleEdit.MaximumPriority + 1));
        // B1.12: a stored priority outside the editor's range (another writer) is shown as the nearest one, not a crash.
        var wide = new ChestRules(Taxonomy, ChestRuleState.Configured, includes: new[] { "Wood" }, priority: 5000);
        Assert.Equal(ChestRuleEdit.MaximumPriority, ChestRuleEdit.From(wide).Priority);
        Assert.Equal(-ChestRuleEdit.MaximumPriority, ChestRuleEdit.From(new ChestRules(Taxonomy, ChestRuleState.Configured, priority: -5000)).Priority);
    }

    [Fact]
    public void Envelope_EditLeavingNothing_RemovesTheRule_ButHiddenPartsKeepIt()
    {
        var empty = new ChestRuleEdit(null, null, null, priority: 4);
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit("", "", Taxonomy, empty, out var none)); Assert.Equal("", none);
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit("", "", Taxonomy, Exact("Wood"), out var wood));
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit(wood!, wood!, Taxonomy, empty, out none)); Assert.Equal("", none);

        // D086 M2/D5: a rule with an alternative the panel doesn't show (a second filtered set) and overflow. Emptying the
        // shown part keeps both; the view names them; Clear removes them.
        var rules = new ChestRules(Taxonomy, ChestRuleState.Configured, new[] {
                new ClassificationFilterRule(Taxonomy, new[] { "food" }),
                new ClassificationFilterRule(Taxonomy, new[] { "materials" }, new[] { new System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.IEnumerable<string>>("usage", new[] { "fuel" }) }) },
            overflow: true);
        var stored = ContainerPolicyEnvelope.Serialize(7, rules);
        var view = ChestRuleEdit.From(ContainerPolicyEnvelope.Read(stored, Taxonomy).Rules!, withFilters: true);
        Assert.Equal(new[] { "food" }, view.Categories); Assert.Equal(2, view.HiddenParts);
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit(stored, stored, Taxonomy,
            new ChestRuleEdit(null, null, null, priority: view.Priority), out var emptied));
        var left = ContainerPolicyEnvelope.Read(emptied!, Taxonomy);
        Assert.True(left.HasRule); Assert.True(left.Rules!.Overflow); Assert.Equal(8UL, left.Revision);
        Assert.Equal("usage", Assert.Single(left.Rules.FullRules).Filters.Keys.Single());
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit(emptied!, emptied!, Taxonomy, null, out var cleared)); Assert.Equal("", cleared);
    }

    [Fact]
    public void Unruled_IsNoRuleOrALegacyClearedRule_NotAnUnreadableOne()
    {
        var evaluator = new ChestRuleEvaluator();
        Assert.True(evaluator.Unruled("", Taxonomy));
        // What clearing wrote before D086 M1.
        Assert.True(evaluator.Unruled(ContainerPolicyEnvelope.Serialize(2, new ChestRules(Taxonomy, ChestRuleState.Unconfigured, priority: 3)), Taxonomy));
        Assert.False(evaluator.Unruled(ContainerPolicyEnvelope.Serialize(2, new ChestRules(Taxonomy, ChestRuleState.Unconfigured, excludes: new[] { "Wood" })), Taxonomy));
        Assert.False(evaluator.Unruled(Payload(), Taxonomy));
        Assert.False(evaluator.Unruled("{\"future\":1}", Taxonomy));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("schema-string")]
    [InlineData("revision-number")]
    [InlineData("revision-leading-zero")]
    [InlineData("revision-overflow")]
    [InlineData("duplicate")]
    [InlineData("extra-field")]
    [InlineData("format")]
    [InlineData("malformed")]
    [InlineData("truncated")]
    [InlineData("array")]
    [InlineData("trailing-comma")]
    [InlineData("trailing-content")]
    [InlineData("leading-zero-number")]
    [InlineData("invalid-escape")]
    public void Envelope_UnknownMalformedOrAmbiguousFormat_IsPreservedAndCannotBeEdited(string kind)
    {
        var original = Payload();
        var payload = kind switch
        {
            "schema" => original.Replace("\"schemaVersion\":1", "\"schemaVersion\":2"),
            "schema-string" => original.Replace("\"schemaVersion\":1", "\"schemaVersion\":\"1\""),
            "revision-number" => original.Replace("\"revision\":\"1\"", "\"revision\":1"),
            "revision-leading-zero" => original.Replace("\"revision\":\"1\"", "\"revision\":\"01\""),
            "revision-overflow" => original.Replace("\"revision\":\"1\"", "\"revision\":\"18446744073709551616\""),
            "duplicate" => original.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"),
            "extra-field" => original.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"future\":true"),
            "format" => original.Replace("yavsos-chest-rules-xml-1", "yavsos-chest-rules-xml-2"),
            "malformed" => "not-json", "truncated" => original.Substring(0, original.Length - 1), "array" => "[]",
            "trailing-comma" => original.Substring(0, original.Length - 1) + ",}",
            "trailing-content" => original + "{}",
            "leading-zero-number" => original.Replace("\"schemaVersion\":1", "\"schemaVersion\":01"),
            "invalid-escape" => original.Replace("Wood", "W\\qood"),
            _ => throw new ArgumentException(nameof(kind))
        };
        Assert.NotEqual(original, payload);
        var read = ContainerPolicyEnvelope.Read(payload, Taxonomy); Assert.Equal(ContainerPolicyReadState.Unsupported, read.State);
        Assert.Equal(payload, read.OriginalPayload); Assert.Null(read.Rules); Assert.False(read.HasRule);
        Assert.Equal(TransferRefusal.UnsupportedCapability, ContainerPolicyEnvelope.TryEdit(payload, payload, Taxonomy, Exact("Wood"), out var update));
        Assert.Null(update);
        Assert.Equal(TransferRefusal.PolicyChanged, ContainerPolicyEnvelope.TryEdit(payload, "", Taxonomy, null, out update));
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit(payload, payload, Taxonomy, null, out update));
        Assert.False(ContainerPolicyEnvelope.Read(update!, Taxonomy).HasRule);
    }
    [Fact]
    public void Envelope_PersistedPayloadBound_IsUtf8BytesAndDoesNotTruncate()
    {
        var payload = Payload(); var boundary = payload + new string(' ', TransferLimits.MaximumPolicyBytes - Encoding.UTF8.GetByteCount(payload));
        Assert.Equal(ContainerPolicyReadState.Supported, ContainerPolicyEnvelope.Read(boundary, Taxonomy).State);
        var oversized = boundary + " "; var read = ContainerPolicyEnvelope.Read(oversized, Taxonomy);
        Assert.Equal(ContainerPolicyReadState.Unsupported, read.State); Assert.Equal(oversized, read.OriginalPayload);
        read = ContainerPolicyEnvelope.Read(payload + new string('\u00e9', TransferLimits.MaximumPolicyBytes / 2), Taxonomy);
        Assert.Equal(ContainerPolicyReadState.Unsupported, read.State);
        var many = new ChestRules(Taxonomy, ChestRuleState.Configured, includes: System.Linq.Enumerable.Range(0, 100).Select(i => new string('x', 900) + i));
        Assert.Throws<InvalidDataException>(() => ContainerPolicyEnvelope.Serialize(1, many));
    }
    [Fact]
    public void Envelope_AbsentAndUnconfigured_HaveNoRule_ConfiguredEmptyHasOne()
    {
        Assert.False(ContainerPolicyEnvelope.Read("", Taxonomy).HasRule);
        Assert.False(ContainerPolicyEnvelope.Read(ContainerPolicyEnvelope.Serialize(1, new ChestRules(Taxonomy, ChestRuleState.Unconfigured)), Taxonomy).HasRule);
        Assert.True(ContainerPolicyEnvelope.Read(ContainerPolicyEnvelope.Serialize(1, new ChestRules(Taxonomy, ChestRuleState.Configured)), Taxonomy).HasRule);
    }
    [Fact]
    public void Envelope_RevisionExhaustion_RefusesWithoutResettingOrOverwriting()
    {
        var payload = Payload(ulong.MaxValue);
        Assert.Equal(TransferRefusal.UnsupportedCapability, ContainerPolicyEnvelope.TryEdit(payload, payload, Taxonomy, Exact("Stone"), out var updated)); Assert.Null(updated);
    }
}
