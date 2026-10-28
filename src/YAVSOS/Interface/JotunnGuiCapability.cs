using System;
using System.Linq;
using System.Reflection;
using YAVSOS.Core.Classification;
using YAVSOS.Integrations;

namespace YAVSOS.Interface;

/// <summary>
/// D058/D062 startup check of the Jotunn <c>GUIManager</c> members the rule panel calls (verified on 2.30.2). Reflection
/// only, against types the host resolves by name, so it runs on detached fakes. A missing or reshaped member turns off
/// the styled panel only; rules, stash and cleanup keep working. Never decided by Jotunn's version number.
/// </summary>
internal static class JotunnGuiCapability
{
    internal const string Adapter = "jotunn-gui";
    internal const string Manager = "Jotunn.Managers.GUIManager";
    private const string Transform = "UnityEngine.Transform", Vector2 = "UnityEngine.Vector2", GameObject = "UnityEngine.GameObject",
        Color = "UnityEngine.Color", Float = "System.Single", Int = "System.Int32", Bool = "System.Boolean", String = "System.String", Void = "System.Void";

    // (static, name, return type, parameter types). Keep in step with JotunnWidgets.
    internal static readonly (bool Static, string Name, string Returns, string[] Parameters)[] Methods =
    {
        (true, "BlockInput", Void, new[] { Bool }),
        (false, "CreateWoodpanel", GameObject, new[] { Transform, Vector2, Vector2, Vector2, Float, Float, Bool }),
        (false, "CreateButton", GameObject, new[] { String, Transform, Vector2, Vector2, Vector2, Float, Float }),
        (false, "CreateToggle", GameObject, new[] { Transform, Float, Float }),
        (false, "CreateInputField", GameObject, new[] { Transform, Vector2, Vector2, Vector2, "UnityEngine.UI.InputField+ContentType", String, Int, Float, Float }),
        (false, "ApplyTextStyle", Void, new[] { "UnityEngine.UI.Text", Color, Int, Bool }),
        (false, "ApplyScrollRectStyle", Void, new[] { "UnityEngine.UI.ScrollRect" }),
    };
    internal static readonly (string Name, string Type)[] Fields =
    {
        ("ValheimControlResources", "UnityEngine.UI.DefaultControls+Resources"), ("ValheimOrange", Color), ("ValheimBeige", Color),
    };
    // Static properties: the overlay canvas the result panel (P6-04) sits on, above the inventory screen.
    internal static readonly (string Name, string Type)[] Properties = { ("CustomGUIFront", GameObject) };

    internal static OptionalCapability Probe(string? version, OptionalTypeResolver? resolve)
    {
        if (version == null || resolve == null) return OptionalCapability.Incompatible(Adapter, "", "Jotunn not loaded");
        try
        {
            Type Named(string name) => name switch
            {
                Float => typeof(float), Int => typeof(int), Bool => typeof(bool), String => typeof(string), Void => typeof(void),
                _ => OptionalJotunnApi.Require(resolve, name)
            };
            var manager = Named(Manager);
            var instance = manager.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            if (instance == null || instance.PropertyType != manager || instance.GetGetMethod() == null || instance.GetIndexParameters().Length != 0)
                throw new MissingMemberException("GUIManager.Instance");
            foreach (var (isStatic, name, returns, parameters) in Methods)
            {
                var flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
                var method = manager.GetMethod(name, flags, null, parameters.Select(Named).ToArray(), null);
                if (method == null || method.ReturnType != Named(returns)) throw new MissingMemberException("GUIManager." + name);
            }
            foreach (var (name, type) in Fields)
            {
                var field = manager.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (field == null || field.FieldType != Named(type)) throw new MissingMemberException("GUIManager." + name);
            }
            foreach (var (name, type) in Properties)
            {
                var property = manager.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
                if (property == null || property.PropertyType != Named(type) || property.GetGetMethod() == null || property.GetIndexParameters().Length != 0)
                    throw new MissingMemberException("GUIManager." + name);
            }
            return new OptionalCapability(Adapter, OptionalCapabilityState.Supported, version, "GUIManager widgets, CustomGUIFront and BlockInput");
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error))
        {
            return OptionalCapability.Incompatible(Adapter, version, "API shape: " + error.Message);
        }
    }
}
