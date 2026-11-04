using System;
using System.Linq;
using Xunit;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Messages;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Core.Tests;

/// <summary>
/// P6-24 (D071) status marks, as messages (P10-04): which mark shows, its state, and which message each line carries.
/// Tests about the words a player reads format them from the real English file.
/// </summary>
public sealed class PanelMarksTests
{
    private static string Heading(IntegrationMark mark) => EnglishText.Of(mark.Tooltip).Topic;
    private static string Text(IntegrationMark mark) => EnglishText.Of(mark.Tooltip).Text;
    private static string[] Keys(IntegrationMark mark) => mark.Lines.Select(x => x.Text.Key).ToArray();

    [Fact]
    public void Marks_OnlyForInstalledMods_AndBrokenWinsOverWorking()
    {
        Assert.Null(PanelMarks.Azu(null, true, true, true, true, true));
        Assert.Null(PanelMarks.EpicLoot(false, false));
        Assert.Null(PanelMarks.Drawers(false, false, false));
        Assert.Null(PanelMarks.Cleanup(true, true)); // YAVSOS's own and working: no mark.
        Assert.Equal(MarkState.Limited, PanelMarks.Categories(false, 0, true).State); // Loading: amber (D101), no banner.

        var azu = PanelMarks.Azu("3.1.7", true, true, true, true, true)!;
        Assert.Equal((MarkKind.AzuAutoStore, "AzuAutoStore 3.1.7", MarkState.Working), (azu.Kind, Heading(azu), azu.State));
        var partial = PanelMarks.Azu("3.1.7", true, false, true, true, true)!;
        Assert.Equal(MarkState.Broken, partial.State);
        Assert.Contains("On: Chest rules apply to AzuAutoStore's stash key and ground pickup.", Text(partial));
        Assert.Contains("Not working: AzuAutoStore changed, so YAVSOS can't choose which chests AzuAutoStore's stash key fills first.", Text(partial));
        var changed = PanelMarks.Azu("", false, false, false, false, false)!;
        Assert.Equal(("AzuAutoStore", MarkState.Broken), (Heading(changed), changed.State));
        Assert.Equal(new[] { "yavsos_mark_azu_rules_broken" }, Keys(changed));

        var epic = PanelMarks.EpicLoot(true, false)!;
        Assert.Equal(MarkState.Broken, epic.State);
        Assert.Equal("Not working: EpicLoot changed, so YAVSOS can't read item rarity for the rarity filter. Everything else works.", Text(epic));
        Assert.Equal(MarkState.Working, PanelMarks.Drawers(true, true, true)!.State);
        Assert.Equal(new[] { "yavsos_mark_drawers_no_azu" }, Keys(PanelMarks.Drawers(true, false, false)!));
        Assert.Equal(new[] { "yavsos_mark_drawers_broken" }, Keys(PanelMarks.Drawers(true, false, true)!));
        Assert.Equal("Item drawers", Heading(PanelMarks.Drawers(true, true, true)!));
    }

    // Inventory case 4: the state word and the line are separate messages joined by one pattern a translator can reorder,
    // and the state word carries the state's color as a tone, not markup.
    [Fact]
    public void MarkLine_IsTheStateWordTinted_ThenAFullSentence()
    {
        var line = new MarkLine(Message.Of("yavsos_mark_server_without_yavsos"), MarkState.Working).Line;
        Assert.Equal("yavsos_mark_line", line.Key);
        Assert.Equal("[Working:On]: This server has no YAVSOS, so your own categories and settings apply.", EnglishText.Marked(line));
        foreach (var (state, word) in new[] { (MarkState.Broken, "[Broken:Not working]: "), (MarkState.Caution, "[Caution:This chest]: "),
                     (MarkState.Limited, "[Caution:Partly working]: "), (MarkState.Off, "[Faint:Off]: ") })
            Assert.StartsWith(word, EnglishText.Marked(new MarkLine(Message.Of("yavsos_mark_server_without_yavsos"), state).Line));
        Assert.All(Enum.GetValues(typeof(MarkState)).Cast<MarkState>(), state => Assert.DoesNotContain("<", EnglishText.Of(PanelMarks.Word(state))));
    }

