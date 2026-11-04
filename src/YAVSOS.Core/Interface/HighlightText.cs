using System;
using System.Collections.Generic;
using System.Linq;
using YAVSOS.Core.Messages;

namespace YAVSOS.Core.Interface;

/// <summary>
/// D101 (C7): the search highlight's status, one short line, with the chests it found in the line's tooltip. Nothing
/// while the first chests are still being checked (about a quarter second, so a "Looking…" line was only a flicker).
/// D122: it counts the chests that hold a match and, with the "Also show chests that accept it" button on, the other
/// chests whose rules accept one; a lit chest's label says which. P10-04: every text is a message (inventory case 2:
/// the verb follows the chest count, "this"/"these" and "it"/"them" the number of things searched, each its own plural
/// message). Pure and Unity-free.
/// </summary>
public static class HighlightText
{
    public const int MaximumListed = 12;
    /// <summary>Most item names an "Accepts …" or "… stays here" label names before "+N more" (X54).</summary>
    public const int LabelNames = 2;

    /// <param name="holds">Chests found so far that hold a match.</param>
    /// <param name="takes">Other chests found so far whose rules accept one (only counted with the button on).</param>
    /// <param name="scanning">Still checking the chests in range.</param>
    /// <param name="range">The range searched, in meters; shown whole.</param>
    /// <param name="subjects">How many things the search asks for: 1 for a category or one item ("this"/"it"), more for several items ("these"/"them").</param>
    /// <param name="withTakes">The "Also show chests that accept it" button is on.</param>
    /// <returns>The status line, or null for none.</returns>
    public static Message? Status(bool active, int holds, int takes, bool scanning, float range, int subjects, bool withTakes)
    {
        if (!active) return null;
        subjects = Math.Max(1, subjects);
        if (holds == 0 && takes == 0)
            return scanning ? null : Message.Of(withTakes ? "yavsos_search_status_none_accepts" : "yavsos_search_status_none")
                .With("meters", (long)Math.Round(range, MidpointRounding.AwayFromZero)).Count(subjects);
        var thing = Message.Of("yavsos_search_this").Count(subjects);
        var held = holds == 0 ? Message.Of("yavsos_search_status_holds_none").With("this", thing)
            : Message.Of("yavsos_search_status_holds").With("this", thing).Count(holds);
        var parts = new List<Message> { held };
        if (withTakes)
        {
            var it = Message.Of("yavsos_search_it").Count(subjects);
            parts.Add(takes == 0 ? Message.Of("yavsos_search_status_accepts_none").With("it", it)
                : Message.Of(holds == 0 ? "yavsos_search_status_accepts" : "yavsos_search_status_accepts_more").With("it", it).Count(takes));
        }
        return Message.Of("yavsos_search_status").With("parts", MessageList.Of(ListKind.Parts, parts));
    }

    /// <summary>
    /// The status line's tooltip: what the search asked for (<paramref name="subject"/>, null: "it"), then the chests
    /// found, nearest first, each with why. Null without chests.
    /// </summary>
    public static Tooltip? Tooltip(MessageList? subject, IReadOnlyList<Message> holding, IReadOnlyList<Message> taking)
    {
        if (holding == null || taking == null) throw new ArgumentNullException(nameof(holding));
        if (holding.Count == 0 && taking.Count == 0) return null;
        var lines = holding.Select(x => Message.Of("yavsos_search_tip_holds").With("chest", x))
            .Concat(taking.Select(x => Message.Of("yavsos_search_tip_accepts").With("chest", x))).ToList();
        var list = MessageList.Of(ListKind.Lines, lines.Take(MaximumListed)).WithMore(Math.Max(0, lines.Count - MaximumListed));
        var heading = subject == null || subject.Items.Count == 0 && subject.More == 0 ? Message.Of("yavsos_search_tip_heading_it")
            : Message.Of("yavsos_search_tip_heading").With("subject", subject);
        return Messages.Tooltip.Of(heading, list);
    }

    /// <summary>
    /// Names for a label or heading: the first <paramref name="shown"/> distinct names, then "+N more". Null without
    /// names. The names are already in the player's language (and safe for rich text where the caller needs it).
    /// </summary>
    public static MessageList? Names(IEnumerable<string> names, int shown = LabelNames)
    {
        if (names == null) throw new ArgumentNullException(nameof(names));
        var distinct = names.Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0) return null;
        shown = Math.Max(1, shown);
        return MessageList.Of(ListKind.Comma, distinct.Take(shown)).WithMore(Math.Max(0, distinct.Count - shown));
    }

    /// <summary>A lit chest's label line when it holds a match: "Holds Copper ×57", "+2 more" for other matching items.</summary>
    public static Message Holds(string name, int count, int otherItems) =>
        Message.Of("yavsos_highlight_holds").With("items", MessageList.Of(ListKind.Comma, new[]
            { Message.Of("yavsos_highlight_amount").With("item", name ?? "").With("amount", count) }).WithMore(Math.Max(0, otherItems)));

    /// <summary>
    /// A lit chest's label line when its rule accepts a match: "Accepts Copper, Tin" (X54), or "Accepts it" without
    /// names.
    /// </summary>
    public static Message Accepts(MessageList? names) =>
        names == null ? Message.Of("yavsos_highlight_accepts_it") : Message.Of("yavsos_highlight_accepts").With("items", names);

    /// <summary>
    /// D123: a chest where something stays after cleanup that the player can act on: "Bronze Sword stays here: no chest
    /// accepts it", or "…: the chests that accept it are full"; "+1 more" and "them" when several items stayed.
    /// </summary>
    public static Message Stays(string name, int otherItems, bool full)
    {
        otherItems = Math.Max(0, otherItems);
        return Message.Of(full ? "yavsos_highlight_stays_full" : "yavsos_highlight_stays")
            .With("items", MessageList.Of(ListKind.Comma, new[] { name ?? "" }).WithMore(otherItems)).Count(1 + otherItems);
    }

    /// <summary>D122: the tooltip of the "Also show chests that accept it" button, by its state.</summary>
    public static Tooltip TakesToggle(bool on) => Messages.Tooltip.Of(Message.Of("yavsos_search_takers"),
        Message.Of(on ? "yavsos_search_takers_on" : "yavsos_search_takers_off"));
}
