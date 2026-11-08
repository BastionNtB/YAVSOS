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
    // D169 (#119): Esc clears the search, a second Esc closes the chest; the lights stay the whole time from the close.
    [Fact]
    public void ClearingTheSearchCountsDown_AndClosingStartsItOver()
    {
        var linger = new HighlightLinger();
        linger.Hold(); linger.LetGo(100, 8);
        Assert.False(linger.Held);
        Assert.True(linger.On(107.9f)); Assert.False(linger.On(108)); // Chest left open: 8 s from the clearing.
        linger.Hold(); linger.LetGo(200, 8); linger.Release(205, 8);
        Assert.True(linger.On(212.9f)); Assert.False(linger.On(213)); // Closed 5 s in: 8 s from the close.
        linger.Release(213, 8); // A later close does not bring it back.
        Assert.False(linger.On(214));
    }

    [Fact]
    public void ClosingAfterTheClearedCountdownRanOut_KeepsItOff()
    {
        var linger = new HighlightLinger();
        linger.Hold(); linger.LetGo(0, 8); linger.Release(30, 8);
        Assert.False(linger.On(30));
    }

    [Fact]
    public void LetGoWithoutAHold_OrAtZero_ChangesNothingOrEndsIt()
    {
        var linger = new HighlightLinger();
        linger.LetGo(0, 8);
        Assert.False(linger.On(0));
        linger.Hold(); linger.Release(0, 20); linger.LetGo(10, 20); // Already counting from a close: not extended.
        Assert.False(linger.On(20));
        linger.Hold(); linger.LetGo(50, 0);
        Assert.False(linger.On(50)); // 0: off at once, as clearing always was.
        linger.Release(51, 20);
        Assert.False(linger.On(52));
    }

    [Fact]
    public void ANewSearchAfterClearing_HoldsAgain_AndEndStopsEverything()
    {
        var linger = new HighlightLinger();
        linger.Hold(); linger.LetGo(0, 8); linger.Hold();
        Assert.True(linger.On(100));
        linger.LetGo(100, 8); linger.End(); linger.Release(101, 8);
        Assert.False(linger.On(101));
    }
}