    // P4-00f (D056): AzuAutoStore's settings keeping some of the rule's items out of this chest type are a caution on its
    // mark (maintainer, 2026-10-24), outranking a setting but not a broken part.
    [Fact]
    public void Marks_KeptOutItems_AreACautionOnTheAzuMark()
    {
        Assert.Equal(MarkState.Working, PanelMarks.Azu("3.1.7", true, true, true, true, true, null).State);
        var item = Message.Of("yavsos_mark_azu_kept_out_item").With("item", "Bronze").With("tab", Message.Of("yavsos_tab_items"));
        var one = PanelMarks.Azu("3.1.7", true, true, true, true, true, item)!;
        Assert.Equal(MarkState.Caution, one.State);
        Assert.EndsWith("This chest: AzuAutoStore keeps Bronze out of this chest type, so it won't stash it here. Its icon on the Items tab is amber. " +
            "Use another chest type or change AzuAutoStore's settings.", Text(one));
        var many = Message.Of("yavsos_mark_azu_kept_out").Count(7).With("items", MessageList.Comma("Bronze", "Tin").WithMore(5))
            .With("tab", Message.Of("yavsos_tab_items"));
        Assert.Equal("AzuAutoStore keeps 7 of this rule's items out of this chest type (Bronze, Tin +5 more), so it won't stash them here. " +
            "Their icons on the Items tab are amber. Use another chest type or change AzuAutoStore's settings.", EnglishText.Of(many));
        Assert.Equal(MarkState.Broken, PanelMarks.Azu("3.1.7", true, false, true, true, true, item)!.State);
        Assert.Equal(MarkState.Caution, new IntegrationMark(MarkKind.AzuAutoStore, Message.Of("yavsos_mark_azu_name"),
            new[] { new MarkLine(Message.Of("yavsos_mark_azu_rules"), MarkState.Off), new MarkLine(item, MarkState.Caution) }).State);
    }

    // D086 D2, M7, D088: exclusions or the pause flag read only in part are amber, not broken.
    [Fact]
    public void Marks_AzuExclusionsReadInPart_AreLimited()
    {
        var errors = Message.Of("yavsos_mark_azu_exclusions_errors").Count(1);
        var limited = PanelMarks.Azu("3.1.7", true, true, true, true, true, null, errors)!;
        Assert.Equal(MarkState.Limited, limited.State);
        Assert.Contains("Partly working: YAVSOS couldn't read AzuAutoStore's exclusions in the last few minutes (see the log). Reading them has failed once this session.", Text(limited));
        Assert.Contains("failed 3 times this session", EnglishText.Of(Message.Of("yavsos_mark_azu_exclusions_errors").Count(3)));
        var off = PanelMarks.Azu("3.1.7", true, true, true, false, true)!;
        Assert.Equal(MarkState.Limited, off.State); Assert.Contains("yavsos_mark_azu_exclusions_unreadable", Keys(off));
        Assert.Equal(MarkState.Limited, PanelMarks.Azu("3.1.7", true, true, true, true, false)!.State);
        Assert.Equal(MarkState.Caution, PanelMarks.Azu("3.1.7", true, true, true, true, false, Message.Of("yavsos_mark_azu_kept_out_item"))!.State);
    }

    // D086 B5.36, D087: categories that gave up say so. D101: loading is an amber mark in place of the panel's banner.
    [Fact]
    public void Marks_CategoriesThatGaveUp_AreBroken_LoadingIsAmber()
    {
        var loading = PanelMarks.Categories(false, 0, false);
        Assert.Equal(MarkState.Limited, loading.State); Assert.Equal(new[] { "yavsos_mark_categories_loading" }, Keys(loading));
        var gave = PanelMarks.Categories(false, 0, false, gaveUp: true)!;
        Assert.Equal(MarkState.Broken, gave.State); Assert.Contains("Always and Never items still work.", Text(gave));
        // B4.26: with the previous answers kept (D086 D7) a stage that gave up is amber, not hidden.
        var kept = PanelMarks.Categories(true, 5, false, gaveUp: true)!;
        Assert.Equal(MarkState.Limited, kept.State); Assert.Contains("categories from before the latest mod or settings change", Text(kept));
        Assert.Equal(MarkState.Working, PanelMarks.Categories(true, 5, false)!.State);
        // B4.9: a rejected server policy says so instead of "still loading".
        var rejected = PanelMarks.Categories(false, 0, false, serverRejected: true);
        Assert.Equal(MarkState.Broken, rejected.State); Assert.Equal(new[] { "yavsos_mark_categories_server_rejected" }, Keys(rejected));
    }

