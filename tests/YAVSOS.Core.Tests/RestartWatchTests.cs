using System.Collections.Generic;
using Xunit;

namespace YAVSOS.Core.Tests;

// D144 (issue #4): a stage that keeps restarting without finishing is named in the log, with a bounded number of lines.
public sealed class RestartWatchTests
{
    [Fact]
    public void NormalStartupChurnStaysQuiet()
    {
        var watch = new RestartWatch("Loaded sources");
        watch.Restarted(0, "the prefab count changed (10 → 11)"); watch.Restarted(1, "the prefab count changed (11 → 12)");
        Assert.Null(watch.Poll(5000)); // Two restarts are not enough, however long ago.
        Assert.Null(watch.Finished(5001)); Assert.Equal(0, watch.Restarts);
        for (var i = 0; i < 10; i++) watch.Restarted(6000 + i, "x");
        Assert.Null(watch.Poll(6059)); // Enough restarts, but under 60 s.
    }

    [Fact]
    public void WarnsWithCountAndCause_ThenAgainAfterAGrowingPause()
    {
        var watch = new RestartWatch("Loaded sources");
        for (var i = 0; i < 3; i++) watch.Restarted(i * 10, $"the prefab count changed ({100 + i} → {101 + i})");
        var first = watch.Poll(60);
        Assert.Equal("Loaded sources restarted 3 times without finishing (3 times in 60 s), most recently because " +
            "the prefab count changed (102 → 103). Categories wait until it finishes. A mod that keeps registering items or " +
            "prefabs while the game runs can cause this; please report it with this log.", first);
        Assert.Null(watch.Poll(61));
        watch.Restarted(70, "the item catalog changed");
        Assert.Null(watch.Poll(119.9));
        Assert.Contains("restarted 1 time without finishing since the last warning (4 times in 2 min), most recently because the item catalog changed", watch.Poll(120));
        Assert.Null(watch.Poll(10_000)); // No new restart: nothing new to say.
        Assert.Equal("Loaded sources finished after 4 restarts in 167 min.", watch.Finished(10_000));
        Assert.Null(watch.Poll(20_000)); Assert.Equal(0, watch.Restarts);
    }

    [Fact]
    public void AStageThatNeverFinishesLogsAboutSixLinesInItsFirstHourThenTwoAnHour()
    {
        var watch = new RestartWatch("The item catalog"); var warnings = new List<double>();
        for (var second = 0; second <= 3 * 3600; second++)
        {
            watch.Restarted(second, "the item count changed (1 → 2)"); // A restart every second.
            if (watch.Poll(second) != null) warnings.Add(second);
        }
        Assert.Equal(new double[] { 60, 120, 240, 480, 960, 1920 }, warnings.FindAll(t => t <= 3600));
        Assert.Equal(new double[] { 3720, 5520, 7320, 9120 }, warnings.FindAll(t => t > 3600));
    }
}
