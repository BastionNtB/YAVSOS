using System;
using Xunit;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;

namespace YAVSOS.Core.Tests;

// P6-03 (D062): the rule panel beside the chest window's real rectangle, and its tri-state checkboxes.
public sealed class PanelPlacementTests
{
    private static readonly PanelRect Screen = new PanelRect(0, 0, 1920, 1080);
    private static PanelRect Place(PanelRect anchor, float width, float height, float x = 0, float y = 0, PanelRect? area = null) =>
        PanelPlacement.Beside(anchor, width, height, area ?? Screen, x, y);

    [Fact]
    public void Beside_PrefersTheRightOfTheWindowTopAligned()
    {
        var placed = Place(new PanelRect(500, 200, 600, 400), 380, 300);
        Assert.Equal(1108, placed.Left); Assert.Equal(600, placed.Top);
        Assert.Equal(380, placed.Width); Assert.Equal(300, placed.Height);
    }

    [Fact]
    public void Beside_FallsBackToTheLeftAndThenStaysOnScreen()
    {
        Assert.Equal(1400 - 8 - 380, Place(new PanelRect(1400, 200, 400, 400), 380, 300).Left);
        // Neither side fits: right of the window, pushed back inside the screen.
        Assert.Equal(1920 - 380, Place(new PanelRect(100, 200, 1700, 400), 380, 300).Left);
    }

    [Fact]
    public void Beside_AppliesTheOffsetRightAndDownThenClamps()
    {
        var anchor = new PanelRect(500, 200, 600, 400);
        var placed = Place(anchor, 380, 300, 50, 30);
        Assert.Equal(1158, placed.Left); Assert.Equal(570, placed.Top);
        Assert.Equal(1080, Place(anchor, 380, 300, 0, -900).Top);
        Assert.Equal(300, Place(anchor, 380, 300, 0, 900).Top);
        Assert.Equal(1540, Place(anchor, 380, 300, 5000).Left);
        var ignored = Place(anchor, 380, 300, float.NaN, float.PositiveInfinity);
        Assert.Equal(1108, ignored.Left); Assert.Equal(600, ignored.Top);
    }

    [Fact]
    public void Beside_KeepsTheTitleVisibleWhenThePanelIsTallerThanTheScreen()
    {
        Assert.Equal(1080, Place(new PanelRect(500, 200, 600, 400), 380, 1300, 0, 200).Top);
    }

    [Fact]
    public void Beside_WorksInCentredLocalSpace()
    {
        var centred = new PanelRect(-960, -540, 1920, 1080);
        var placed = Place(new PanelRect(-400, -300, 600, 400), 380, 300, area: centred);
        Assert.Equal(208, placed.Left); Assert.Equal(100, placed.Top);
    }

    [Fact]
    public void OffsetFor_ReproducesTheVisiblePlaceOnly()
    {
        var anchor = new PanelRect(500, 200, 600, 400);
        var placed = Place(anchor, 380, 300, -40, 25);
        Assert.Equal((-40f, 25f), PanelPlacement.OffsetFor(anchor, 380, 300, Screen, placed));
        // Dragged far past the right edge: only the shift the player could see is kept.
        var edge = Place(anchor, 380, 300, 4000);
        Assert.Equal((1540f - 1108f, 0f), PanelPlacement.OffsetFor(anchor, 380, 300, Screen, edge));
    }

    [Fact]
    public void Around_BoundsScaledOrRotatedCornersAndSkipsNonFinite()
    {
        // A grid rotated -90 degrees: its corners swap axes, and the bounds follow them.
        var rotated = PanelRect.Around(new (float, float)[] { (300, 100), (300, 500), (100, 500), (float.NaN, 0), (100, 100) });
        Assert.Equal(100, rotated.Left); Assert.Equal(100, rotated.Bottom); Assert.Equal(200, rotated.Width); Assert.Equal(400, rotated.Height);
        var union = rotated.Union(new PanelRect(50, 300, 100, 400));
        Assert.Equal(50, union.Left); Assert.Equal(700, union.Top); Assert.Equal(300, union.Right); Assert.Equal(100, union.Bottom);
        Assert.Throws<ArgumentException>(() => PanelRect.Around(new (float, float)[] { (float.NaN, 1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PanelRect(0, 0, -1, 1));
        // The four-corner form used every frame gives the same bounds.
        var four = PanelRect.Around((300, 100), (300, 500), (float.NaN, 0), (100, 100));
        Assert.Equal((100f, 100f, 200f, 400f), (four.Left, four.Bottom, four.Width, four.Height));
        Assert.Throws<ArgumentException>(() => PanelRect.Around((float.NaN, 1), (1, float.PositiveInfinity), (float.NaN, 0), (0, float.NaN)));
    }

    [Fact]
    public void CategoryCheckbox_ReplacesTheTextMarkers()
    {
        Assert.Equal((CheckboxState.Ticked, false), CategoryCheckbox.Of(CategorySelectionState.On));
        Assert.Equal((CheckboxState.Ticked, true), CategoryCheckbox.Of(CategorySelectionState.Implied));
        Assert.Equal((CheckboxState.Partial, false), CategoryCheckbox.Of(CategorySelectionState.Partial));
        Assert.Equal((CheckboxState.Empty, false), CategoryCheckbox.Of(CategorySelectionState.Off));
    }
}
