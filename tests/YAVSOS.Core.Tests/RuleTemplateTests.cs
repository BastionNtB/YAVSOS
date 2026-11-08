using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Messages;
using YAVSOS.Core.Templates;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Core.Tests;

// X23 (D157): rule templates: the model, its codec and limits, missing parts, the server's admin and revision checks.
public sealed class RuleTemplateTests
{
    private static readonly ClassificationTaxonomy Full = DefaultClassificationTaxonomy.Create();
    private static readonly ClassificationTaxonomy Reduced = new ClassificationTaxonomy(Full.Categories.Values.Where(x => x.Id != "fish"));

    private static KeyValuePair<string, IEnumerable<string>> Filter(string dimension, params string[] values) => new(dimension, values);

    private static RuleTemplate T(string id, string name, ulong revision = 1, string[]? categories = null, string[]? always = null, string[]? never = null,
        int priority = 0, string by = "", params KeyValuePair<string, IEnumerable<string>>[] filters) =>
        new RuleTemplate(id, name, revision, by, categories ?? new[] { "food" }, always, never, filters, priority);

    private static RuleEditState Loaded(string payload, ClassificationTaxonomy taxonomy)
    {
        var state = new RuleEditState();
        var read = ContainerPolicyEnvelope.Read(payload, taxonomy);
        if (read.HasRule) state.Load(read.Rules!);
        return state;
    }

