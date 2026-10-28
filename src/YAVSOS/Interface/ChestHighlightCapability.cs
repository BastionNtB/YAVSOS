using System;
using System.Linq;
using System.Reflection;
using YAVSOS.Core.Classification;
using YAVSOS.Integrations;

namespace YAVSOS.Interface;

/// <summary>
/// D058 startup check of the game members the chest highlight (P6-06) calls, verified on game 1.0.17: the
/// <c>MaterialMan</c> per-object property blocks (<c>instance</c>, <c>SetValue&lt;T&gt;</c>, <c>ResetValue</c>) and the
/// <c>ShaderProps._Color</c>/<c>_EmissionColor</c> IDs, as vanilla <c>WearNTear.Highlight</c> uses them. Reflection on
/// types the host resolves by name, so it runs on detached fakes. A failure turns off only the highlight; the panel
/// still lists the matching chests. Never decided by a version number.
/// </summary>
internal static class ChestHighlightCapability
{
    internal const string Adapter = "chest-highlight";
    internal const string MaterialMan = "MaterialMan", ShaderProps = "ShaderProps", GameObject = "UnityEngine.GameObject";

    internal static OptionalCapability Probe(OptionalTypeResolver? resolve)
    {
        if (resolve == null) return OptionalCapability.Incompatible(Adapter, "", "no type resolver");
        try
        {
            var manager = OptionalJotunnApi.Require(resolve, MaterialMan);
            var props = OptionalJotunnApi.Require(resolve, ShaderProps);
            var gameObject = OptionalJotunnApi.Require(resolve, GameObject);
            var instance = manager.GetProperty("instance", BindingFlags.Public | BindingFlags.Static);
            if (instance == null || instance.PropertyType != manager || instance.GetGetMethod() == null || instance.GetIndexParameters().Length != 0)
                throw new MissingMemberException("MaterialMan.instance");
            var set = manager.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(x => x.Name == "SetValue" && x.IsGenericMethodDefinition &&
                x.GetGenericArguments().Length == 1 && x.ReturnType == typeof(void) &&
                x.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { gameObject, typeof(int), x.GetGenericArguments()[0], typeof(bool) }));
            if (set == null) throw new MissingMemberException("MaterialMan.SetValue");
            var reset = manager.GetMethod("ResetValue", BindingFlags.Public | BindingFlags.Instance, null, new[] { gameObject, typeof(int) }, null);
            if (reset == null || reset.ReturnType != typeof(void)) throw new MissingMemberException("MaterialMan.ResetValue");
            foreach (var name in new[] { "_Color", "_EmissionColor" })
            {
                var field = props.GetField(name, BindingFlags.Public | BindingFlags.Static);
                if (field == null || field.FieldType != typeof(int)) throw new MissingMemberException("ShaderProps." + name);
            }
            return new OptionalCapability(Adapter, OptionalCapabilityState.Supported, "", "MaterialMan property blocks and ShaderProps colour IDs");
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error))
        {
            return OptionalCapability.Incompatible(Adapter, "", "game shape: " + error.Message);
        }
    }
}
