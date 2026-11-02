using YAVSOS.Core.Interface;
using Xunit;

namespace YAVSOS.Core.Tests;

public sealed class ResultCountdownTests
{
    private static int Frames(ResultCountdown countdown, float seconds, bool inventoryOpen, bool hold, float frame = 0.1f)
    {
        var frames = 0;
        while (countdown.Tick(frame, seconds, inventoryOpen, hold)) if (++frames > 100_000) break;
        return frames;
    }

    [Fact]
    public void ClosesByItselfAfterTheDefaultTime()
    {
        var countdown = new ResultCountdown();
        Assert.False(countdown.Tick(0.1f, ResultCountdown.DefaultSeconds, false, false)); // Nothing published yet.
        countdown.Restart();
        Assert.True(countdown.Tick(9.5f, 10, false, false));
        Assert.False(countdown.Tick(0.5f, 10, false, false));
        Assert.False(countdown.Running);
        Assert.False(countdown.Tick(0.5f, 10, false, false)); // Stays closed.
    }

    [Fact]
    public void ByDefaultTheInventoryBeingOpenDoesNotHoldIt()
    {
        var countdown = new ResultCountdown();
        countdown.Restart();
        Assert.True(countdown.Tick(5, 10, inventoryOpen: true, holdWhileOpen: false));
        Assert.False(countdown.Tick(5, 10, inventoryOpen: true, holdWhileOpen: false));
    }

    [Fact]
    public void WithTheHoldOnItWaitsWhileTheInventoryIsOpenAndResumesWhenItCloses()
    {
        var countdown = new ResultCountdown();
        countdown.Restart();
        Assert.True(countdown.Tick(4, 10, inventoryOpen: false, holdWhileOpen: true));
        for (var i = 0; i < 1000; i++) Assert.True(countdown.Tick(1, 10, inventoryOpen: true, holdWhileOpen: true));
        Assert.True(countdown.Tick(5.5f, 10, inventoryOpen: false, holdWhileOpen: true)); // 9.5 s counted.
        Assert.False(countdown.Tick(0.5f, 10, inventoryOpen: false, holdWhileOpen: true));
    }

    [Fact]
    public void TheCloseButtonStopsItAndANewResultRestartsTheFullTime()
    {
        var countdown = new ResultCountdown();
        countdown.Restart();
        Assert.True(countdown.Tick(8, 10, false, false));
        countdown.Stop();
        Assert.False(countdown.Tick(0, 10, true, true));
        countdown.Restart();
        Assert.True(countdown.Tick(8, 10, false, false));
        countdown.Restart(); // Another run finished while the last result was still up.
        Assert.True(countdown.Tick(9.5f, 10, false, false));
        Assert.False(countdown.Tick(0.5f, 10, false, false));
    }

    [Fact]
    public void AChangedSettingAppliesToThePanelAlreadyUp()
    {
        var countdown = new ResultCountdown();
        countdown.Restart();
        Assert.True(countdown.Tick(8, 10, false, false));
        Assert.False(countdown.Tick(0, 5, false, false)); // Shortened below what has already been shown.
        countdown.Restart();
        Assert.True(countdown.Tick(9, 10, false, false));
        Assert.True(countdown.Tick(10, 30, false, false)); // Lengthened.
        Assert.False(countdown.Tick(11, 30, false, false));
    }

    [Theory]
    [InlineData(0f, 3f)]
    [InlineData(-5f, 3f)]
    [InlineData(500f, 60f)]
    [InlineData(float.NaN, 10f)]
    [InlineData(float.PositiveInfinity, 60f)]
    [InlineData(25f, 25f)]
    public void TheTimeIsClampedToItsRange(float setting, float expected) => Assert.Equal(expected, ResultCountdown.Limit(setting));

    [Fact]
    public void FramesAddUpToTheLimitAndNegativeOrUnreadableFramesCountNothing()
    {
        var countdown = new ResultCountdown();
        countdown.Restart();
        Assert.InRange(Frames(countdown, 10, false, false), 98, 100);
        countdown.Restart();
        Assert.True(countdown.Tick(-50, 10, false, false));
        Assert.True(countdown.Tick(float.NaN, 10, false, false));
        Assert.True(countdown.Tick(9.5f, 10, false, false));
        Assert.False(countdown.Tick(0.5f, 10, false, false));
    }
}
