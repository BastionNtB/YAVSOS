using System;

namespace YAVSOS.Core.Classification;

/// <summary>Ordinal whole-prefab glob. Only * and ? are operators; no regex or culture-dependent matching.</summary>
internal sealed class PrefabPattern
{
    internal string Selector { get; }
    internal int Literals { get; }
    internal int Stars { get; }
    internal int Questions { get; }
    internal PrefabPattern(string match, string selector)
    {
        if (match != "exact" && match != "glob" || string.IsNullOrEmpty(selector) || selector.Length > 128)
            throw new ArgumentException("Bounded exact/glob selector required.");
        foreach (var c in selector)
        {
            if (c < '!' || c > '~' || "<>&\"'/\\[]".IndexOf(c) >= 0) throw new ArgumentException("Invalid prefab selector.");
            if (c == '*') Stars++; else if (c == '?') Questions++; else Literals++;
        }
        if ((match == "exact") != (Stars + Questions == 0)) throw new ArgumentException("Exact disallows wildcards; glob requires them.");
        Selector = selector;
    }
    internal bool Matches(string prefab, ClassificationWork work)
    {
        var input = 0; var pattern = 0; var star = -1; var retry = 0;
        while (input < prefab.Length)
        {
            work.Spend();
            if (pattern < Selector.Length && Selector[pattern] != '*' && (Selector[pattern] == '?' || Selector[pattern] == prefab[input])) { pattern++; input++; }
            else if (pattern < Selector.Length && Selector[pattern] == '*') { star = pattern++; retry = input; }
            else if (star >= 0) { pattern = star + 1; input = ++retry; }
            else return false;
        }
        while (pattern < Selector.Length && Selector[pattern] == '*') { work.Spend(); pattern++; }
        return pattern == Selector.Length;
    }
}
