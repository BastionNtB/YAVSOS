using YAVSOS.Core.Interface;
using Xunit;

namespace YAVSOS.Core.Tests;

public sealed class KeyNamesTests
{
    [Theory]
    [InlineData("Period", null, ".")]
    [InlineData("Q", "LeftControl", "Ctrl + Q")]
    [InlineData("Period", "LeftControl,LeftShift", "Ctrl + Shift + .")]
    [InlineData("Alpha5", null, "5")]
    [InlineData("Keypad1", null, "Num 1")]
    [InlineData("Mouse2", null, "Middle mouse")]
    [InlineData("Mouse4", null, "Mouse 5")]
    [InlineData("F5", null, "F5")]
    [InlineData("None", null, "")]
    [InlineData("", null, "")]
    public void FormatsAsAPlayerReadsIt(string main, string? modifiers, string expected) =>
        Assert.Equal(expected, KeyNames.Format(main, modifiers?.Split(',')));

    [Fact]
    public void LeftAndRightModifiersShowOnce() =>
        Assert.Equal("Ctrl + .", KeyNames.Format("Period", new[] { "LeftControl", "RightControl" }));
}
