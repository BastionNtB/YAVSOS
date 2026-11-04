using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace YAVSOS.Core.Messages;

/// <summary>
/// X30 step 2 (P10-04): reads a message file, one flat JSON object of string values (D115, EXTRACTION-TOOL-CHECK §2).
/// As strict as Jotunn's loader, which refuses a value that isn't a string: a nested object, a number, a duplicate key
/// or trailing text is a <see cref="FormatException"/>. YAVSOS keeps the English it read here as its fallback when
/// Jotunn can't take the file, and the tests read every language file with it.
/// </summary>
public static class FlatJson
{
    public static Dictionary<string, string> Parse(string text)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var at = 0;
        if (text.Length > 0 && text[0] == '﻿') at++;
        Skip(text, ref at);
        Expect(text, ref at, '{');
        Skip(text, ref at);
        if (Peek(text, at) == '}') at++;
        else
            while (true)
            {
                Skip(text, ref at);
                var key = String(text, ref at);
                Skip(text, ref at);
                Expect(text, ref at, ':');
                Skip(text, ref at);
                if (Peek(text, at) != '"') throw Fail(at, "the value of \"" + key + "\" is not a string");
                var value = String(text, ref at);
                if (result.ContainsKey(key)) throw Fail(at, "the key \"" + key + "\" appears twice");
                result.Add(key, value);
                Skip(text, ref at);
                if (Peek(text, at) == ',') { at++; continue; }
                Expect(text, ref at, '}');
                break;
            }
        Skip(text, ref at);
        if (at != text.Length) throw Fail(at, "text after the closing brace");
        return result;
    }

    private static char Peek(string text, int at) => at < text.Length ? text[at] : '\0';

    private static void Skip(string text, ref int at)
    {
        while (at < text.Length && text[at] is ' ' or '\t' or '\r' or '\n') at++;
    }

    private static void Expect(string text, ref int at, char wanted)
    {
        if (Peek(text, at) != wanted) throw Fail(at, "expected '" + wanted + "'");
        at++;
    }

    private static string String(string text, ref int at)
    {
        Expect(text, ref at, '"');
        var value = new StringBuilder();
        while (true)
        {
            if (at >= text.Length) throw Fail(at, "unterminated string");
            var c = text[at++];
            if (c == '"') return value.ToString();
            if (c < ' ') throw Fail(at - 1, "a control character inside a string");
            if (c != '\\') { value.Append(c); continue; }
            if (at >= text.Length) throw Fail(at, "unterminated escape");
            var escape = text[at++];
            switch (escape)
            {
                case '"': value.Append('"'); break;
                case '\\': value.Append('\\'); break;
                case '/': value.Append('/'); break;
                case 'b': value.Append('\b'); break;
                case 'f': value.Append('\f'); break;
                case 'n': value.Append('\n'); break;
                case 'r': value.Append('\r'); break;
                case 't': value.Append('\t'); break;
                case 'u':
                    if (at + 4 > text.Length || !int.TryParse(text.Substring(at, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                        throw Fail(at, "a bad \\u escape");
                    value.Append((char)code); at += 4; break;
                default: throw Fail(at - 1, "an unknown escape \\" + escape);
            }
        }
    }

    private static FormatException Fail(int at, string what) =>
        new FormatException("Not a flat JSON object of strings at character " + at.ToString(CultureInfo.InvariantCulture) + ": " + what + ".");
}
