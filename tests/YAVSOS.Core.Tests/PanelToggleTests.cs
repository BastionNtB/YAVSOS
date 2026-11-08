using Xunit;
using YAVSOS.Core.Interface;

namespace YAVSOS.Core.Tests;

/// <summary>X58 (D148): the YAVSOS button below the chest window.</summary>
public sealed class PanelToggleTests
{
    // A chest window 600 wide, its bottom edge at y = 100 (y up), from x = 50.
    private static readonly PanelRect Window = new(50, 100, 600, 400);

    [Theory]
    [InlineData(ToggleButtonPlace.Left, 50)]
    [InlineData(ToggleButtonPlace.Middle, 310)] // 50 + (600 - 80) / 2
    [InlineData(ToggleButtonPlace.Right, 570)]  // 650 - 80
    public void Presets_PutTheButtonBelowTheirEndOfTheWindow(ToggleButtonPlace place, float left)
    {
        var (x, top) = PanelToggle.Place(Window, 80, place, PanelToggle.DefaultOffsetX, PanelToggle.DefaultOffsetY);
        Assert.Equal(left, x);
        Assert.Equal(94, top); // 6 below the bottom edge
    }

    [Fact]
    public void Offsets_MoveRightAndDown_FromThePreset()
    {
        Assert.Equal((70f, 80f), PanelToggle.Place(Window, 80, ToggleButtonPlace.Left, 20, 20));
        Assert.Equal((550f, 110f), PanelToggle.Place(Window, 80, ToggleButtonPlace.Right, -20, -10));
    }

    [Fact]
    public void AWiderWindow_KeepsEachPresetAtItsEnd()
    {
        var wide = new PanelRect(50, 100, 900, 400);
        Assert.Equal(50, PanelToggle.Place(wide, 80, ToggleButtonPlace.Left, 0, 6).Left);
        Assert.Equal(870, PanelToggle.Place(wide, 80, ToggleButtonPlace.Right, 0, 6).Left);
    }

    [Fact]
    public void Tooltip_SaysWhatAClickDoes()
    {
        var hide = PanelToggle.Tip(panelShown: true);
        Assert.Equal("YAVSOS", hide.Name);
        Assert.Equal("Hides the YAVSOS panel on every chest. The stash and cleanup keys and the item tooltips keep working.", EnglishText.Of(hide.Body!));
        Assert.Equal("Shows the YAVSOS panel on every chest, where you set what each chest accepts.", EnglishText.Of(PanelToggle.Tip(panelShown: false).Body!));
    }
}
