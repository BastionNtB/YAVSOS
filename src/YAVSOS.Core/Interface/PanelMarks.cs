using System;
using System.Collections.Generic;
using System.Linq;
using YAVSOS.Core.Messages;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Core.Interface;

/// <summary>
/// A mark's state (D071): working, broken (all or part), turned off by a setting, or working with a caution about this
/// chest, such as AzuAutoStore's settings keeping some of the rule's items out of its chest type. Limited (D086): one
/// optional part failed and only it is off; the rest works. Shown amber, like a caution.
/// </summary>
public enum MarkState { Working, Broken, Off, Caution, Limited }

/// <summary>Which status mark this is, so code never tells marks apart by their (translated) names.</summary>
public enum MarkKind { AzuAutoStore, Stashing, Cleanup, Display, Categories, Server, MultiUserChest, EpicLoot, Drawers }

/// <summary>SF4 (D087): the display-only parts that can turn themselves off after an error, for the Display mark.</summary>
public enum DisplayPart { Where, Search, Categories, Labels, Glow, Results }

/// <summary>One part of an integration and whether it works; <see cref="MarkState.Off"/> means a setting turned it off.</summary>
public sealed class MarkLine
{
    /// <summary>A full sentence; the hover line puts the state word before it (<see cref="Line"/>).</summary>
    public Message Text { get; }
    public MarkState State { get; }
    public MarkLine(Message text, MarkState state) { Text = text ?? throw new ArgumentNullException(nameof(text)); State = state; }

    /// <summary>
    /// The hover line: the state word in the state's color, then the sentence (inventory case 4). The sentence stands on
    /// its own, so a translator can reorder the two in <c>yavsos_mark_line</c> without changing either.
    /// </summary>
    public Message Line => Message.Of("yavsos_mark_line").With("state", PanelMarks.Word(State), PanelMarks.ToneOf(State)).With("text", Text);
}

/// <summary>
/// One status mark at the top right of the panel (D071): a ✓, ✗ or grey dash for one integration that is installed,
/// with lines saying what is on or off for the hover text. A mark is broken if any line is, else a caution if any line is,
/// else off if any line is: a caution is about the chest in front of the player, so it outranks a setting.
/// </summary>
public sealed class IntegrationMark
{
    public MarkKind Kind { get; }
    public Message Name { get; }
    public IReadOnlyList<MarkLine> Lines { get; }
    public MarkState State { get; }
    public IntegrationMark(MarkKind kind, Message name, IEnumerable<MarkLine> lines)
    {
        Kind = kind;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Lines = (lines ?? throw new ArgumentNullException(nameof(lines))).ToList().AsReadOnly();
        State = Lines.Any(x => x.State == MarkState.Broken) ? MarkState.Broken : Lines.Any(x => x.State == MarkState.Caution) ? MarkState.Caution
            : Lines.Any(x => x.State == MarkState.Limited) ? MarkState.Limited : Lines.Any(x => x.State == MarkState.Off) ? MarkState.Off : MarkState.Working;
    }

    /// <summary>What the hover says: the mark's name as the heading, then one line per part.</summary>
    public Tooltip Tooltip => Tooltip.Of(Name, MessageList.Of(ListKind.Lines, Lines.Select(x => x.Line)));
}

/// <summary>
/// D071 status marks, built from plain facts the game side reads from each adapter, so the choice of message and the
/// rule "only installed mods get a mark" are tested here. Returns messages, never English. Pure and Unity-free.
/// </summary>
public static class PanelMarks
{
    /// <summary>Server settings the marks name (inventory case 11): identifiers, shown as written in every language.</summary>
    public const string AllowStashingSetting = "Server.AllowStashing", AllowCleanupSetting = "Server.AllowCleanup";

