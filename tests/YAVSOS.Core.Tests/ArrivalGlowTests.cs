using System;
using System.Linq;
using YAVSOS.Core.Interface;
using Xunit;

namespace YAVSOS.Core.Tests;

public sealed class ArrivalGlowTests
{
    [Fact]
    public void FlashesSettlesAndFadesForTheHighlightTime()
    {
        var glow = new ArrivalGlow<string>();
        Assert.True(glow.Arrive("chest", 100, 20));
        Assert.Equal((ArrivalGlow<string>.FlashGain, 1f), glow.Light("chest", 100));
        var settling = glow.Light("chest", 100.3f)!.Value;
        Assert.InRange(settling.Gain, ArrivalGlow<string>.SteadyGain, ArrivalGlow<string>.FlashGain); // Between flash and steady.
        Assert.Equal((ArrivalGlow<string>.SteadyGain, 1f), glow.Light("chest", 110));
        var fading = glow.Light("chest", 119.75f)!.Value; // Half of the last half second.
        Assert.Equal(0.5f, fading.Tint, 3);
        Assert.Equal(ArrivalGlow<string>.SteadyGain * 0.5f, fading.Gain, 3);
        Assert.Null(glow.Light("chest", 120));
    }

    [Fact]
    public void EachEndIsReportedOnceAndForgottenAfterTheEffectWindow()
    {
        var glow = new ArrivalGlow<string>();
        glow.Arrive("chest", 0, 0.5f);
        Assert.Empty(glow.Expire(0.4f));
        Assert.Equal(new[] { "chest" }, glow.Expire(0.5f));
        Assert.Empty(glow.Expire(0.6f)); // Already reported.
        Assert.Equal(1, glow.Count); // Kept until its effect window passes, so a quick second arrival doesn't puff again.
        glow.Expire(1f);
        Assert.Equal(0, glow.Count);
    }

    [Fact]
    public void OneEffectPerChestPerSecondButEveryArrivalRelights()
    {
        var glow = new ArrivalGlow<string>();
        Assert.True(glow.Arrive("chest", 10, 5));
        Assert.False(glow.Arrive("chest", 10.25f, 5)); // A second stack in the same run: no second puff...
        Assert.Equal((ArrivalGlow<string>.FlashGain, 1f), glow.Light("chest", 10.25f)); // ...but it flashes again.
        Assert.NotNull(glow.Light("chest", 15.2f)); // The glow restarted at 10.25, so it lasts past 15.
        Assert.True(glow.Arrive("chest", 11, 5));
        Assert.True(glow.Arrive("other", 11, 5)); // Other chests are independent.
    }

    [Fact]
    public void ZeroSecondsPlaysTheEffectWithoutLighting()
    {
        var glow = new ArrivalGlow<string>();
        Assert.True(glow.Arrive("chest", 0, 0));
        Assert.Null(glow.Light("chest", 0));
        Assert.Empty(glow.Lit);
        Assert.Empty(glow.Expire(0)); // Never lit, so nothing to reset.
    }

    [Fact]
    public void APuffOnlyArrivalOnALitChestStillReportsItsEnd()
    {
        var glow = new ArrivalGlow<string>();
        glow.Arrive("chest", 0, 20);
        glow.Arrive("chest", 2, 0); // The player set 0 seconds meanwhile.
        Assert.Null(glow.Light("chest", 2));
        Assert.Equal(new[] { "chest" }, glow.Expire(2));
    }

    [Fact]
    public void TheTimeIsClampedLikeTheSearchHighlight()
    {
        var glow = new ArrivalGlow<string>();
        glow.Arrive("long", 0, 500);
        Assert.NotNull(glow.Light("long", HighlightLinger.MaximumSeconds - 0.01f));
        Assert.Null(glow.Light("long", HighlightLinger.MaximumSeconds));
        glow.Arrive("negative", 0, -3);
        Assert.DoesNotContain("negative", glow.Lit);
        glow.Arrive("unset", 0, float.NaN);
        Assert.NotNull(glow.Light("unset", HighlightLinger.DefaultSeconds - 0.01f));
    }

    [Fact]
    public void AStaysMarkLightsWithoutUsingTheEffectWindow()
    {
        var glow = new ArrivalGlow<string>();
        Assert.False(glow.Arrive("chest", 0, 20, withEffect: false)); // D123: lit, no effect.
        Assert.NotNull(glow.Light("chest", 0));
        Assert.True(glow.Arrive("chest", 0.2f, 20)); // An arrival right after still plays its effect.
    }

    [Fact]
    public void ClearForgetsEverything()
    {
        var glow = new ArrivalGlow<string>();
        glow.Arrive("chest", 0, 20);
        glow.Clear();
        Assert.Equal(0, glow.Count);
        Assert.True(glow.Arrive("chest", 0.1f, 20)); // Its effect may play again.
    }

    [Fact]
    public void EveryChoiceButNoneNamesAGameEffect()
    {
        Assert.Equal("vfx_Place_smallitem", ChestArrivalEffects.Prefab(ChestArrivalEffect.SmallItemPuff));
        Assert.Equal("", ChestArrivalEffects.Prefab(ChestArrivalEffect.None));
        var named = Enum.GetValues(typeof(ChestArrivalEffect)).Cast<ChestArrivalEffect>().Where(x => x != ChestArrivalEffect.None)
            .Select(ChestArrivalEffects.Prefab).ToList();
        Assert.All(named, x => Assert.StartsWith("vfx_", x));
        Assert.Equal(named.Count, named.Distinct().Count());
    }
}
