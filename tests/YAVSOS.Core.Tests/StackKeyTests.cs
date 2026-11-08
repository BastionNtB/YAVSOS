using System;
using System.Linq;
using Xunit;
using YAVSOS.Core.Interface;

namespace YAVSOS.Core.Tests;

/// <summary>X68 (D162): the stack key's one-time move to Shift + middle mouse, and keeping its click from ZenSign's search.</summary>
public sealed class StackKeyTests
{
    [Theory]
    // saved main key, saved modifiers, record → move?
    [InlineData("Mouse2", "", "", true)]                              // The old default, never moved: moves.
    [InlineData("Mouse2", "", "stack-key-shift", false)]             // Moved before, then set back on purpose: kept.
    [InlineData("Mouse2", "", "other-move,stack-key-shift", false)]
    [InlineData("Mouse2", "", " stack-key-shift ", false)]
    [InlineData("Mouse2", "", "other-move", true)]                    // Another move's record doesn't count.
    [InlineData("Mouse2", "LeftShift", "", false)]                    // Already the new default.
    [InlineData("Mouse2", "LeftControl", "", false)]                  // A key the player chose.
    [InlineData("Mouse3", "", "", false)]
    [InlineData("Period", "", "", false)]
    [InlineData("None", "", "", false)]
    [InlineData(null, "", null, false)]
    public void ShouldMove_OnlyTheOldDefault_Once(string? main, string modifiers, string? record, bool expected) =>
        Assert.Equal(expected, StackKey.ShouldMove(main, modifiers.Split(',', StringSplitOptions.RemoveEmptyEntries), record));

    [Fact]
    public void Mark_AddsTheMoveOnce_AndKeepsOtherRecords()
    {
        Assert.Equal("stack-key-shift", StackKey.Mark(""));
        Assert.Equal("stack-key-shift", StackKey.Mark(null));
        Assert.Equal("stack-key-shift", StackKey.Mark("stack-key-shift"));
        Assert.Equal("a-move,stack-key-shift,z-move", StackKey.Mark("z-move, a-move,,"));
        Assert.True(StackKey.Done(StackKey.Mark("")));
        Assert.False(StackKey.Done("stack-key-shiftx"));
        // A whole first start: the old default moves and is marked; the next start leaves a value set back to it.
        var record = "";
        Assert.True(StackKey.ShouldMove("Mouse2", Array.Empty<string>(), record));
        record = StackKey.Mark(record);
        Assert.False(StackKey.ShouldMove("Mouse2", Array.Empty<string>(), record));
    }

    [Theory]
    [InlineData("Mouse2", "Mouse2", true)]
    [InlineData("Mouse2", "Mouse3", false)]
    [InlineData("Mouse2", null, false)]
    [InlineData("None", "None", false)]
    [InlineData("", "", false)]
    [InlineData("F", "F", true)]
    public void SameMainKey_IgnoresModifiers_AndNeverMatchesNone(string ours, string? theirs, bool expected) =>
        Assert.Equal(expected, PartnerKeys.SameMainKey(ours, theirs));

    // The frames a consumed click resets the other mod's button on: the press, every held frame, then two after the release.
    [Fact]
    public void ClickConsume_ResetsWhileHeld_AndTwoFramesAfterTheRelease_ThenEnds()
    {
        var consume = new ClickConsume();
        Assert.False(consume.Frame(true));        // Not armed: a press YAVSOS didn't act on is left alone.
        consume.Arm();
        Assert.True(consume.Armed);
        var held = new[] { true, true, true, false, false, false, false };
        Assert.Equal(new[] { true, true, true, true, true, false, false }, held.Select(consume.Frame).ToArray());
        Assert.False(consume.Armed);
        // A press again within the tail keeps it going.
        consume.Arm();
        Assert.Equal(new[] { true, true, true, true, false, false }, new[] { false, true, false, false, false, false }.Select(consume.Frame).ToArray());
        consume.Arm(); consume.Cancel();
        Assert.False(consume.Frame(true));
        Assert.Equal(2, ClickConsume.FramesAfterRelease);
    }

    // The click that ZenSign would see: a release after the reset frames never registers. Mirrors the game's ZInput
    // button (held/wasPressed/released, updated in its Tick; ResetButtonStatus clears them), with the reset before or after
    // the Tick in a frame.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClickConsume_HidesTheRelease_FromAButtonUpCheck(bool resetBeforeTick)
    {
        bool held = false, wasPressed = false, released = false;
        void Tick() { released = wasPressed && !held; wasPressed = held; }
        void Reset() { held = wasPressed = released = false; }
        var consume = new ClickConsume();
        var seen = 0;
        // Frames: press, 3 held, release, then idle. The press arrives at the frame's start (the input system's event).
        var input = new[] { "press", "held", "held", "held", "release", "idle", "idle", "idle" };
        for (var frame = 0; frame < input.Length; frame++)
        {
            if (input[frame] == "press") held = true;
            if (input[frame] == "release") held = false;
            if (!resetBeforeTick) Tick();
            if (frame == 0) consume.Arm();
            if (consume.Frame(input[frame] is "press" or "held")) Reset();
            if (resetBeforeTick) Tick();
            if (released) seen++;
        }
        Assert.Equal(0, seen);
        // Without the guard the same click is one release.
        held = wasPressed = released = false; seen = 0;
        foreach (var step in input) { if (step == "press") held = true; if (step == "release") held = false; Tick(); if (released) seen++; }
        Assert.Equal(1, seen);
    }
}