    /// <summary>The state word that starts a hover line: the panel's font has no ✓ or ✗, so words carry the state.</summary>
    public static Message Word(MarkState state) => Message.Of(state switch
    {
        MarkState.Working => "yavsos_mark_state_working",
        MarkState.Broken => "yavsos_mark_state_broken",
        MarkState.Caution => "yavsos_mark_state_caution",
        MarkState.Limited => "yavsos_mark_state_limited",
        _ => "yavsos_mark_state_off"
    });

    /// <summary>The state word's color, the same as the mark's symbol.</summary>
    public static Tone ToneOf(MarkState state) => state switch
    {
        MarkState.Working => Tone.Working,
        MarkState.Broken => Tone.Broken,
        MarkState.Caution or MarkState.Limited => Tone.Caution,
        _ => Tone.Faint
    };

    private static MarkLine Line(string key, MarkState state) => new(Message.Of(key), state);

    /// <summary>
    /// AzuAutoStore installed (D042, D070). <paramref name="version"/> null: not installed, no mark. <paramref name="keptOut"/>:
    /// what its settings keep out of this chest type that the rule accepts (D056), or null when nothing is.
    /// <paramref name="exclusionsProblem"/>: why its exclusions are read only in part (D086 D2, D088), shown amber; null with
    /// <paramref name="exclusions"/> false means they can't be read and cleanup is off.
    /// </summary>
    public static IntegrationMark? Azu(string? version, bool rules, bool order, bool pickup, bool exclusions, bool pause, Message? keptOut = null,
        Message? exclusionsProblem = null)
    {
        if (version == null) return null;
        var name = version.Length == 0 ? Message.Of("yavsos_mark_azu_name") : Message.Of("yavsos_mark_azu_name_version").With("version", version);
        if (!rules)
            return new IntegrationMark(MarkKind.AzuAutoStore, name, new[] { Line("yavsos_mark_azu_rules_broken", MarkState.Broken) });
        return new IntegrationMark(MarkKind.AzuAutoStore, name, new[]
        {
            Line("yavsos_mark_azu_rules", MarkState.Working),
            order ? Line("yavsos_mark_azu_order", MarkState.Working) : Line("yavsos_mark_azu_order_broken", MarkState.Broken),
            pickup ? Line("yavsos_mark_azu_pickup", MarkState.Working) : Line("yavsos_mark_azu_pickup_broken", MarkState.Broken),
            exclusions && exclusionsProblem == null ? Line("yavsos_mark_azu_exclusions", MarkState.Working)
            : new MarkLine(exclusionsProblem ?? Message.Of("yavsos_mark_azu_exclusions_unreadable"), MarkState.Limited),
            pause ? Line("yavsos_mark_azu_pause", MarkState.Working) : Line("yavsos_mark_azu_pause_unreadable", MarkState.Limited)
        }.Concat(keptOut == null ? Array.Empty<MarkLine>() : new[] { new MarkLine(keptOut, MarkState.Caution) }));
    }

    /// <summary>YAVSOS's own stashing, when AzuAutoStore is not installed (D061).</summary>
    /// <param name="stashKey">The stash key as a player reads it ("."); <paramref name="stackKey"/> the stack key's.</param>
    /// <param name="chests">SF2 (D087): the game's chest members could be bound; false means no chest can be read.</param>
    public static IntegrationMark Stashing(bool allowed, bool reader, string stashKey, string stackKey, bool chests = true) =>
        new(MarkKind.Stashing, Message.Of("yavsos_mark_stashing_name"), new[]
        {
            !chests ? Line("yavsos_mark_stashing_chests_unreadable", MarkState.Broken)
            : !reader ? Line("yavsos_stash_reader_broken", MarkState.Broken)
            : allowed ? new MarkLine(StashKeys(stashKey ?? "", stackKey ?? ""), MarkState.Working)
            : new MarkLine(Message.Of("yavsos_mark_stashing_off").With("setting", AllowStashingSetting), MarkState.Off)
        });

