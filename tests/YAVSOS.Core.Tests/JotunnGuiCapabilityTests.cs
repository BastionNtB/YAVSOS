using System;
using System.Collections.Generic;
using Xunit;
using YAVSOS.Core.Tests.JotunnGuiFakes;
using YAVSOS.Integrations;
using YAVSOS.Interface;

namespace YAVSOS.Core.Tests;

// D058/D062: the styled panel turns off on a reshaped GUIManager, decided by shape, never by version.
public sealed class JotunnGuiCapabilityTests
{
    private static OptionalTypeResolver Resolver(Type manager, params (string Name, Type? Type)[] overrides)
    {
        var map = new Dictionary<string, Type?>(StringComparer.Ordinal)
        {
            [JotunnGuiCapability.Manager] = manager, ["UnityEngine.Transform"] = typeof(Transform), ["UnityEngine.Vector2"] = typeof(Vector2),
            ["UnityEngine.GameObject"] = typeof(GameObject), ["UnityEngine.Color"] = typeof(Color), ["UnityEngine.UI.Text"] = typeof(Text),
            ["UnityEngine.UI.ScrollRect"] = typeof(ScrollRect), ["UnityEngine.UI.InputField+ContentType"] = typeof(ContentType),
            ["UnityEngine.UI.DefaultControls+Resources"] = typeof(Resources),
        };
        foreach (var (name, type) in overrides) map[name] = type;
        return name => map.TryGetValue(name, out var type) ? type : null;
    }

    [Fact]
    public void Probe_AcceptsTheVerifiedShapeAtAnyVersion()
    {
        foreach (var version in new[] { "2.30.2", "9.0.0", "" })
        {
            var capability = JotunnGuiCapability.Probe(version, Resolver(typeof(GuiManager)));
            Assert.True(capability.Supported, capability.Witness);
            Assert.Equal(version, capability.ObservedVersion);
        }
    }

    [Theory]
    [InlineData(typeof(NoToggle), "CreateToggle")]
    [InlineData(typeof(ReshapedWoodpanel), "CreateWoodpanel")]
    [InlineData(typeof(InstanceBlockInput), "BlockInput")]
    [InlineData(typeof(WrongReturn), "CreateButton")]
    [InlineData(typeof(ColourAsProperty), "ValheimOrange")]
    [InlineData(typeof(NoInstance), "Instance")]
    [InlineData(typeof(NoFront), "CustomGUIFront")]
    public void Probe_RejectsAMissingOrReshapedMember(Type manager, string member)
    {
        var capability = JotunnGuiCapability.Probe("2.30.2", Resolver(manager));
        Assert.False(capability.Supported);
        Assert.Contains(member, capability.Diagnostic);
    }

    [Fact]
    public void Probe_RejectsAbsentJotunnOrAnUnresolvedType()
    {
        Assert.False(JotunnGuiCapability.Probe(null, null).Supported);
        var capability = JotunnGuiCapability.Probe("2.30.2", Resolver(typeof(GuiManager), ("UnityEngine.UI.ScrollRect", null)));
        Assert.False(capability.Supported);
        Assert.Contains("ScrollRect", capability.Diagnostic);
        Assert.False(JotunnGuiCapability.Probe("2.30.2", Resolver(typeof(GuiManager), (JotunnGuiCapability.Manager, null))).Supported);
    }
}
