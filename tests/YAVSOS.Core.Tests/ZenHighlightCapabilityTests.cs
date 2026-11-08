using System;
using Xunit;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Tests.ZenHighlightFakes;
using YAVSOS.Interface;

#pragma warning disable IDE0060 // Shapes only: the probe reads signatures, never calls them.
namespace UnityEngine
{
    // Only its name matters: the probe checks that the outline type is a component by its base type's name.
    public class MonoBehaviour { }
}

namespace YAVSOS.Core.Tests.ZenHighlightFakes
{
    public sealed class Piece { }
    public struct Color { }
    // Zen.ModLib 1.14.22's internal outline component, as far as YAVSOS reads it.
    internal class QuickOutline : UnityEngine.MonoBehaviour
    {
        public Color OutlineColor { get; set; }
    }
    internal class PlainOutline // Not a component.
    {
        public Color OutlineColor { get; set; }
    }
    internal class OtherColorOutline : UnityEngine.MonoBehaviour
    {
        public int OutlineColor { get; set; }
    }
    public static class CountAsLong
    {
        public static long Count => 0;
        public static void Select(Piece? piece, float duration, bool blink, Color color = default, Action? onFinish = null) { }
        public static void Unselect(Piece? piece) { }
    }
    public static class Highlight
    {
        public static int Count => 0;
        public static void Select(Piece piece, bool blink, Color color = default) { }
        public static void Select(Piece? piece, float duration, bool blink, Color color = default, Action? onFinish = null) { }
        public static void Unselect(Piece? piece) { }
        public static void None() { }
    }
    public static class NoUnselect
    {
        public static void Select(Piece? piece, float duration, bool blink, Color color = default, Action? onFinish = null) { }
    }
    public static class NoDuration
    {
        public static void Select(Piece piece, bool blink, Color color = default) { }
        public static void Unselect(Piece? piece) { }
    }
    public sealed class NotStatic
    {
        public static void Select(Piece? piece, float duration, bool blink, Color color = default, Action? onFinish = null) { }
        public static void Unselect(Piece? piece) { }
    }
}

namespace YAVSOS.Core.Tests
{
    // X66 (#79, D163): Zen.ModLib's outline is used only when its shape is the one read from 1.14.22.
    public sealed class ZenHighlightCapabilityTests
    {
        private static Integrations.OptionalTypeResolver Resolver(Type highlight, Type? outline = null) => name => name switch
        {
            ZenHighlightCapability.Highlight => highlight, ZenHighlightCapability.Piece => typeof(Piece),
            ZenHighlightCapability.Color => typeof(Color), ZenHighlightCapability.Outline => outline, _ => null
        };

        // D163: Count and the outline's colour let YAVSOS tell its outlines from ZenSign's; without them it still works.
        [Fact]
        public void ReadsCountAndTheOutlineColour_WhenTheyHaveTheirShape()
        {
            var capability = ZenHighlightCapability.Probe("1.14.22", Resolver(typeof(Highlight), typeof(QuickOutline)), out var api);
            Assert.True(capability.Supported);
            Assert.True(api!.Reads);
            Assert.Equal(typeof(QuickOutline), api.Outline);
            Assert.Equal(typeof(Color), api.OutlineColor!.ReturnType);
            Assert.Equal(typeof(int), api.Count!.ReturnType);
            Assert.Contains("reads Count and the outline colour", capability.Diagnostic);
        }

        [Theory]
        [InlineData(typeof(Highlight), null, "the outline colour")]
        [InlineData(typeof(Highlight), typeof(PlainOutline), "the outline colour")]
        [InlineData(typeof(Highlight), typeof(OtherColorOutline), "the outline colour")]
        [InlineData(typeof(CountAsLong), typeof(QuickOutline), "Highlight.Count")]
        public void WithoutTheOptionalParts_TheOutlineStillWorks_AndSaysWhatIsMissing(Type highlight, Type? outline, string missing)
        {
            var capability = ZenHighlightCapability.Probe("1.14.22", Resolver(highlight, outline), out var api);
            Assert.True(capability.Supported);
            Assert.False(api!.Reads);
            Assert.Contains("without " + missing, capability.Diagnostic);
            Assert.NotNull(api.Select);
        }

        [Fact]
        public void AResolverThatThrowsForTheOutline_LeavesOnlyTheOutlineReadingOut()
        {
            Integrations.OptionalTypeResolver resolve = name => name == ZenHighlightCapability.Outline ? throw new TypeLoadException("gone") : Resolver(typeof(Highlight))(name);
            var capability = ZenHighlightCapability.Probe("1.14.22", resolve, out var api);
            Assert.True(capability.Supported);
            Assert.Null(api!.Outline);
            Assert.NotNull(api.Count);
        }

        [Fact]
        public void AcceptsTheLibrarysShape_AndFindsTheFiveArgumentSelect()
        {
            var capability = ZenHighlightCapability.Probe("1.14.22", Resolver(typeof(Highlight)), out var api);
            Assert.True(capability.Supported);
            Assert.NotNull(api);
            Assert.Equal(5, api!.Select.GetParameters().Length);
            Assert.Equal("Unselect", api.Unselect.Name);
        }

        [Fact]
        public void AnyVersionWithTheShape_IsSupported() // D058: the shape decides, not the number.
        {
            Assert.True(ZenHighlightCapability.Probe("9.0.0", Resolver(typeof(Highlight)), out _).Supported);
        }

        [Theory]
        [InlineData(typeof(NoUnselect), "Unselect")]
        [InlineData(typeof(NoDuration), "Select(Piece, float, bool, Color, Action)")]
        [InlineData(typeof(NotStatic), "not a static class")]
        public void AReshapedApi_IsIncompatible_AndSaysWhy(Type highlight, string why)
        {
            var capability = ZenHighlightCapability.Probe("1.14.22", Resolver(highlight), out var api);
            Assert.Equal(OptionalCapabilityState.Incompatible, capability.State);
            Assert.Contains(why, capability.Diagnostic);
            Assert.Null(api);
        }

        [Fact]
        public void WithoutTheLibrary_ItIsAbsent()
        {
            Assert.Equal(OptionalCapabilityState.Absent, ZenHighlightCapability.Probe(null, Resolver(typeof(Highlight)), out var api).State);
            Assert.Null(api);
            Assert.Equal(OptionalCapabilityState.Incompatible, ZenHighlightCapability.Probe("1.14.22", name => null, out _).State);
        }
    }
}
