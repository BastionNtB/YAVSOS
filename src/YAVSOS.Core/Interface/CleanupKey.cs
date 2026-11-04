using System;
using System.Collections.Generic;
using System.Linq;
using YAVSOS.Core.Messages;

namespace YAVSOS.Core.Interface;

/// <summary>A key with the modifiers held with it, as Unity <c>KeyCode</c> names (Core has no Unity reference).</summary>
public sealed class KeyChord
{
    public KeyChord(string mainKey, IEnumerable<string>? modifiers = null)
    {
        MainKey = mainKey ?? "";
        Modifiers = (modifiers ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrEmpty(x) && x != "None").Distinct().ToArray();
    }

    public string MainKey { get; }
    public IReadOnlyList<string> Modifiers { get; }
    /// <summary>No main key: an unbound shortcut.</summary>
    public bool IsNone => MainKey.Length == 0 || MainKey == "None";
    /// <summary>As a player reads it ("Ctrl + ."), for the game side to format (<see cref="KeyNames"/>); null when unbound.</summary>
    public MessageList? Name() => KeyNames.Format(MainKey, Modifiers);
    /// <summary>For logs and tests: the Unity key names; players get <see cref="Name"/>.</summary>
    public override string ToString() => string.Join(" + ", Modifiers.Append(MainKey));
}

/// <summary>
/// D125 (P9-27), D126: the cleanup key, Ctrl + <c>.</c> by default (every modifier clashes with something: Ctrl crouches,
/// Shift + <c>.</c> is Azu's default pause key, Alt dodges with ZenCombat). With AzuAutoStore, cleanup is the modifiers of
/// <c>Cleanup.Shortcut</c> plus Azu's store key; Ctrl when <c>Cleanup.Shortcut</c> has no modifier, so Azu's plain store
/// key never also cleans up; no key when <c>Cleanup.Shortcut</c> is cleared. Left and right modifiers count as the same key.
/// </summary>
public static class CleanupKey
{
    public const string FallbackModifier = "LeftControl";
    public static readonly KeyChord Default = new KeyChord("Period", new[] { FallbackModifier });

    /// <summary>
    /// The cleanup chord with AzuAutoStore: <paramref name="own"/>'s modifiers (or Ctrl) plus Azu's store key and its own
    /// modifiers. Null when either key is unbound: cleanup then has no key (the button and console command still work).
    /// </summary>
    public static KeyChord? WithAzu(KeyChord own, KeyChord azuStore)
    {
        if (own.IsNone || azuStore.IsNone) return null;
        var modifiers = own.Modifiers.Count == 0 ? new[] { FallbackModifier } : own.Modifiers;
        return new KeyChord(azuStore.MainKey, modifiers.Concat(azuStore.Modifiers));
    }

    /// <summary>The same key with the same modifiers, left and right modifiers alike, order ignored.</summary>
    public static bool Same(KeyChord? a, KeyChord? b) =>
        a != null && b != null && !a.IsNone && !b.IsNone && a.MainKey == b.MainKey &&
        new HashSet<string>(a.Modifiers.Select(Sideless)).SetEquals(b.Modifiers.Select(Sideless));

    /// <summary>The other side's name of a modifier ("RightShift" for "LeftShift"), or null for any other key.</summary>
    public static string? OtherSide(string key) =>
        key.StartsWith("Left", StringComparison.Ordinal) && IsModifier(key.Substring(4)) ? "Right" + key.Substring(4)
        : key.StartsWith("Right", StringComparison.Ordinal) && IsModifier(key.Substring(5)) ? "Left" + key.Substring(5)
        : null;

    private static bool IsModifier(string side) => side is "Control" or "Shift" or "Alt" or "Command" or "Apple" or "Windows";

    private static string Sideless(string key) => OtherSide(key) == null ? key
        : key.StartsWith("Left", StringComparison.Ordinal) ? key.Substring(4) : key.Substring(5);
}