    // D086 D1: a failed optional part turns only itself off, shown amber ("partly working"), below a broken part.
    [Fact]
    public void Marks_AFailedOptionalPart_IsLimitedNotBroken()
    {
        static IntegrationMark Of(MarkState a, MarkState b) => new(MarkKind.Display, Message.Of("yavsos_mark_display_name"),
            new[] { new MarkLine(Message.Of("yavsos_mark_server_without_yavsos"), a), new MarkLine(Message.Of("yavsos_mark_server_without_yavsos"), b) });
        var limited = Of(MarkState.Working, MarkState.Limited);
        Assert.Equal(MarkState.Limited, limited.State);
        Assert.Contains("\nPartly working: ", Text(limited));
        Assert.Equal(MarkState.Working, PanelMarks.EpicLoot(true, true)!.State);
        Assert.Equal(MarkState.Broken, Of(MarkState.Broken, MarkState.Limited).State);
        Assert.Equal(MarkState.Caution, Of(MarkState.Limited, MarkState.Caution).State);
        Assert.Equal(MarkState.Limited, Of(MarkState.Off, MarkState.Limited).State);
    }

    // D071: a setting that turns something off gets the grey dash and names the setting (inventory case 11: an identifier
    // in a placeholder, never translated).
    [Fact]
    public void Marks_TurnedOffBySetting_NameTheSetting()
    {
        var stash = PanelMarks.Stashing(false, true, ".", "Middle mouse");
        Assert.Equal(MarkState.Off, stash.State);
        Assert.Equal("Off: This server has turned off stashing (the Server.AllowStashing setting).", Text(stash));
        Assert.Equal("Server.AllowStashing", stash.Lines[0].Text.TryGet("setting", out var setting) ? setting : null);
        Assert.Equal(MarkState.Broken, PanelMarks.Stashing(true, false, ".", "Middle mouse").State); // A broken reader outranks the setting.
        var cleanup = PanelMarks.Cleanup(false, true)!;
        Assert.Equal((MarkKind.Cleanup, "Cleanup", MarkState.Off), (cleanup.Kind, Heading(cleanup), cleanup.State)); // D136: the noun.
        Assert.Equal("Off: This server has turned off cleanup (the Server.AllowCleanup setting).", Text(cleanup));
        Assert.Equal(MarkState.Broken, PanelMarks.Cleanup(false, false)!.State);
        Assert.Equal(new[] { "yavsos_cleanup_reader_broken" }, Keys(PanelMarks.Cleanup(false, false)!));
        // B4.22: cleanup refused while Azu's exclusions can't be read shows on the Cleanup mark itself.
        var azuOff = PanelMarks.Cleanup(true, true, azuUnreadable: true)!;
        Assert.Equal(MarkState.Broken, azuOff.State); Assert.StartsWith("Not working: Cleanup is off: YAVSOS can't read AzuAutoStore's exclusions", Text(azuOff));

        // Inventory case 10: the count is a plural form and digits without grouping.
        var categories = PanelMarks.Categories(true, 2311, true)!;
        Assert.Equal("On: 2311 items are sorted into categories.\nOn: The server's category settings apply.", Text(categories));
        Assert.Equal("On: 1 item is sorted into categories.", Text(PanelMarks.Categories(true, 1, false)!).Split('\n')[0]);
        Assert.Contains("This game's own category settings apply.", Text(PanelMarks.Categories(true, 5, false)!));
        Assert.Null(PanelMarks.Server(false)); // D074 Q4: only a server without YAVSOS gets a mark.
        var server = PanelMarks.Server(true)!;
        Assert.Equal(("Server", MarkState.Working), (Heading(server), server.State));
        Assert.Equal("On: This server has no YAVSOS, so your own categories and settings apply.", Text(server));
    }

