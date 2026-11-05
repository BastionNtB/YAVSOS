using System;
using Xunit;

namespace YAVSOS.Core.Tests;

// D144 (issue #4): a failed loading stage retries quickly a few times, then keeps trying with a growing pause.
public sealed class StageRetryTests
{
    [Fact]
    public void Schedule_QuickRetriesThenDoublingPauseUpToFiveMinutes()
    {
        var retry = new StageRetry(2, 3, 1000);
        Assert.Equal(" Trying again in 3 s (1 of 2).", retry.Schedule(0)); Assert.False(retry.Slow); Assert.Equal(3000, retry.RetryAt);
        Assert.Equal(" Trying again in 3 s (2 of 2).", retry.Schedule(0)); Assert.False(retry.Slow);
        var pauses = new double[6];
        for (var i = 0; i < pauses.Length; i++) { retry.Schedule(0); pauses[i] = retry.RetryAt / 1000; }
        Assert.Equal(new double[] { 60, 120, 240, 300, 300, 300 }, pauses);
        Assert.True(retry.Slow); Assert.Equal(8, retry.Attempts);
        Assert.Equal(" Trying again in 300 s (failure 9).", retry.Schedule(0));
    }

    [Fact]
    public void Take_FiresOnceWhenDue_HoldAndResetCancel()
    {
        var retry = new StageRetry(0, 0, 1);
        Assert.False(retry.Take(1e9)); // Nothing scheduled.
        retry.Schedule(10); Assert.True(retry.Slow);
        Assert.False(retry.Take(69.9)); Assert.True(retry.Take(70)); Assert.False(retry.Take(1e9));
        retry.Schedule(0); retry.Hold(); Assert.False(retry.Take(1e9)); Assert.Equal(2, retry.Attempts);
        retry.Schedule(0); retry.Reset(); Assert.False(retry.Take(1e9)); Assert.Equal(0, retry.Attempts); Assert.False(retry.Slow);
    }

    [Theory]
    [InlineData(-1, 3, 1000)]
    [InlineData(11, 3, 1000)]
    [InlineData(1, 61, 1000)]
    [InlineData(1, 3, 0)]
    [InlineData(1, double.NaN, 1000)]
    public void Constructor_RejectsOutOfRangeSettings(int quick, double seconds, double units) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new StageRetry(quick, seconds, units));
}
