using YAVSOS.Core.Interface;
using YAVSOS.Core.Messages;
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
        Assert.Equal(expected, Words(KeyNames.Format(main, modifiers?.Split(','))));

    [Fact]
    public void LeftAndRightModifiersShowOnce() =>
        Assert.Equal("Ctrl + .", Words(KeyNames.Format("Period", new[] { "LeftControl", "RightControl" })));

    // P10-04: key names that are words are messages; symbols and Unity's names are shown as they are; a language joins them.
    [Fact]
    public void WordsAreMessages_AndTheJoinIsTheLanguages()
    {
        Assert.Equal(MessageList.Of(ListKind.Keys, new object[] { Message.Of("yavsos_key_ctrl"), "." }), KeyNames.Format("Period", new[] { "LeftControl" }));
        Assert.Equal(MessageList.Of(ListKind.Keys, new object[] { Message.Of("yavsos_key_numpad").With("key", "Plus") }), KeyNames.Format("KeypadPlus"));
        Assert.Null(KeyNames.Format("None"));
        string Lookup(string key) => key switch { "yavsos_key_ctrl" => "Strg", "yavsos_key_join_pair" => "{{first}}+{{rest}}", _ => EnglishText.File.TryGetValue(key, out var x) ? x : null! };
        Assert.Equal("Strg+.", MessageFormat.Format(KeyNames.Format("Period", new[] { "LeftControl" })!, Lookup));
    }

    internal static string Words(MessageList? keys) => keys == null ? "" : EnglishText.Of(keys);
}