    [Fact]
    public void Marks_Stashing_NamesEachKey_AndLeavesOutAnUnsetOne()
    {
        Assert.Equal("On: Press . to stash into nearby chests, or Middle mouse on a stack to stash just that stack.",
            Text(PanelMarks.Stashing(true, true, ".", "Middle mouse")));
        Assert.Equal(new[] { "yavsos_mark_stashing_on_stash" }, Keys(PanelMarks.Stashing(true, true, ".", "")));
        Assert.Equal(new[] { "yavsos_mark_stashing_on_stack" }, Keys(PanelMarks.Stashing(true, true, "", "Middle mouse")));
        Assert.Equal(new[] { "yavsos_mark_stashing_on_unbound" }, Keys(PanelMarks.Stashing(true, true, "", "")));
        Assert.All(new[] { PanelMarks.Stashing(true, true, ".", ""), PanelMarks.Stashing(true, true, "", "x"), PanelMarks.Stashing(true, true, "", "") },
            mark => Assert.Equal(MarkState.Working, mark.State));
    }

    [Fact]
    public void Marks_ChestsUnbound_BreakStashingAndCleanup()
    {
        // SF2 (D087): without the game's chest members no chest can be read; that outranks the reader and the settings.
        var stash = PanelMarks.Stashing(false, false, ".", "Middle mouse", chests: false);
        Assert.Equal(MarkState.Broken, stash.State);
        Assert.Equal("Not working: Stashing is off: YAVSOS can't read chests in this game version (see the log).", Text(stash));
        var cleanup = PanelMarks.Cleanup(true, true, chests: false)!;
        Assert.Equal(("Cleanup", MarkState.Broken), (Heading(cleanup), cleanup.State));
        Assert.Equal("Not working: Cleanup is off: YAVSOS can't read chests in this game version (see the log).", Text(cleanup));
        Assert.Equal(MarkState.Working, PanelMarks.Stashing(true, true, ".", "Middle mouse", chests: true).State);
        Assert.Null(PanelMarks.Cleanup(true, true, chests: true));
    }

    // Inventory case 2: the verb and pronoun follow the number of parts as a plural form; the parts are a list.
    [Fact]
    public void Marks_Display_NamesThePartsTurnedOff()
    {
        // SF4 (D087): no mark while every display part works.
        Assert.Null(PanelMarks.Display(Array.Empty<DisplayPart>()));
        var one = PanelMarks.Display(new[] { DisplayPart.Search })!;
        Assert.Equal((MarkKind.Display, "Display", MarkState.Limited), (one.Kind, Heading(one), one.State));
        Assert.Equal("Partly working: After an error, YAVSOS turned off the YAVSOS panel's search (see the log). Restart the game to turn it back on. " +
            "Chest rules, stashing and cleanup still work.", Text(one));
        // In the order they failed, each once.
        var three = PanelMarks.Display(new[] { DisplayPart.Labels, DisplayPart.Search, DisplayPart.Labels, DisplayPart.Categories })!;
        Assert.StartsWith("Partly working: After an error, YAVSOS turned off chest labels, the YAVSOS panel's search and the category line in item tooltips (see the log)",
            Text(three));
        Assert.Contains("turn them back on", Text(three));
        Assert.Single(three.Lines);
        Assert.Equal(3L, three.Lines[0].Text.TryGet(MessageFormat.CountName, out var count) ? count : null);
        // Every part has its own message.
        Assert.Equal(Enum.GetValues(typeof(DisplayPart)).Length,
            Enum.GetValues(typeof(DisplayPart)).Cast<DisplayPart>().Select(x => PanelMarks.Part(x).Key).Distinct().Count());
        // A new part changes the key, so the panel redraws the marks.
        Assert.NotEqual(PanelMarks.Key(PanelMarks.Shown(one)), PanelMarks.Key(PanelMarks.Shown(three)));
    }

