using YAVSOS.Core.Interface;
using Xunit;

namespace YAVSOS.Core.Tests;

/// <summary>D125 (P9-27), D126: cleanup's key, with and without AzuAutoStore.</summary>
public sealed class CleanupKeyTests
{
    private static KeyChord Key(string main, params string[] modifiers) => new KeyChord(main, modifiers);

    [Fact]
    public void DefaultIsCtrlPeriod() => Assert.Equal("Ctrl + .", CleanupKey.Default.ToString());

    [Fact]
    public void WithAzuTakesTheModifierFromCleanupShortcutAndTheMainKeyFromAzu()
    {
        var chord = CleanupKey.WithAzu(Key("Period", "LeftControl"), Key("Comma"));
        Assert.Equal("Ctrl + ,", chord!.ToString());
        Assert.Equal(new[] { "LeftControl" }, chord.Modifiers);
        Assert.Equal("Comma", chord.MainKey);
    }

    [Fact]
    public void WithAzuKeepsADeliberateShiftOrAlt()
    {
        Assert.Equal("Shift + .", CleanupKey.WithAzu(Key("Period", "LeftShift"), Key("Period"))!.ToString());
        Assert.Equal("Alt + .", CleanupKey.WithAzu(Key("K", "LeftAlt"), Key("Period"))!.ToString());
    }

    [Fact]
    public void WithAzuAddsAzusOwnStoreModifiers() =>
        Assert.Equal("Shift + Ctrl + .", CleanupKey.WithAzu(Key("Period", "LeftShift"), Key("Period", "LeftControl"))!.ToString());

    [Fact]
    public void WithAzuFallsBackToCtrlWhenCleanupShortcutHasNoModifier()
    {
        // Without a modifier, every press of Azu's store key would also start cleanup.
        var chord = CleanupKey.WithAzu(Key("Period"), Key("Period"));
        Assert.Equal(new[] { CleanupKey.FallbackModifier }, chord!.Modifiers);
        Assert.Equal("Ctrl + .", chord.ToString());
    }

    [Fact]
    public void ClearedCleanupShortcutOrAzuStoreKeyMeansNoCleanupKey()
    {
        Assert.Null(CleanupKey.WithAzu(Key("None"), Key("Period")));
        Assert.Null(CleanupKey.WithAzu(Key(""), Key("Period")));
        Assert.Null(CleanupKey.WithAzu(Key("Period", "LeftControl"), Key("None")));
    }

    [Fact]
    public void DefaultChordDoesNotClashWithAzusDefaultPauseKey()
    {
        // D126: Azu's default Pause Shortcut is Shift + .; the default cleanup chord is Ctrl + . and leaves it alone.
        Assert.False(CleanupKey.Same(CleanupKey.WithAzu(CleanupKey.Default, Key("Period")), Key("Period", "LeftShift")));
        Assert.False(CleanupKey.Same(CleanupKey.WithAzu(Key("Period"), Key("Period")), Key("Period", "LeftShift")));
    }

    [Fact]
    public void AShiftCleanupShortcutClashesWithAzusDefaultPauseKey()
    {
        var chord = CleanupKey.WithAzu(Key("Period", "LeftShift"), Key("Period"));
        Assert.True(CleanupKey.Same(chord, Key("Period", "LeftShift"))); // Azu's default Pause Shortcut
        Assert.True(CleanupKey.Same(chord, Key("Period", "RightShift")));
        Assert.False(CleanupKey.Same(chord, Key("None"))); // the production pack's Pause Shortcut
        Assert.False(CleanupKey.Same(chord, null));
        Assert.False(CleanupKey.Same(chord, Key("Period", "LeftShift", "LeftControl")));
        Assert.False(CleanupKey.Same(chord, Key("Comma", "LeftShift")));
        Assert.False(CleanupKey.Same(CleanupKey.WithAzu(Key("Period", "LeftAlt"), Key("Period")), Key("Period", "LeftShift")));
    }

    [Fact]
    public void SameIgnoresModifierOrder() =>
        Assert.True(CleanupKey.Same(Key("Period", "LeftShift", "LeftControl"), Key("Period", "RightControl", "LeftShift")));

    [Theory]
    [InlineData("LeftShift", "RightShift")]
    [InlineData("RightControl", "LeftControl")]
    [InlineData("LeftAlt", "RightAlt")]
    [InlineData("Period", null)]
    [InlineData("Left", null)]
    [InlineData("LeftArrow", null)]
    public void OtherSide(string key, string? expected) => Assert.Equal(expected, CleanupKey.OtherSide(key));
}
