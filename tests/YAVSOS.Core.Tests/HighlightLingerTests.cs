using YAVSOS.Core.Interface;
using Xunit;

namespace YAVSOS.Core.Tests;

public sealed class HighlightLingerTests
{
    [Fact]
    public void HeldWhileSearchingThenCountsDownFromTheClose()
    {
        var linger = new HighlightLinger();
        Assert.False(linger.On(0));
        linger.Hold();
        Assert.True(linger.On(1000)); // No time limit while the search is active.
        linger.Release(1000, 20);
        Assert.False(linger.Held);
        Assert.True(linger.On(1019.9f));
        Assert.False(linger.On(1020));
    }

    [Fact]
    public void ZeroEndsAtTheCloseAndTheRangeIsClamped()
    {
        var linger = new HighlightLinger();
        linger.Hold(); linger.Release(5, 0);
        Assert.False(linger.On(5));
        linger.Hold(); linger.Release(5, 500);
        Assert.True(linger.On(64.9f)); Assert.False(linger.On(65));
        linger.Hold(); linger.Release(5, -3);
        Assert.False(linger.On(5));
    }

    [Fact]
    public void ClearEndsAndANewSearchHoldsAgain()
    {
        var linger = new HighlightLinger();
        linger.Hold(); linger.End();
        Assert.False(linger.On(0));
        linger.Hold(); linger.Release(0, 20);
        linger.Hold(); // Reopened and searched again before the countdown ran out.
        Assert.True(linger.On(100));
    }

    [Fact]
    public void ReleaseWithoutAHoldChangesNothing()
    {
        var linger = new HighlightLinger();
        linger.Release(0, 20);
        Assert.False(linger.On(1));
        linger.Hold(); linger.Release(0, 20); linger.Release(15, 20); // A second close does not extend it.
        Assert.False(linger.On(20));
    }
}