    // Inventory case 12: each MultiUserChest copy has its own sentence; no possessive is spliced into one.
    [Theory]
    [InlineData(MultiUserChestCopy.Standalone, true, "yavsos_mark_muc_standalone", MarkState.Working)]
    [InlineData(MultiUserChestCopy.AzuAutoStore, true, "yavsos_mark_muc_azu", MarkState.Working)]
    [InlineData(MultiUserChestCopy.Yavsos, true, "yavsos_mark_muc_yavsos", MarkState.Working)]
    [InlineData(MultiUserChestCopy.None, true, "yavsos_mark_muc_none", MarkState.Broken)]
    [InlineData(MultiUserChestCopy.Yavsos, false, "yavsos_mark_muc_none", MarkState.Broken)]
    public void Marks_MultiUserChest_PicksASentencePerCopy(MultiUserChestCopy copy, bool available, string key, MarkState state)
    {
        var mark = PanelMarks.MultiUserChest(copy, available);
        Assert.Equal((MarkKind.MultiUserChest, "MultiUserChest", state), (mark.Kind, Heading(mark), mark.State));
        Assert.Equal(new[] { key }, Keys(mark));
    }

    [Fact]
    public void Marks_ShownInOrder_AndKeyChangesWithAnyState()
    {
        var shown = PanelMarks.Shown(PanelMarks.Azu(null, true, true, true, true, true), PanelMarks.Stashing(true, true, ".", "Middle mouse"),
            PanelMarks.Cleanup(true, true), PanelMarks.MultiUserChest(MultiUserChestCopy.Yavsos, true));
        Assert.Equal(new[] { MarkKind.Stashing, MarkKind.MultiUserChest }, shown.Select(x => x.Kind));
        var off = PanelMarks.Shown(PanelMarks.Stashing(false, true, ".", "Middle mouse"), PanelMarks.MultiUserChest(MultiUserChestCopy.Yavsos, true));
        Assert.NotEqual(PanelMarks.Key(shown), PanelMarks.Key(off));
        Assert.Equal(PanelMarks.Key(off), PanelMarks.Key(PanelMarks.Shown(PanelMarks.Stashing(false, true, ".", "Middle mouse"),
            PanelMarks.MultiUserChest(MultiUserChestCopy.Yavsos, true))));
        // A changed value (another stash key) changes the key too.
        Assert.NotEqual(PanelMarks.Key(PanelMarks.Shown(PanelMarks.Stashing(true, true, ".", "Middle mouse"))),
            PanelMarks.Key(PanelMarks.Shown(PanelMarks.Stashing(true, true, ",", "Middle mouse"))));
    }

    // The marks' text is messages from the English file, with no markup; every mark line ends as a full sentence.
    [Fact]
    public void Marks_EveryLineIsAFullSentence_WithoutMarkup()
    {
        var marks = new[]
        {
            PanelMarks.Azu("3.1.7", true, false, false, false, false, Message.Of("yavsos_mark_azu_kept_out_item").With("item", "Tin").With("tab", Message.Of("yavsos_tab_items")),
                Message.Of("yavsos_mark_azu_exclusions_changed")),
            PanelMarks.Azu("3.1.7", true, true, true, true, true), PanelMarks.Azu("", false, true, true, true, true),
            PanelMarks.Stashing(true, true, ".", "Middle mouse"), PanelMarks.Stashing(false, true, ".", "x"), PanelMarks.Stashing(true, false, ".", "x"),
            PanelMarks.Cleanup(true, true, true), PanelMarks.Display(new[] { DisplayPart.Where, DisplayPart.Glow, DisplayPart.Results }),
            PanelMarks.Categories(true, 3, false, gaveUp: true), PanelMarks.Categories(false, 0, true, gaveUp: true), PanelMarks.Server(true),
            PanelMarks.MultiUserChest(MultiUserChestCopy.AzuAutoStore, true), PanelMarks.EpicLoot(true, true), PanelMarks.EpicLoot(true, false),
            PanelMarks.Drawers(true, true, true), PanelMarks.Drawers(true, false, true)
        };
        foreach (var mark in marks.Select(x => x!))
        foreach (var line in mark.Lines)
        {
            var text = EnglishText.Of(line.Text);
            Assert.DoesNotContain("[yavsos_", text); Assert.DoesNotContain("{{", text); Assert.DoesNotContain("<", text);
            Assert.True(!char.IsLower(text[0]) && text.EndsWith("."), text);
        }
    }
}