    // Which keys stash; an unbound key ("") is left out rather than shown as a gap in the sentence.
    private static Message StashKeys(string stashKey, string stackKey) =>
        stashKey.Length != 0 && stackKey.Length != 0 ? Message.Of("yavsos_mark_stashing_on").With("stash", stashKey).With("stack", stackKey)
        : stashKey.Length != 0 ? Message.Of("yavsos_mark_stashing_on_stash").With("stash", stashKey)
        : stackKey.Length != 0 ? Message.Of("yavsos_mark_stashing_on_stack").With("stack", stackKey)
        : Message.Of("yavsos_mark_stashing_on_unbound");

    /// <summary>Clean up nearby: a mark only when it is turned off or cannot run, since it is YAVSOS's own.</summary>
    /// <param name="azuUnreadable">B4.22, D088: AzuAutoStore's exclusions can't be read at all, so cleanup refuses to run.</param>
    /// <param name="chests">SF2 (D087): as for <see cref="Stashing"/>.</param>
    public static IntegrationMark? Cleanup(bool allowed, bool reader, bool azuUnreadable = false, bool chests = true)
    {
        var line = !chests ? Line("yavsos_mark_cleanup_chests_unreadable", MarkState.Broken)
            : !reader ? Line("yavsos_cleanup_reader_broken", MarkState.Broken)
            : !allowed ? new MarkLine(Message.Of("yavsos_mark_cleanup_off").With("setting", AllowCleanupSetting), MarkState.Off)
            : azuUnreadable ? Line("yavsos_mark_cleanup_azu_unreadable", MarkState.Broken)
            : null;
        return line == null ? null : new IntegrationMark(MarkKind.Cleanup, Message.Of("yavsos_mark_cleanup_name"), new[] { line });
    }

    /// <summary>
    /// SF4 (D087): display-only parts turned off after an error this session, named in the order they failed; null when
    /// none is. Each also logged its own warning. Chest rules, stashing and cleanup don't depend on them.
    /// </summary>
    public static IntegrationMark? Display(IEnumerable<DisplayPart> off)
    {
        var parts = (off ?? throw new ArgumentNullException(nameof(off))).Distinct().ToList();
        if (parts.Count == 0) return null;
        // One sentence whose verb and pronoun follow the number of parts (inventory case 2): a plural form, not words in code.
        var named = MessageList.Of(ListKind.And, parts.Select(Part));
        return new IntegrationMark(MarkKind.Display, Message.Of("yavsos_mark_display_name"), new[]
        {
            new MarkLine(Message.Of("yavsos_mark_display_off").With("parts", named).Count(parts.Count), MarkState.Limited)
        });
    }

    /// <summary>A display part's name, written to read after "YAVSOS turned off".</summary>
    public static Message Part(DisplayPart part) => Message.Of(part switch
    {
        DisplayPart.Where => "yavsos_mark_display_part_where",
        DisplayPart.Search => "yavsos_mark_display_part_search",
        DisplayPart.Categories => "yavsos_mark_display_part_categories",
        DisplayPart.Labels => "yavsos_mark_display_part_labels",
        DisplayPart.Glow => "yavsos_mark_display_part_glow",
        _ => "yavsos_mark_display_part_results"
    });

