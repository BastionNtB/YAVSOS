using System;
using System.Linq;
using System.Reflection;
using YAVSOS.Core.Classification;
using YAVSOS.Integrations;

namespace YAVSOS.Interface;

/// <summary>
/// X66 (#79, D163): the shape of Zen.ModLib's public highlight, which ZenSign's search uses: a static class
/// <c>Zen.Lib.Highlight</c> with <c>Select(Piece, float duration, bool blink, Color color, Action onFinish)</c> and
/// <c>Unselect(Piece)</c>, both public static void (read from Zen.ModLib 1.14.22, 2026-11-07). Those two are required.
/// Two more parts are optional and only make YAVSOS a better neighbour: the public static <c>int Count</c> (how many
/// pieces any mod highlights) and the outline component <c>Zen.Components.QuickOutline</c> with its
/// <c>Color OutlineColor</c> getter, which tell whether another mod took or ended a chest's outline and let YAVSOS remove
/// the component it made. Without them the outline still works, as the prototype did. Reflection on types the host
/// resolves by name, so it runs on detached fakes; no reference to Zen.ModLib. Absent when the library isn't loaded; a
/// changed shape of the two required methods is incompatible and only the outline turns off, so the marker shows
/// instead (D058). Never decided by a version number.
/// </summary>
internal static class ZenHighlightCapability
{
    internal const string Adapter = "zen-highlight";
    internal const string PluginGuid = "ZenDragon.Zen.ModLib";
    internal const string Highlight = "Zen.Lib.Highlight", Outline = "Zen.Components.QuickOutline", Piece = "Piece", Color = "UnityEngine.Color";

    internal sealed class Api
    {
        internal Api(MethodInfo select, MethodInfo unselect, MethodInfo? count, Type? outline, MethodInfo? outlineColor)
        { Select = select; Unselect = unselect; Count = count; Outline = outline; OutlineColor = outlineColor; }
        internal MethodInfo Select { get; }
        internal MethodInfo Unselect { get; }
        /// <summary>The getter of <c>Highlight.Count</c>; null when it isn't there.</summary>
        internal MethodInfo? Count { get; }
        /// <summary>The outline component's type, with <see cref="OutlineColor"/>; both null when either isn't there.</summary>
        internal Type? Outline { get; }
        internal MethodInfo? OutlineColor { get; }
        /// <summary>The optional parts are there: YAVSOS can tell its outlines from another mod's.</summary>
        internal bool Reads => Count != null && Outline != null;
    }

    internal static OptionalCapability Probe(string? version, OptionalTypeResolver? resolve, out Api? api)
    {
        api = null;
        if (version == null || resolve == null) return OptionalCapability.Absent(Adapter);
        try
        {
            var highlight = OptionalJotunnApi.Require(resolve, Highlight);
            var piece = OptionalJotunnApi.Require(resolve, Piece);
            var color = OptionalJotunnApi.Require(resolve, Color);
            if (!highlight.IsAbstract || !highlight.IsSealed) throw new MissingMemberException(Highlight + " is not a static class");
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
            var select = highlight.GetMethods(flags).FirstOrDefault(x => x.Name == "Select" && x.ReturnType == typeof(void) && !x.IsGenericMethodDefinition &&
                x.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { piece, typeof(float), typeof(bool), color, typeof(Action) }));
            if (select == null) throw new MissingMemberException(Highlight + ".Select(Piece, float, bool, Color, Action)");
            var unselect = highlight.GetMethod("Unselect", flags, null, new[] { piece }, null);
            if (unselect == null || unselect.ReturnType != typeof(void)) throw new MissingMemberException(Highlight + ".Unselect(Piece)");
            var (count, outline, outlineColor, missing) = Optional(resolve, highlight, color);
            api = new Api(select, unselect, count, outline, outlineColor);
            return new OptionalCapability(Adapter, OptionalCapabilityState.Supported, version,
                "Highlight.Select and Unselect" + (api.Reads ? "; reads Count and the outline colour" : "; without " + missing + ", other mods' highlights can't be told apart"));
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error))
        {
            return OptionalCapability.Incompatible(Adapter, version, "API shape: " + error.Message);
        }
    }

    // The optional parts, each with its exact shape; a missing or changed one is left out, never an error.
    private static (MethodInfo? Count, Type? Outline, MethodInfo? OutlineColor, string Missing) Optional(OptionalTypeResolver resolve, Type highlight, Type color)
    {
        var count = highlight.GetProperty("Count", BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
        if (count != null && (count.ReturnType != typeof(int) || count.GetParameters().Length != 0)) count = null;
        Type? outline = null;
        try { outline = resolve(Outline); }
        catch (Exception error) when (OptionalFailure.Recoverable(error)) { }
        var getter = outline?.GetProperty("OutlineColor", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
        if (getter != null && (getter.ReturnType != color || getter.GetParameters().Length != 0)) getter = null;
        if (outline == null || getter == null || !IsComponent(outline)) { outline = null; getter = null; }
        var missing = count == null && outline == null ? "Highlight.Count and the outline colour" : count == null ? "Highlight.Count" : "the outline colour";
        return (count, outline, getter, count != null && outline != null ? "" : missing);
    }

    // A component type (GetComponent needs one); checked by its base names, so the fakes need no Unity.
    private static bool IsComponent(Type type)
    {
        for (var t = type.BaseType; t != null; t = t.BaseType)
            if (t.FullName is "UnityEngine.MonoBehaviour" or "UnityEngine.Component" or "UnityEngine.Behaviour") return true;
        return false;
    }
}
