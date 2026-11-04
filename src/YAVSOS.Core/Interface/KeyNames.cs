using System;
using System.Collections.Generic;
using System.Linq;
using YAVSOS.Core.Messages;

namespace YAVSOS.Core.Interface;

/// <summary>
/// P8-17 (Q12): a key binding as a player reads it, for hints such as "Stash stack [Middle mouse]": "Period" becomes ".",
/// "LeftControl" becomes "Ctrl", modifiers first and joined with " + ". Takes Unity <c>KeyCode</c> names, so Core needs
/// no Unity reference; an unknown name is shown as it is. P10-04: key names that are words are messages
/// (<c>yavsos_key_</c>), symbols and Unity's own names are shown as they are, and the parts are a
/// <see cref="ListKind.Keys"/> list, so a language can name the keys and join them its own way.
/// </summary>
public static class KeyNames
{
    private static readonly Dictionary<string, string> Symbols = new(StringComparer.Ordinal)
    {
        ["Period"] = ".", ["Comma"] = ",", ["Slash"] = "/", ["Backslash"] = "\\", ["Semicolon"] = ";", ["Quote"] = "'",
        ["BackQuote"] = "`", ["Minus"] = "-", ["Equals"] = "=", ["Plus"] = "+", ["LeftBracket"] = "[", ["RightBracket"] = "]",
    };

    private static readonly Dictionary<string, Message> Words = new(StringComparer.Ordinal)
    {
        ["LeftControl"] = Message.Of("yavsos_key_ctrl"), ["RightControl"] = Message.Of("yavsos_key_ctrl"),
        ["LeftShift"] = Message.Of("yavsos_key_shift"), ["RightShift"] = Message.Of("yavsos_key_shift"),
        ["LeftAlt"] = Message.Of("yavsos_key_alt"), ["RightAlt"] = Message.Of("yavsos_key_alt"),
        ["Return"] = Message.Of("yavsos_key_enter"), ["Escape"] = Message.Of("yavsos_key_esc"),
        ["Mouse0"] = Message.Of("yavsos_key_mouse_left"), ["Mouse1"] = Message.Of("yavsos_key_mouse_right"), ["Mouse2"] = Message.Of("yavsos_key_mouse_middle"),
    };

    /// <summary>The keys, modifiers first; null when there is no main key (an unbound shortcut).</summary>
    public static MessageList? Format(string mainKey, IEnumerable<string>? modifiers = null)
    {
        if (string.IsNullOrEmpty(mainKey) || mainKey == "None") return null;
        var parts = (modifiers ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrEmpty(x) && x != "None").Select(Name).Distinct().ToList();
        parts.Add(Name(mainKey));
        return MessageList.Of(ListKind.Keys, parts);
    }

    /// <summary>A string (a symbol or Unity's name) or a <see cref="Message"/> (a key name that is a word).</summary>
    private static object Name(string key)
    {
        if (Symbols.TryGetValue(key, out var symbol)) return symbol;
        if (Words.TryGetValue(key, out var word)) return word;
        if (key.Length == 6 && key.StartsWith("Alpha", StringComparison.Ordinal)) return key.Substring(5);
        if (key.StartsWith("Keypad", StringComparison.Ordinal) && key.Length > 6) return Message.Of("yavsos_key_numpad").With("key", key.Substring(6));
        if (key.Length == 6 && key.StartsWith("Mouse", StringComparison.Ordinal)) return Message.Of("yavsos_key_mouse").With("number", key[5] - '0' + 1);
        return key;
    }
}
