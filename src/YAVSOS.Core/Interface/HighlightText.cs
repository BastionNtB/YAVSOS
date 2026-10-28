using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace YAVSOS.Core.Interface;

/// <summary>
/// D101 (C7): the search highlight's status, one short line, with the chests it found in the line's tooltip. Nothing
/// while the first chests are still being checked (about a quarter second, so a "Looking…" line was only a flicker).
/// Pure and Unity-free.
/// </summary>
public static class HighlightText
{
    public const int MaximumListed = 12;

    /// <param name="found">Chests found so far.</param>
    /// <param name="lit">Whether any are coloured; when the game's colouring is unavailable they are only listed ("Found").</param>
    /// <param name="several">The search asks for several items ("these items" rather than "this").</param>
    public static string Status(bool active, int found, bool lit, bool scanning, float range, bool several)
    {
        if (!active) return "";
        if (found == 0)
            return scanning ? "" : "No chest within " + range.ToString("0", CultureInfo.InvariantCulture) + " m takes " + (several ? "these items." : "this.");
        return (lit ? "Lit up " : "Found ") + found.ToString(CultureInfo.InvariantCulture) + " chest" + (found == 1 ? "" : "s");
    }

    /// <summary>The status line's tooltip: what the search asked for, then each chest found, nearest first.</summary>
    public static (string Topic, string Text)? Tooltip(string subject, IReadOnlyList<string> chests)
    {
        if (chests == null) throw new ArgumentNullException(nameof(chests));
        if (chests.Count == 0) return null;
        var text = string.Join("\n", chests.Take(MaximumListed)) + (chests.Count > MaximumListed ? "\n… and " + (chests.Count - MaximumListed) + " more" : "");
        return (string.IsNullOrEmpty(subject) ? "Chests that take it" : "Chests that take " + subject, text);
    }
}