    private static string Rule(ChestRuleEdit edit)
    {
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit("", "", Full, edit, out var payload));
        return payload!;
    }

    private static int counter;
    private static int Next(int n) => counter++ % n;

    // ---- What a template holds.
    [Fact]
    public void Of_TakesWhatCopyTakes_CategoriesByIdAlwaysNeverFiltersAndPriority()
    {
        var payload = Rule(new ChestRuleEdit(new[] { "weapons", "fish" }, new[] { "Wood" }, new[] { "Club" }, priority: 3,
            filters: new[] { Filter("rarity", "magic") }));
        var state = Loaded(payload, Reduced); // "fish" is unknown here (D050) and still goes into the template.
        var template = RuleTemplate.Of(state, "abc", "Weapons")!;
        Assert.Equal(new[] { "fish", "weapons" }, template.Categories);
        Assert.Equal(new[] { "Wood" }, template.Includes); Assert.Equal(new[] { "Club" }, template.Excludes);
        Assert.Equal(new[] { "magic" }, template.Filters["rarity"]); Assert.Equal(3, template.Priority);
    }

    [Fact]
    public void Of_ChestWithoutARule_IsNull()
    {
        Assert.Null(RuleTemplate.Of(new RuleEditState(), "abc", "Empty"));
    }

    [Fact]
    public void Apply_SavesLikePaste_UnknownCategoriesStayStoredUnused()
    {
        var template = T("abc", "Fish and food", categories: new[] { "fish", "food" }, always: new[] { "Stone" }, priority: -2);
        var state = Loaded(Rule(new ChestRuleEdit(new[] { "weapons" }, null, new[] { "Club" })), Reduced);
        state.Paste(template.ToCopy(), Reduced);
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit("", "", Reduced, state.Edit(), out var updated));
        var rules = ContainerPolicyEnvelope.Read(updated!, Full).Rules!;
        Assert.Equal(new[] { "fish", "food" }, rules.FullRules.SelectMany(x => x.Categories).OrderBy(x => x).ToArray());
        Assert.Empty(rules.Excludes); // The chest's own Never went: the template replaces the rule, as Paste does.
        Assert.Equal(new[] { "Stone" }, rules.Includes); Assert.Equal(-2, rules.Priority);
        var reduced = ContainerPolicyEnvelope.Read(updated!, Reduced).Rules!;
        Assert.Equal(new[] { "fish" }, reduced.UnknownCategories);
    }

    [Fact]
    public void Template_RefusesBadIdsNamesAndCategories()
    {
        Assert.Throws<ArgumentException>(() => T("ABC", "x"));
        Assert.Throws<ArgumentException>(() => T("abc", "  "));
        Assert.Throws<ArgumentException>(() => T("abc", new string('x', 65)));
        Assert.Throws<ArgumentException>(() => T("abc", "x", categories: new[] { "Not An Id" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => T("abc", "x", revision: 0));
    }

    [Fact]
    public void Names_AreTrimmed_UniqueIgnoringCase_AndCopiesGetANumber()
    {
        Assert.Equal("Metals", TemplateNames.Clean("  Metals "));
        Assert.Null(TemplateNames.Clean("a\u0007b"));
        var taken = new HashSet<string>(new[] { "Metals", "Metals 2" }, TemplateNames.Comparer);
        Assert.Equal("metals 3", TemplateNames.Free("metals", taken.Contains)); // The name as given, with the first free number.
        var longName = new string('x', 64);
        var free = TemplateNames.Free(longName, n => n == longName);
        Assert.Equal(64, free.Length); Assert.EndsWith(" 2", free);
    }

    // ---- The codec.
    [Fact]
    public void Codec_RoundTripsCanonically()
    {
        var list = new TemplateList(TemplateListKind.Server, 7, new[]
        {
            T("aaa", "Smelter row", 3, new[] { "metals" }, new[] { "Coal" }, new[] { "Stone" }, 2, "Bastion", Filter("usage", "crafting", "fuel")),
            T("bbb", "Farm <crops> & \"seeds\"", categories: new[] { "farming" })
        });
        var text = RuleTemplateCodec.Encode(list);
        var read = RuleTemplateCodec.Decode(text, TemplateListKind.Server);
        Assert.Equal(TemplateReadState.Valid, read.State); Assert.True(read.Writable);
        Assert.Equal(7ul, read.List!.Revision);
        Assert.Equal(new[] { "aaa", "bbb" }, read.List.Templates.Select(x => x.Id));
        var first = read.List.Templates[0];
        Assert.Equal("Bastion", first.By); Assert.Equal(3ul, first.Revision); Assert.Equal(2, first.Priority);
        Assert.Equal(new[] { "crafting", "fuel" }, first.Filters["usage"]);
        Assert.Equal("Farm <crops> & \"seeds\"", read.List.Templates[1].Name);
        Assert.Equal(text, RuleTemplateCodec.Encode(read.List)); // The same list gives the same text.
    }

    [Fact]
    public void Codec_BlankIsAnEmptyWritableList()
    {
        var read = RuleTemplateCodec.Decode("", TemplateListKind.Personal);
        Assert.Equal(TemplateReadState.Empty, read.State); Assert.True(read.Writable); Assert.Equal(0, read.List!.Count);
    }

    [Theory]
    [InlineData("<yavsos-templates schema=\"2\" kind=\"personal\" revision=\"1\"><template id=\"a\" whatever=\"x\"/></yavsos-templates>", TemplateReadState.Newer)]
    [InlineData("<yavsos-templates schema=\"1\" kind=\"personal\" revision=\"1\"><template id=\"a\" revision=\"1\" name=\"n\" color=\"red\"><category>food</category></template></yavsos-templates>", TemplateReadState.Malformed)]
    [InlineData("<yavsos-templates schema=\"1\" kind=\"personal\" revision=\"1\"><template id=\"a\" revision=\"1\" name=\"n\"><category>food</category><icon>x</icon></template></yavsos-templates>", TemplateReadState.Malformed)]
    [InlineData("<yavsos-templates schema=\"1\" kind=\"server\" revision=\"1\"/>", TemplateReadState.Malformed)]
    [InlineData("<yavsos-templates schema=\"1\" kind=\"personal\" revision=\"1\"><template id=\"a\" revision=\"1\" name=\"n\"/></yavsos-templates>", TemplateReadState.Malformed)]
    [InlineData("<yavsos-templates schema=\"1\" kind=\"personal\" revision=\"1\"><template id=\"a\" revision=\"1\" name=\"n\"><category>food</category></template><template id=\"b\" revision=\"1\" name=\"N\"><category>food</category></template></yavsos-templates>", TemplateReadState.Malformed)]
    [InlineData("<!DOCTYPE x [<!ENTITY e \"y\">]><yavsos-templates schema=\"1\" kind=\"personal\" revision=\"1\"/>", TemplateReadState.Malformed)]
    [InlineData("not xml", TemplateReadState.Malformed)]
    public void Codec_NewerOrUnknownData_IsNeverWritable(string text, TemplateReadState state)
    {
        // D153: what this version can't read stays as stored; nothing may write over it.
        var read = RuleTemplateCodec.Decode(text, TemplateListKind.Personal);
        Assert.Equal(state, read.State); Assert.False(read.Writable); Assert.Null(read.List);
        Assert.NotEmpty(read.Diagnostic);
    }

    [Fact]
    public void Codec_ListOverItsLimit_IsMalformedNotCut()
    {
        var read = RuleTemplateCodec.Decode(new string(' ', 10) + "<a>" + new string('x', TemplateLimits.ServerListBytes) + "</a>", TemplateListKind.Server);
        Assert.Equal(TemplateReadState.Malformed, read.State); Assert.False(read.Writable);
    }

    [Fact]
    public void Kept_ShowsTheLastListButRefusesWrites()
    {
        var last = new TemplateList(TemplateListKind.Server, 2, new[] { T("aaa", "A") });
        var kept = TemplateListRead.Kept(RuleTemplateCodec.Decode("<yavsos-templates schema=\"9\"/>", TemplateListKind.Server), last);
        Assert.Same(last, kept.List); Assert.False(kept.Writable); Assert.Equal(TemplateReadState.Newer, kept.State);
        Assert.Equal(TemplateOutcome.Unreadable, TemplateServerRules.Decide(kept.Writable ? kept.List : null, TemplateChangeRequest.Delete(1, "aaa", 1), true, "", Next).Outcome);
    }

    // ---- Limits (D157): 64 per list, 32 KiB each, 256 KiB for the server's list, 512 KiB for personal templates.
    [Fact]
    public void List_Of64_RefusesAnotherAndSaysFull()
    {
        var list = TemplateList.Empty(TemplateListKind.Personal);
        for (var i = 0; i < TemplateLimits.PerList; i++) list = list.Add(T("t" + i, "T" + i)).List;
        var refused = list.Add(T("extra", "Extra"));
        Assert.Equal(TemplateOutcome.Full, refused.Outcome); Assert.Same(list, refused.List);
        Assert.Equal(64ul, list.Revision);
    }

    [Fact]
    public void Template_Over32KiB_IsRefused()
    {
        var items = Enumerable.Range(0, 2000).Select(i => "SomeModdedItemPrefabName_" + i).ToArray();
        var big = T("big", "Big", always: items);
        Assert.True(RuleTemplateCodec.TemplateBytes(big) > TemplateLimits.TemplateBytes);
        Assert.Equal(TemplateOutcome.TooLarge, TemplateList.Empty(TemplateListKind.Personal).Add(big).Outcome);
    }

    [Theory]
    [InlineData(TemplateListKind.Server, 5000)]
    [InlineData(TemplateListKind.Personal, 9000)]
    public void WholeList_OverItsLimit_IsRefused(TemplateListKind kind, int bytesEach)
    {
        var items = Enumerable.Range(0, bytesEach / 30).Select(i => "ItemPrefab_" + i.ToString("D5")).ToArray();
        var list = TemplateList.Empty(kind);
        TemplateEdit last = null!;
        for (var i = 0; i < TemplateLimits.PerList; i++)
        {
            last = list.Add(T("t" + i, "T" + i, always: items));
            if (!last.Done) break;
            list = last.List;
        }
        Assert.Equal(TemplateOutcome.ListTooLarge, last.Outcome);
        Assert.True(RuleTemplateCodec.Bytes(list) <= TemplateLimits.ListBytes(kind));
        Assert.True(list.Count < TemplateLimits.PerList);
    }

    [Fact]
    public void Personal_NewFirst_Server_NewLast()
    {
        var personal = TemplateList.Empty(TemplateListKind.Personal).Add(T("a", "A")).List.Add(T("b", "B")).List;
        Assert.Equal(new[] { "b", "a" }, personal.Templates.Select(x => x.Id));
        var server = TemplateList.Empty(TemplateListKind.Server).Add(T("a", "A")).List.Add(T("b", "B")).List;
        Assert.Equal(new[] { "a", "b" }, server.Templates.Select(x => x.Id));
    }

    [Fact]
    public void Rename_Update_Delete_ChangeOneTemplateAndBumpItsRevision()
    {
        var list = TemplateList.Empty(TemplateListKind.Personal).Add(T("a", "A")).List.Add(T("b", "B", by: "")).List;
        Assert.Equal(TemplateOutcome.NameTaken, list.Rename("a", "b").Outcome);
        Assert.Equal(TemplateOutcome.NameInvalid, list.Rename("a", " ").Outcome);
        var renamed = list.Rename("a", "a").List; // Its own name in another case is free.
        Assert.Equal("a", renamed.Find("a")!.Name); Assert.Equal(2ul, renamed.Find("a")!.Revision);
        var updated = renamed.Replace("b", T("zzz", "Ignored", categories: new[] { "metals" }, priority: 4)).List.Find("b")!;
        Assert.Equal("B", updated.Name); Assert.Equal(new[] { "metals" }, updated.Categories); Assert.Equal(4, updated.Priority); Assert.Equal(2ul, updated.Revision);
        Assert.Equal(new[] { "a" }, renamed.Delete("b").List.Templates.Select(x => x.Id));
        Assert.Equal(TemplateOutcome.Changed, renamed.Delete("nope").Outcome);
    }

    // ---- The server's checks.
    private static TemplateList Server(params RuleTemplate[] templates) => new TemplateList(TemplateListKind.Server, 1, templates);

    [Fact]
    public void Server_RefusesNonAdmins_AndChangesNothing()
    {
        var list = Server(T("aaa", "A"));
        foreach (var request in new[] { TemplateChangeRequest.Share(1, T("x", "New")), TemplateChangeRequest.Update(2, "aaa", 1, T("x", "X")),
                     TemplateChangeRequest.Rename(3, "aaa", 1, "B"), TemplateChangeRequest.Delete(4, "aaa", 1) })
        {
            var edit = TemplateServerRules.Decide(list, request, admin: false, "Mallory", Next);
            Assert.Equal(TemplateOutcome.NotAdmin, edit.Outcome); Assert.Same(list, edit.List);
        }
    }

    [Fact]
    public void Server_Share_GetsANewIdRevision1AndTheSendersName()
    {
        var list = Server(T("aaa", "A"));
        var edit = TemplateServerRules.Decide(list, TemplateChangeRequest.Share(9, T("mine", "Metals", revision: 5, by: "spoofed")), true, "Bastion", Next);
        Assert.True(edit.Done);
        var shared = edit.Template!;
        Assert.NotEqual("mine", shared.Id); Assert.True(TemplateIds.Valid(shared.Id));
        Assert.Equal(1ul, shared.Revision); Assert.Equal("Bastion", shared.By);
        Assert.Equal(2, edit.List.Count); Assert.Equal(2ul, edit.List.Revision);
        var reply = TemplateServerRules.Reply(TemplateChangeRequest.Share(9, shared), edit);
        Assert.Equal(TemplateOutcome.Done, reply.Outcome); Assert.Equal(shared.Id, reply.Id); Assert.Equal(1ul, reply.Revision);
        Assert.Equal(TemplateOutcome.NameTaken, TemplateServerRules.Decide(edit.List, TemplateChangeRequest.Share(10, T("x", "metals")), true, "B", Next).Outcome);
    }

    [Fact]
    public void Server_TwoAdmins_TheOlderRevisionIsRefused_ThenTheRetryWorks()
    {
        var list = Server(T("aaa", "Smelter row", categories: new[] { "metals" }));
        // Both admins selected revision 1. The first saves; the second's change names revision 1 and is refused.
        var first = TemplateServerRules.Decide(list, TemplateChangeRequest.Update(1, "aaa", 1, T("x", "X", always: new[] { "Coal" })), true, "A", Next);
        Assert.True(first.Done); Assert.Equal(2ul, first.Template!.Revision);
        foreach (var stale in new[] { TemplateChangeRequest.Update(2, "aaa", 1, T("x", "X")), TemplateChangeRequest.Rename(3, "aaa", 1, "Mine"),
                     TemplateChangeRequest.Delete(4, "aaa", 1) })
        {
            var refused = TemplateServerRules.Decide(first.List, stale, true, "B", Next);
            Assert.Equal(TemplateOutcome.Changed, refused.Outcome); Assert.Same(first.List, refused.List);
            Assert.Equal(0ul, TemplateServerRules.Reply(stale, refused).Revision);
        }
        // Looking again shows revision 2; the retry names it.
        var retry = TemplateServerRules.Decide(first.List, TemplateChangeRequest.Rename(5, "aaa", 2, "Smelters"), true, "B", Next);
        Assert.True(retry.Done); Assert.Equal("Smelters", retry.Template!.Name); Assert.Equal(new[] { "Coal" }, retry.Template.Includes);
        Assert.Equal(3ul, retry.Template.Revision);
        Assert.Equal(TemplateOutcome.Changed, TemplateServerRules.Decide(retry.List, TemplateChangeRequest.Delete(6, "gone", 1), true, "B", Next).Outcome);
    }

    [Fact]
    public void Server_UnreadableFile_RefusesEveryChange()
    {
        var edit = TemplateServerRules.Decide(null, TemplateChangeRequest.Share(1, T("x", "X")), true, "A", Next);
        Assert.Equal(TemplateOutcome.Unreadable, edit.Outcome); Assert.False(edit.Done);
    }

    [Fact]
    public void Server_FullList_IsRefused()
    {
        var list = Server(Enumerable.Range(0, 64).Select(i => T("t" + i, "T" + i)).ToArray());
        Assert.Equal(TemplateOutcome.Full, TemplateServerRules.Decide(list, TemplateChangeRequest.Share(1, T("x", "X")), true, "A", Next).Outcome);
    }

    // ---- The wire.
    [Fact]
    public void Requests_RoundTrip()
    {
        var template = T("abc", "Metals", 3, new[] { "metals" }, new[] { "Coal" }, null, 1, "", Filter("usage", "crafting"));
        foreach (var request in new[] { TemplateChangeRequest.Share(7, template), TemplateChangeRequest.Update(8, "aaa", 4, template),
                     TemplateChangeRequest.Rename(9, "aaa", 2, "New name"), TemplateChangeRequest.Delete(10, "aaa", 6) })
        {
            Assert.True(TemplateChangeCodec.TryDecode(TemplateChangeCodec.Encode(request), out var read));
            Assert.Equal(request.RequestId, read!.RequestId); Assert.Equal(request.Operation, read.Operation);
            Assert.Equal(request.Id, read.Id); Assert.Equal(request.Seen, read.Seen); Assert.Equal(request.Name, read.Name);
            Assert.Equal(request.Template != null, read.Template != null);
            if (read.Template != null) Assert.True(read.Template.SameRule(template));
        }
    }

    [Theory]
    [InlineData("<yavsos-template-change schema=\"1\" request=\"1\" operation=\"share\"/>")]
    [InlineData("<yavsos-template-change schema=\"1\" request=\"1\" operation=\"delete\" id=\"aaa\"/>")]
    [InlineData("<yavsos-template-change schema=\"1\" request=\"1\" operation=\"burn\" id=\"aaa\" seen=\"1\"/>")]
    [InlineData("<yavsos-template-change schema=\"2\" request=\"1\" operation=\"delete\" id=\"aaa\" seen=\"1\"/>")]
    [InlineData("<yavsos-template-change schema=\"1\" request=\"1\" operation=\"delete\" id=\"aaa\" seen=\"1\" admin=\"yes\"/>")]
    [InlineData("<yavsos-template-change schema=\"1\" request=\"1\" operation=\"delete\" id=\"AAA\" seen=\"1\"/>")]
    [InlineData("<yavsos-template-change schema=\"1\" request=\"1\" operation=\"rename\" id=\"aaa\" seen=\"1\" name=\"  \"/>")]
    [InlineData("garbage")]
    public void Requests_Malformed_AreRefused(string text)
    {
        Assert.False(TemplateChangeCodec.TryDecode(System.Text.Encoding.UTF8.GetBytes(text), out var request));
        Assert.Null(request);
    }

    [Fact]
    public void Requests_OverTheirBound_AreRefusedUnread()
    {
        Assert.False(TemplateChangeCodec.TryDecode(new byte[TemplateChangeCodec.MaximumRequestBytes + 1], out _));
        Assert.False(TemplateChangeCodec.TryDecode(null, out _));
    }

    [Fact]
    public void Replies_RoundTrip_AndGarbageIsRefused()
    {
        var reply = new TemplateChangeReply(123, TemplateOutcome.Changed, "abc", 0);
        Assert.True(TemplateChangeCodec.TryDecodeReply(TemplateChangeCodec.EncodeReply(reply), out var read));
        Assert.Equal(123ul, read.RequestId); Assert.Equal(TemplateOutcome.Changed, read.Outcome); Assert.Equal("abc", read.Id);
        Assert.True(TemplateChangeCodec.TryDecodeReply(TemplateChangeCodec.EncodeReply(new TemplateChangeReply(5, TemplateOutcome.Done, "", 0)), out var empty));
        Assert.Equal("", empty.Id);
        foreach (var bad in new[] { "ytc1:1:99:abc:0", "ytc2:1:0:abc:0", "ytc1:1:0:ABC:0", "ytc1:x:0:abc:0", "ytc1:1:0:abc", "" })
            Assert.False(TemplateChangeCodec.TryDecodeReply(System.Text.Encoding.UTF8.GetBytes(bad), out _));
    }

    [Fact]
    public void Tracker_OneAtATime_LateRepliesDropped_TimesOut()
    {
        var tracker = new TemplateRequestTracker(100);
        var first = TemplateChangeRequest.Delete(tracker.NextId(), "aaa", 1);
        Assert.True(tracker.Start(first, 0));
        Assert.False(tracker.Start(TemplateChangeRequest.Delete(tracker.NextId(), "bbb", 1), 1)); // One change waits at a time.
        Assert.Null(tracker.Reply(new TemplateChangeReply(first.RequestId + 50, TemplateOutcome.Done, "aaa", 2)));
        Assert.Null(tracker.Expired(TemplateRequestTracker.AnswerSeconds - 0.1));
        Assert.Same(first, tracker.Expired(TemplateRequestTracker.AnswerSeconds));
        Assert.False(tracker.Busy);
        Assert.Null(tracker.Reply(new TemplateChangeReply(first.RequestId, TemplateOutcome.Done, "aaa", 2))); // Late: dropped.
        var second = TemplateChangeRequest.Delete(tracker.NextId(), "aaa", 1);
        Assert.True(tracker.Start(second, 20));
        Assert.Same(second, tracker.Reply(new TemplateChangeReply(second.RequestId, TemplateOutcome.Done, "aaa", 0)));
        Assert.True(tracker.NextId() >= 100);
    }

    [Fact]
    public void Rate_AllowsABurst_ThenOneASecond_PerSender()
    {
        var rate = new TemplateChangeRate();
        for (var i = 0; i < TemplateChangeRate.Burst; i++) Assert.True(rate.Allow("Steam_1", 0));
        Assert.False(rate.Allow("Steam_1", 0));
        Assert.True(rate.Allow("Steam_2", 0));
        Assert.True(rate.Allow("Steam_1", 1.01));
        Assert.False(rate.Allow("Steam_1", 1.02));
    }

    // ---- Missing parts.
    [Fact]
    public void Missing_ByIdOnly_CategoriesItemsAndFilters()
    {
        var template = T("abc", "Mixed", categories: new[] { "fish", "food" }, always: new[] { "Coal", "JC_Gem_Red" }, never: new[] { "Stone" },
            filters: new[] { Filter("rarity", "magic"), Filter("future-dimension", "x"), Filter("usage", "crafting", "laser") });
        var loaded = new HashSet<string> { "Coal", "Stone" };
        var missing = TemplateMissing.Find(template, Reduced.Categories.ContainsKey, loaded.Contains, (dimension, _) => dimension != "rarity");
        Assert.Equal(new[] { "fish" }, missing.Categories);
        Assert.Equal(new[] { "JC_Gem_Red" }, missing.Items);
        Assert.Contains(("rarity", (string?)"magic"), missing.Filters);
        Assert.Contains(("future-dimension", (string?)null), missing.Filters);
        Assert.Contains(("usage", (string?)"laser"), missing.Filters);
        Assert.DoesNotContain(("usage", (string?)"crafting"), missing.Filters);
        Assert.Equal(5, missing.Count);
    }

    [Fact]
    public void Missing_NothingWhenAllExist_AndItemsWaitForTheItemList()
    {
        var template = T("abc", "Food", categories: new[] { "food" }, always: new[] { "Anything" });
        Assert.Same(TemplateMissing.None, TemplateMissing.Find(template, Full.Categories.ContainsKey, hasItem: null));
        // A renamed category (an admin's display name) is the same ID: nothing is missing.
        Assert.Equal(0, TemplateMissing.Find(template, id => id == "food", _ => true).Count);
    }

    // ---- What the tab says.
    [Fact]
    public void Text_SummaryDetailsAndMissing_InEnglish()
    {
        var template = T("abc", "Smelter row", categories: new[] { "materials.metal", "fish" }, always: new[] { "Coal" }, never: new[] { "Stone", "Wood" },
            priority: 3, by: "Bastion");
        string Name(string id) => id == "materials.metal" ? "Metals" : id;
        Assert.Equal("fish, Metals · 1 Always · 2 Never · priority 2 (Urgent) · by Bastion",
            EnglishText.Of(TemplateText.Summary(template, TemplateListKind.Server, Name)));
        Assert.DoesNotContain("by Bastion", EnglishText.Of(TemplateText.Summary(template, TemplateListKind.Personal, Name)));
        var details = EnglishText.Of(TemplateText.Details(template, TemplateListKind.Server, Name, x => x, null));
        Assert.Equal("Categories: fish, Metals\nAlways: Coal\nNever: Stone, Wood\nPriority: 2 (Urgent)\nShared by Bastion.", details);
        var missing = TemplateMissing.Find(template, Reduced.Categories.ContainsKey, _ => true);
        Assert.Equal("1 part missing here", EnglishText.Of(TemplateText.Missing(missing.Count)));
        Assert.Equal("Missing here: fish. Apply uses the rest; this part stays stored, unused.", EnglishText.Of(TemplateText.MissingLine(missing)!));
        Assert.Null(TemplateText.MissingLine(TemplateMissing.None));
        var tip = EnglishText.Of(TemplateText.Tip("Smelter row", TemplateText.Details(template, TemplateListKind.Personal, Name, x => x, null), TemplateText.MissingLine(missing)));
        Assert.Equal("Smelter row", tip.Topic); Assert.EndsWith("this part stays stored, unused.", tip.Text);
    }

    [Fact]
    public void Text_EveryOutcomeHasWords()
    {
        foreach (TemplateOutcome outcome in Enum.GetValues(typeof(TemplateOutcome)))
        {
            foreach (TemplateOperation operation in Enum.GetValues(typeof(TemplateOperation)))
                Assert.DoesNotContain("[", EnglishText.Of(TemplateText.ServerAnswer(operation, outcome, "X", false).Text));
            if (TemplateText.PersonalRefusal(outcome, "X") is { } refusal) Assert.DoesNotContain("[", EnglishText.Of(refusal));
            else Assert.Equal(TemplateOutcome.Done, outcome);
        }
        foreach (TemplateAsk ask in Enum.GetValues(typeof(TemplateAsk)))
            Assert.DoesNotContain("{{", EnglishText.Of(TemplateText.Confirm(ask, "A", "B", 2)));
        Assert.Equal("2 parts aren't on this server. Share anyway?", EnglishText.Of(TemplateText.Confirm(TemplateAsk.ShareMissing, "A", null, 2)));
        Assert.Equal("Shared \"Metals\" with everyone on this server.", EnglishText.Of(TemplateText.ServerAnswer(TemplateOperation.Share, TemplateOutcome.Done, "Metals", false).Text));
        Assert.Equal(MessageKind.Summary, TemplateText.ServerAnswer(TemplateOperation.Delete, TemplateOutcome.Done, "X", false).Kind);
        Assert.Equal("That template isn't there anymore.", EnglishText.Of(TemplateText.ServerAnswer(TemplateOperation.Update, TemplateOutcome.Changed, "X", gone: true).Text));
        Assert.Equal("3 of 64", EnglishText.Of(TemplateText.Count(3)));
    }
}
