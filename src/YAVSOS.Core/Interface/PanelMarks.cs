using System;
using System.Collections.Generic;
using System.Linq;

namespace YAVSOS.Core.Interface;

/// <summary>
/// A mark's state (D071): working, broken (all or part), turned off by a setting, or working with a caution about this
/// chest, such as AzuAutoStore's config keeping some of the rule's items out of its chest type. Limited (D086): one optional
/// part failed and only it is off; the rest works. Shown amber, like a caution.
/// </summary>
public enum MarkState { Working, Broken, Off, Caution, Limited }

/// <summary>One part of an integration and whether it works; <see cref="Off"/> means a setting turned it off.</summary>
public sealed class MarkLine
{
    public string Text { get; }
    public MarkState State { get; }
    public MarkLine(string text, MarkState state) { Text = text ?? throw new ArgumentNullException(nameof(text)); State = state; }
}

/// <summary>
/// One status mark at the top right of the panel (D071): a ✓, ✗ or grey dash for one integration that is installed,
/// with lines saying what is on or off for the hover text. A mark is broken if any line is, else a caution if any line is,
/// else off if any line is: a caution is about the chest in front of the player, so it outranks a setting.
/// </summary>
public sealed class IntegrationMark
{
    public string Name { get; }
    public IReadOnlyList<MarkLine> Lines { get; }
    public MarkState State { get; }
    public IntegrationMark(string name, IEnumerable<MarkLine> lines)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Lines = (lines ?? throw new ArgumentNullException(nameof(lines))).ToList().AsReadOnly();
        State = Lines.Any(x => x.State == MarkState.Broken) ? MarkState.Broken : Lines.Any(x => x.State == MarkState.Caution) ? MarkState.Caution
            : Lines.Any(x => x.State == MarkState.Limited) ? MarkState.Limited : Lines.Any(x => x.State == MarkState.Off) ? MarkState.Off : MarkState.Working;
    }

    /// <summary>What the hover says, one line per part; the caller adds colour.</summary>
    public string Tooltip => string.Join("\n", Lines.Select(x => PanelMarks.Word(x.State) + x.Text));
}

/// <summary>
/// D071 status marks, built from plain facts the game side reads from each adapter, so the wording and the rule "only
/// installed mods get a mark" are tested here. Pure and Unity-free.
/// </summary>
public static class PanelMarks
{
    /// <summary>The start of a hover line: the panel's font has no ✓ or ✗, so words carry the state.</summary>
    public static string Word(MarkState state) => state switch
    { MarkState.Working => "On: ", MarkState.Broken => "Not working: ", MarkState.Caution => "This chest: ", MarkState.Limited => "Partly working: ", _ => "Off: " };

    /// <summary>
    /// AzuAutoStore installed (D042, D070). <paramref name="version"/> null: not installed, no mark. <paramref name="keptOut"/>:
    /// what its config keeps out of this chest type that the rule takes (D056), or null/empty when nothing is.
    /// <paramref name="exclusionsProblem"/>: why its exclusions are read only in part (D086 D2, D088), shown amber; null with
    /// <paramref name="exclusions"/> false means they can't be read and clean up is off.
    /// </summary>
    public static IntegrationMark? Azu(string? version, bool rules, bool order, bool pickup, bool exclusions, bool pause, string? keptOut = null,
        string? exclusionsProblem = null)
    {
        if (version == null) return null;
        var name = "AzuAutoStore" + (version.Length == 0 ? "" : " " + version);
        if (!rules)
            return new IntegrationMark(name, new[]
            {
                new MarkLine("chest rules: AzuAutoStore changed in a way YAVSOS does not recognise, so rules do not affect its store key. AzuAutoStore keeps working on its own.", MarkState.Broken)
            });
        return new IntegrationMark(name, new[]
        {
            new MarkLine("chest rules apply to its store key and pickup", MarkState.Working),
            new MarkLine(order ? "its store key fills chests in YAVSOS's order (priority, then most specific)" : "chest order: its store key keeps its own order", order ? MarkState.Working : MarkState.Broken),
            new MarkLine(pickup ? "ground pickup goes to the best ruled chest" : "ground pickup keeps AzuAutoStore's own choice", pickup ? MarkState.Working : MarkState.Broken),
            exclusions && exclusionsProblem == null ? new MarkLine("its excluded items are respected by clean up", MarkState.Working)
            : new MarkLine(exclusionsProblem ?? "its exclusion list can't be read, so clean up is off: it can't tell which chests AzuAutoStore keeps items out of. Everything else works.", MarkState.Limited),
            new MarkLine(pause ? "its pause key pauses ground pickup into nearby ruled chests" : "its pause key can't be read, so ground pickup may still fill chests while paused", pause ? MarkState.Working : MarkState.Limited)
        }.Concat(string.IsNullOrEmpty(keptOut) ? Array.Empty<MarkLine>() : new[] { new MarkLine(keptOut!, MarkState.Caution) }));
    }

