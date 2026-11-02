using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace YAVSOS.Core.Interface;

/// <summary>
/// D101 (C7): the search highlight's status, one short line, with the chests it found in the line's tooltip. Nothing
/// while the first chests are still being checked (about a quarter second, so a "Looking…" line was only a flicker).
/// D122: it counts the chests that hold a match and, with the takes toggle on, the other chests whose rule would take
/// one; a lit chest's label says which. Pure and Unity-free.
/// </summary>
public static class HighlightText
{
    public const int MaximumListed = 12;

    /// <param name="holds">Chests found so far that hold a match.</param>
    /// <param name="takes">Other chests found so far whose rule would take one (only counted with the toggle on).</param>
    /// <param name="scanning">Still checking the chests in range.</param>
    /// <param name="several">The search asks for several items ("these"/"them" rather than "this"/"it").</param>
    /// <param name="withTakes">The takes toggle is on.</param>
    public static string Status(bool active, int holds, int takes, bool scanning, float range, bool several, bool withTakes)
    {
        if (!active) return "";
        var thing = several ? "these" : "this"; var pronoun = several ? "them" : "it";
        if (holds == 0 && takes == 0)
            return scanning ? "" : "No chest within " + range.ToString("0", CultureInfo.InvariantCulture) + " m " + (withTakes ? "holds or takes " : "holds ") + thing + ".";
        var line = holds == 0 ? "None hold " + thing : Count(holds) + (holds == 1 ? " holds " : " hold ") + thing;
        if (!withTakes) return line;
        return line + " · " + (takes == 0 ? "none take " + pronoun : Count(takes) + (holds == 0 ? "" : " more") + (takes == 1 ? " takes " : " take ") + pronoun);
    }

    /// <summary>The status line's tooltip: what the search asked for, then the chests found, nearest first, each with why.</summary>
    public static (string Topic, string Text)? Tooltip(string subject, IReadOnlyList<string> holding, IReadOnlyList<string> taking)
    {
        if (holding == null || taking == null) throw new ArgumentNullException(nameof(holding));
        if (holding.Count == 0 && taking.Count == 0) return null;
        var lines = holding.Select(x => x + " · holds").Concat(taking.Select(x => x + " · takes")).ToList();
        var text = string.Join("\n", lines.Take(MaximumListed)) + (lines.Count > MaximumListed ? "\n… and " + (lines.Count - MaximumListed) + " more" : "");
        return (string.IsNullOrEmpty(subject) ? "Chests lit for it" : "Chests lit for " + subject, text);
    }

    /// <summary>A lit chest's label line when it holds a match: "holds Copper ×57", "+2 more" for other matching items.</summary>
    public static string Holds(string name, int count, int otherItems) =>
        "holds " + name + " ×" + Count(count) + (otherItems > 0 ? " +" + Count(otherItems) + " more" : "");

    /// <summary>A lit chest's label line when its rule would take a match: "takes Copper".</summary>
    public static string Takes(string subject) => "takes " + (string.IsNullOrEmpty(subject) ? "it" : subject);

    /// <summary>
    /// D123: a chest cleanup left something in that the player can act on: "keeps Bronze Sword: no chest takes it", or
    /// "…: the chests that take it are full"; "+1 more" and "them" when several items stayed.
    /// </summary>
    public static string Keeps(string name, int otherItems, bool full)
    {
        var them = otherItems > 0 ? "them" : "it";
        return "keeps " + name + (otherItems > 0 ? " +" + Count(otherItems) + " more" : "") + ": " +
            (full ? "the chests that take " + them + " are full" : "no chest takes " + them);
    }

    /// <summary>D122: the rule-tag button's tooltip, by its state.</summary>
    public static (string Topic, string Text) TakesToggle(bool on) => ("Also chests that would take it", on
        ? "On: chests whose rule would take a match light up too, in your takes colour. Chests that hold one keep the holds colour. Click to light only the chests that hold it."
        : "Off: only chests that hold a match light up. Click to also light the chests whose rule would take it.");

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
