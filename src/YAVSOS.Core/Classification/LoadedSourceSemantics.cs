using System;

namespace YAVSOS.Core.Classification;

/// <summary>Narrow detached interpretation of the installed game's loaded-definition fields.</summary>
public static class LoadedSourceSemantics
{
    // Exact registration lookup precedes this fallback. Preserve arbitrary spaces/punctuation in real names.
    public static string? CloneBaseName(string name)
    {
        if (name == null) throw new ArgumentNullException(nameof(name));
        const string suffix = "(Clone)";
        return name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal)
            ? name.Substring(0, name.Length - suffix.Length) : null;
    }
}