    /// <summary>YAVSOS's own stashing, when AzuAutoStore is not installed (D061).</summary>
    public static IntegrationMark Stashing(bool allowed, bool reader, string storeKey, string itemKey) => new IntegrationMark("Stashing", new[]
    {
        !reader ? new MarkLine("stashing: your inventory can't be read exactly in this game version", MarkState.Broken)
        : allowed ? new MarkLine($"{storeKey} stores nearby, {itemKey} on a stack stores it", MarkState.Working)
        : new MarkLine("stashing is turned off by the server setting Server.AllowStashing", MarkState.Off)
    });

    /// <summary>Clean up nearby: a mark only when it is turned off or cannot run, since it is YAVSOS's own.</summary>
    public static IntegrationMark? Cleanup(bool allowed, bool reader) =>
        !reader ? new IntegrationMark("Clean up", new[] { new MarkLine("clean up: chests can't be read exactly in this game version", MarkState.Broken) })
        : !allowed ? new IntegrationMark("Clean up", new[] { new MarkLine("clean up is turned off by the server setting Server.AllowCleanup", MarkState.Off) })
        : null;

    /// <summary>
    /// YAVSOS's categories: amber while they load (D101, in place of the panel's old banner), then ready, with whose
    /// settings apply. Answers only become ready once the server's settings are accepted, so there is no "out of sync"
    /// state to show.
    /// </summary>
    /// <param name="gaveUp">D086 B5.36, D087: a stage gave up after its retries; the mark says so instead of "loading".</param>
    public static IntegrationMark Categories(bool ready, int items, bool fromServer, bool gaveUp = false)
    {
        if (!ready)
            return !gaveUp
                ? new IntegrationMark("Categories", new[] { new MarkLine(
                    "still loading. Exact items (Always, Never) already work; category parts start working when it's ready.", MarkState.Limited) })
                : new IntegrationMark("Categories", new[] { new MarkLine(
                "categories could not be built, so category rules take nothing. Exact items (Always, Never) still work. The log says why; it tries again when mods or settings change.",
                MarkState.Broken) });
        return new IntegrationMark("Categories", new[]
        {
            new MarkLine("ready: " + items.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " items sorted", MarkState.Working),
            new MarkLine(fromServer ? "the server's category settings apply" : "this game's own category settings apply", MarkState.Working)
        });
    }

    /// <summary>A server without YAVSOS (D044, D074 Q4): shown only then, since every other mode is the expected one.</summary>
    public static IntegrationMark? Server(bool withoutYavsos) => !withoutYavsos ? null : new IntegrationMark("Server", new[]
    {
        new MarkLine("this server has no YAVSOS, so your own categories and settings apply", MarkState.Working)
    });

    /// <summary>
    /// MultiUserChest (D043, D061): which copy carries moves to and from another player's chest. <paramref name="copy"/>
    /// empty: no copy is in use.
    /// </summary>
    public static IntegrationMark MultiUserChest(string copy, bool available) => new IntegrationMark("MultiUserChest", new[]
    {
        available ? new MarkLine("moves into another player's open chest go through " + copy, MarkState.Working)
        : new MarkLine("no working copy, so clean up and stashing skip chests another player has open", MarkState.Broken)
    });

    /// <summary>
    /// EpicLoot (P6-14): its rarity filter depends on YAVSOS reading it. <paramref name="recipesFailed"/> (D086 D1): the
    /// compatibility that registers EpicLoot's own crafted items failed, so only those items may lack categories. Null when
    /// not installed.
    /// </summary>
    public static IntegrationMark? EpicLoot(bool installed, bool reader, bool recipesFailed = false) => !installed ? null : new IntegrationMark("EpicLoot", new[]
    {
        reader ? new MarkLine("the rarity filter reads each item's rarity", MarkState.Working)
        : new MarkLine("rarity filter: EpicLoot changed in a way YAVSOS does not recognise. Everything else works.", MarkState.Broken)
    }.Concat(!recipesFailed ? Array.Empty<MarkLine>() : new[] { new MarkLine(
        "crafted items: EpicLoot changed how it adds its own crafted items (such as its belt and rings), so on a dedicated server those may have no category. Every other category works.",
        MarkState.Limited) }));

    /// <summary>RossItemDrawers (D047): AzuAutoStore fills a drawer reserved for an item. Null when not installed.</summary>
    public static IntegrationMark? Drawers(bool installed, bool working, bool azu) => !installed ? null : new IntegrationMark("Item drawers", new[]
    {
        working ? new MarkLine("AzuAutoStore fills empty drawers reserved for an item", MarkState.Working)
        : new MarkLine(azu ? "reserved drawers: RossItemDrawers changed in a way YAVSOS does not recognise. Drawers keep working on their own."
            : "reserved drawers need the AzuAutoStore integration, which is not working", MarkState.Broken)
    });

    /// <summary>The marks in display order, without the integrations that are not installed.</summary>
    public static IReadOnlyList<IntegrationMark> Shown(params IntegrationMark?[] marks) => marks.Where(x => x != null).Select(x => x!).ToList().AsReadOnly();

    /// <summary>A key over every mark's state and text, so the panel redraws the marks only when one changes.</summary>
    public static string Key(IReadOnlyList<IntegrationMark> marks) => string.Join("\n\n", marks.Select(x => x.Name + "|" + x.State + "|" + x.Tooltip));
}
