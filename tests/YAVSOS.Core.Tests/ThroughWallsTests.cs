using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;

namespace YAVSOS.Core.Tests;

// X66 (D163, #79): a glowing chest gets Zen.ModLib's outline when it can, otherwise the marker; the glow stays either way.
public sealed class ThroughWallsTests
{
    private const OptionalCapabilityState Supported = OptionalCapabilityState.Supported, Absent = OptionalCapabilityState.Absent,
        Incompatible = OptionalCapabilityState.Incompatible;

    [Theory]
    [InlineData(true, Supported, false, false, false, ThroughWallsWay.Outline)]
    [InlineData(true, Absent, false, false, false, ThroughWallsWay.Marker)]       // Zen.ModLib not installed.
    [InlineData(true, Incompatible, false, false, false, ThroughWallsWay.Marker)] // Its shape changed (D058).
    [InlineData(true, Supported, true, false, false, ThroughWallsWay.Marker)]     // The outline failed this session.
    [InlineData(true, Supported, false, true, false, ThroughWallsWay.Marker)]     // Not drawn, or taken by another mod.
    [InlineData(true, Absent, false, false, true, ThroughWallsWay.None)]          // The marker failed too: the glow only.
    [InlineData(true, Supported, false, false, true, ThroughWallsWay.Outline)]    // A failed marker doesn't touch the outline.
    [InlineData(false, Supported, false, false, false, ThroughWallsWay.None)]     // The player turned it off.
    [InlineData(false, Absent, false, false, false, ThroughWallsWay.None)]
    public void Choice_PrefersTheOutline_ThenTheMarker(bool on, OptionalCapabilityState zen, bool zenFailed, bool missing, bool markerFailed, ThroughWallsWay expected) =>
        Assert.Equal(expected, ThroughWallsChoice.For(on, zen, zenFailed, missing, markerFailed));

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(3, 3, true)]   // Only YAVSOS's own highlights.
    [InlineData(2, 3, true)]   // Fewer: some of YAVSOS's ended too.
    [InlineData(4, 3, false)]  // Another mod (ZenSign's search) highlights a piece: leave its outlines alone.
    [InlineData(1, 0, false)]
    [InlineData(-1, 0, false)] // Unreadable.
    public void Reclaim_OnlyWhenNoOtherModHighlights(int zenCount, int held, bool expected) =>
        Assert.Equal(expected, ThroughWallsChoice.CanReclaim(zenCount, held));

    private static ThroughWallsLook Outline(int kind = 0) => new ThroughWallsLook(ThroughWallsWay.Outline, kind);
    private static ThroughWallsLook Marker(int kind = 0) => new ThroughWallsLook(ThroughWallsWay.Marker, kind);

    [Fact]
    public void Changes_ShowNewChests_HideGoneOnes_AndRecolorInPlace()
    {
        var shown = new Dictionary<string, ThroughWallsLook> { ["a"] = Outline(), ["b"] = Marker(), ["c"] = Outline(0), ["d"] = Marker(0) };
        var wanted = new Dictionary<string, ThroughWallsLook> { ["a"] = Outline(), ["c"] = Outline(1), ["d"] = Marker(2), ["e"] = Marker() };
        var changes = ThroughWallsChanges<string>.Between(shown, wanted);
        Assert.Equal(new[] { "b" }, changes.Hide);
        Assert.Equal(new[] { "e" }, changes.Show.Select(x => x.Key));
        Assert.Equal(new[] { ("c", Outline(1)), ("d", Marker(2)) }, changes.Recolor.Select(x => (x.Key, x.Value)));
        Assert.False(changes.None);
    }

    [Fact]
    public void Changes_AnotherWay_IsAHideAndAShow_NeverARecolor()
    {
        // The outline was taken by another mod: the chest moves to the marker.
        var changes = ThroughWallsChanges<string>.Between(new Dictionary<string, ThroughWallsLook> { ["a"] = Outline(0) },
            new Dictionary<string, ThroughWallsLook> { ["a"] = Marker(1) });
        Assert.Equal(new[] { "a" }, changes.Hide);
        Assert.Equal(new[] { ("a", Marker(1)) }, changes.Show.Select(x => (x.Key, x.Value)));
        Assert.Empty(changes.Recolor);
    }

    [Fact]
    public void Changes_NoneLooks_CountAsNotShown_AndTheSameLooksChangeNothing()
    {
        var none = new ThroughWallsLook(ThroughWallsWay.None, 0);
        var changes = ThroughWallsChanges<string>.Between(new Dictionary<string, ThroughWallsLook> { ["a"] = Outline() },
            new Dictionary<string, ThroughWallsLook> { ["a"] = none, ["b"] = none });
        Assert.Equal(new[] { "a" }, changes.Hide);
        Assert.Empty(changes.Show);
        var same = new Dictionary<string, ThroughWallsLook> { ["a"] = Outline(2), ["b"] = Marker(1) };
        Assert.True(ThroughWallsChanges<string>.Between(same, new Dictionary<string, ThroughWallsLook>(same)).None);
        Assert.True(ThroughWallsChanges<string>.Between(new Dictionary<string, ThroughWallsLook>(), new Dictionary<string, ThroughWallsLook>()).None);
    }
}