    /// <summary>
    /// YAVSOS's categories: amber while they load (D101, in place of the panel's old banner), then ready, with whose
    /// settings apply. Answers only become ready once the server's settings are accepted, so there is no "out of sync"
    /// state to show.
    /// </summary>
    /// <param name="gaveUp">D086 B5.36, D087: a stage gave up after its retries; the mark says so instead of "loading". While the
    /// previous answers are kept (D086 D7), it adds an amber line (B4.26).</param>
    /// <param name="serverRejected">B4.9: the server's category settings were rejected, so categories can't load.</param>
    public static IntegrationMark Categories(bool ready, int items, bool fromServer, bool gaveUp = false, bool serverRejected = false)
    {
        var name = Message.Of("yavsos_mark_categories_name");
        if (!ready && serverRejected)
            return new IntegrationMark(MarkKind.Categories, name, new[] { Line("yavsos_mark_categories_server_rejected", MarkState.Broken) });
        if (!ready)
            return new IntegrationMark(MarkKind.Categories, name, new[]
            {
                !gaveUp ? Line("yavsos_mark_categories_loading", MarkState.Limited) : Line("yavsos_mark_categories_gave_up", MarkState.Broken)
            });
        return new IntegrationMark(MarkKind.Categories, name, new[]
        {
            new MarkLine(Message.Of("yavsos_mark_categories_ready").Count(items), MarkState.Working),
            Line(fromServer ? "yavsos_mark_categories_from_server" : "yavsos_mark_categories_from_game", MarkState.Working)
        }.Concat(!gaveUp ? Array.Empty<MarkLine>() : new[] { Line("yavsos_mark_categories_stale", MarkState.Limited) }));
    }

    /// <summary>A server without YAVSOS (D044, D074 Q4): shown only then, since every other mode is the expected one.</summary>
    public static IntegrationMark? Server(bool withoutYavsos) => !withoutYavsos ? null
        : new IntegrationMark(MarkKind.Server, Message.Of("yavsos_mark_server_name"), new[] { Line("yavsos_mark_server_without_yavsos", MarkState.Working) });

    /// <summary>
    /// MultiUserChest (D043, D061): which copy carries moves to and from another player's chest. <paramref name="available"/>
    /// false, or no copy: none is in use. Each copy has its own sentence, so no possessive is spliced in (inventory case 12).
    /// </summary>
    public static IntegrationMark MultiUserChest(MultiUserChestCopy copy, bool available) =>
        new(MarkKind.MultiUserChest, Message.Of("yavsos_mark_muc_name"), new[]
        {
            !available ? Line("yavsos_mark_muc_none", MarkState.Broken) : copy switch
            {
                MultiUserChestCopy.Standalone => Line("yavsos_mark_muc_standalone", MarkState.Working),
                MultiUserChestCopy.AzuAutoStore => Line("yavsos_mark_muc_azu", MarkState.Working),
                MultiUserChestCopy.Yavsos => Line("yavsos_mark_muc_yavsos", MarkState.Working),
                _ => Line("yavsos_mark_muc_none", MarkState.Broken)
            }
        });

    /// <summary>EpicLoot (P6-14): its rarity filter depends on YAVSOS reading it. Null when not installed.</summary>
    public static IntegrationMark? EpicLoot(bool installed, bool reader) => !installed ? null
        : new IntegrationMark(MarkKind.EpicLoot, Message.Of("yavsos_mark_epicloot_name"), new[]
        {
            reader ? Line("yavsos_mark_epicloot_rarity", MarkState.Working) : Line("yavsos_mark_epicloot_broken", MarkState.Broken)
        });

    /// <summary>RossItemDrawers (D047): AzuAutoStore fills a drawer reserved for an item. Null when not installed.</summary>
    public static IntegrationMark? Drawers(bool installed, bool working, bool azu) => !installed ? null
        : new IntegrationMark(MarkKind.Drawers, Message.Of("yavsos_mark_drawers_name"), new[]
        {
            working ? Line("yavsos_mark_drawers_reserved", MarkState.Working)
            : Line(azu ? "yavsos_mark_drawers_broken" : "yavsos_mark_drawers_no_azu", MarkState.Broken)
        });

    /// <summary>The marks in display order, without the integrations that are not installed.</summary>
    public static IReadOnlyList<IntegrationMark> Shown(params IntegrationMark?[] marks) => marks.Where(x => x != null).Select(x => x!).ToList().AsReadOnly();

    /// <summary>A key over every mark's state and message, so the panel redraws the marks only when one changes.</summary>
    public static string Key(IReadOnlyList<IntegrationMark> marks) => string.Join("\n\n", marks.Select(x => x.Kind + "|" + x.State + "|" + x.Tooltip));
}
