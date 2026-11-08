using System;
using System.Collections.Generic;
using System.Linq;

namespace YAVSOS.Core.Interface;

/// <summary>
/// X68 (D162): the stack key's default moved from plain middle mouse to Shift + middle mouse. A saved value equal to the
/// old default moves once to the new one; the settings file keeps a record of the move, so a player who sets plain middle
/// mouse again afterwards keeps it. Key names are Unity's <c>KeyCode</c> names, as the settings file writes them.
/// </summary>
public static class StackKey
{
    /// <summary>The move's name in the settings file's record of one-time moves.</summary>
    public const string MoveId = "stack-key-shift";
    /// <summary>The default before 1.3: middle mouse with no modifier.</summary>
    public const string OldMainKey = "Mouse2";

    /// <summary>
    /// Whether to move the saved stack key to the new default: it is exactly the old default (middle mouse, no modifier)
    /// and the record in <paramref name="moves"/> doesn't name this move yet. A player who chose plain middle mouse on
    /// purpose is moved too: the file can't tell the two apart (D162).
    /// </summary>
    public static bool ShouldMove(string? savedMainKey, IEnumerable<string>? savedModifiers, string? moves) =>
        !Done(moves) && string.Equals(savedMainKey, OldMainKey, StringComparison.Ordinal) && !(savedModifiers ?? Array.Empty<string>()).Any();

    /// <summary>Whether the record names this move.</summary>
    public static bool Done(string? moves) => Ids(moves).Contains(MoveId, StringComparer.Ordinal);

    /// <summary>
    /// The record with this move added, whether or not a value moved, so the move runs only once: one comma-separated
    /// list of move names, sorted, each once. Names it can't read are kept as they are.
    /// </summary>
    public static string Mark(string? moves) => string.Join(",", Ids(moves).Append(MoveId).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal));

    private static IEnumerable<string> Ids(string? moves) =>
        (moves ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length != 0);
}

/// <summary>
/// X68 (D162): another mod's key that fires on the same main key as one of YAVSOS's. Modifiers don't count: ZenSign's
/// search reads only its button (<c>ZInput.GetButtonUp</c>), so Shift + middle click is also its middle click.
/// </summary>
public static class PartnerKeys
{
    /// <summary>Both are real keys and the same one; <c>None</c> or an empty name never matches.</summary>
    public static bool SameMainKey(string? ours, string? theirs) =>
        !string.IsNullOrEmpty(ours) && ours != "None" && string.Equals(ours, theirs, StringComparison.Ordinal);
}

/// <summary>
/// X68 (D162): keeps one click from reaching another mod's button. Armed when YAVSOS acts on a press, it asks the caller
/// to reset that button every frame while the key is held and for <see cref="FramesAfterRelease"/> frames after, so the
/// release the other mod waits for never registers, whichever runs first in a frame. Bounded: it ends on its own once the
/// key is up. Main-thread owned.
/// </summary>
public sealed class ClickConsume
{
    /// <summary>Frames after the release that still reset, for an input system that reports the release a frame late.</summary>
    public const int FramesAfterRelease = 2;
    private int after = -1;

    public bool Armed => after >= 0;

    /// <summary>YAVSOS acted on this press.</summary>
    public void Arm() => after = FramesAfterRelease;

    /// <summary>One frame, with whether the key is held now. Returns true when the caller should reset the other mod's button.</summary>
    public bool Frame(bool held)
    {
        if (after < 0) return false;
        if (held) { after = FramesAfterRelease; return true; }
        if (after == 0) { after = -1; return false; }
        after--;
        return true;
    }

    public void Cancel() => after = -1;
}
