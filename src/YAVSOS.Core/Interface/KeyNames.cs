using System;
using System.Collections.Generic;
using System.Linq;

namespace YAVSOS.Core.Interface;

/// <summary>
/// P8-17 (Q12): a key binding as a player reads it, for hints such as "Stash stack [Middle mouse]": "Period" becomes ".",
/// "LeftControl" becomes "Ctrl", modifiers first and joined with " + ". Takes Unity <c>KeyCode</c> names, so Core needs
/// no Unity reference; an unknown name is shown as it is.
/// </summary>
public static class KeyNames
{
    private static readonly Dictionary<string, string> Short = new(StringComparer.Ordinal)
    {
        ["Period"] = ".", ["Comma"] = ",", ["Slash"] = "/", ["Backslash"] = "\\", ["Semicolon"] = ";", ["Quote"] = "'",
        ["BackQuote"] = "`", ["Minus"] = "-", ["Equals"] = "=", ["Plus"] = "+", ["LeftBracket"] = "[", ["RightBracket"] = "]",
        ["LeftControl"] = "Ctrl", ["RightControl"] = "Ctrl", ["LeftShift"] = "Shift", ["RightShift"] = "Shift",
        ["LeftAlt"] = "Alt", ["RightAlt"] = "Alt", ["Return"] = "Enter", ["Escape"] = "Esc",
        ["Mouse0"] = "Left mouse", ["Mouse1"] = "Right mouse", ["Mouse2"] = "Middle mouse",
    };

    /// <summary>"" when there is no main key (an unbound shortcut).</summary>
    public static string Format(string mainKey, IEnumerable<string>? modifiers = null)
    {
        if (string.IsNullOrEmpty(mainKey) || mainKey == "None") return "";
        var parts = (modifiers ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrEmpty(x) && x != "None").Select(Name).Distinct().ToList();
        parts.Add(Name(mainKey));
        return string.Join(" + ", parts);
    }

    private static string Name(string key)
    {
        if (Short.TryGetValue(key, out var shown)) return shown;
        if (key.Length == 6 && key.StartsWith("Alpha", StringComparison.Ordinal)) return key.Substring(5);
        if (key.StartsWith("Keypad", StringComparison.Ordinal) && key.Length > 6) return "Num " + key.Substring(6);
        if (key.Length == 6 && key.StartsWith("Mouse", StringComparison.Ordinal)) return "Mouse " + (key[5] - '0' + 1);
        return key;
    }
}
