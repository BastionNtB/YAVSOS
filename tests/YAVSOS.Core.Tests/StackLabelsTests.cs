using System;
using System.Linq;
using Xunit;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Messages;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Core.Tests;

// X65 (D163, #78): wherever YAVSOS names a chest, a chest in a stack says its place in it; a chest alone keeps its form.
public sealed class StackLabelsTests
{
    private static StackPlace Of(int place, int size) => new StackPlace(0, place, size);

    [Theory]
    [InlineData(2, 3, "Reinforced chest 6 m away, 2 of 3 from the bottom")]
    [InlineData(1, 3, "Reinforced chest 6 m away, bottom of a stack of 3")]
    [InlineData(3, 3, "Reinforced chest 6 m away, top of a stack of 3")]
    [InlineData(1, 2, "Reinforced chest 6 m away, bottom of a stack of 2")]
    [InlineData(2, 2, "Reinforced chest 6 m away, top of a stack of 2")]
    [InlineData(4, 6, "Reinforced chest 6 m away, 4 of 6 from the bottom")]
    [InlineData(1, 1, "Reinforced chest 6 m away")]
    [InlineData(0, 0, "Reinforced chest 6 m away")] // No place known (default): today's form.
    public void ChestName_SaysItsPlaceInItsStack(int place, int size, string expected) =>
        Assert.Equal(expected, EnglishText.Of(ChestName.Away("Reinforced chest", 6, Of(place, size)).Said));

    [Fact]
    public void AChestAlone_KeepsTodaysMessage_AndTheOpenOrNamelessChestSaysNoPlace()
    {
        Assert.Equal(Message.Of("yavsos_where_chest_away").With("chest", "Chest").With("meters", 4L), ChestName.Away("Chest", 4).Said);
        Assert.Equal(ChestName.Away("Chest", 4).Said, ChestName.Away("Chest", 4, Of(1, 1)).Said);
        Assert.Equal("this chest", EnglishText.Of(ChestName.Open.Said));
        Assert.Equal("a chest nearby", EnglishText.Of(ChestName.Away("", 4, Of(2, 3)).Said));
    }

    [Fact]
    public void Named_KeepsTheDistanceAndThePlace_AndEqualityCountsThePlace()
    {
        var chest = ChestName.Away("Chest<b>", 6, Of(2, 3));
        var renamed = chest.Named("Chest‹b›");
        Assert.Equal(("Chest‹b›", 6L, Of(2, 3)), (renamed.Name, renamed.Meters, renamed.Stack));
        Assert.Same(ChestName.Open, ChestName.Open.Named("x"));
        Assert.Equal(ChestName.Away("Chest", 6, Of(2, 3)), ChestName.Away("Chest", 6, Of(2, 3)));
        Assert.NotEqual(ChestName.Away("Chest", 6, Of(2, 3)), ChestName.Away("Chest", 6, Of(3, 3)));
        Assert.NotEqual(ChestName.Away("Chest", 6, Of(2, 3)), ChestName.Away("Chest", 6));
    }

    // The item tooltip's where-line, stash and cleanup.
    [Fact]
    public void WhereLine_NamesTheChestByItsPlace()
    {
        var name = ChestName.Away("Reinforced chest", 6, Of(2, 3));
        var stash = new WhereLine(Message.Of("yavsos_where_lead_stash"), name.Said);
        Assert.Equal("Stash stack: Reinforced chest 6 m away, 2 of 3 from the bottom", EnglishText.Of(stash.Whole));
        var moves = new WhereLine(Message.Of("yavsos_where_lead_cleanup"),
            Message.Of("yavsos_where_cleanup_moves_because").With("chest", name.Said).With("because", Message.Of("yavsos_where_because_holds")));
        Assert.Equal("Clean up: moves to Reinforced chest 6 m away, 2 of 3 from the bottom (already holds some)", EnglishText.Of(moves.Whole));
    }

    // The where-line keeps its 160 characters: a long chest name is cut, and the lead stays whole.
    [Fact]
    public void WhereLine_WithAPlace_StillFitsItsLimit()
    {
        var name = ChestName.Away(new string('x', 140), 12, Of(11, 12));
        var line = new WhereLine(Message.Of("yavsos_where_lead_stash"), name.Said);
        var fitted = EnglishText.Of(line.Fit(EnglishText.Of, x => x));
        Assert.True(fitted.Length <= ExplanationView.MaximumText, fitted.Length + " characters");
        Assert.StartsWith("Stash stack: xxx", fitted);
        Assert.EndsWith("…", fitted);
    }

    // The result panel's place lines carry the place through the label and the escaped name.
    [Fact]
    public void ResultPlace_SaysThePlace_BeforeTheChestLabel()
    {
        var report = RunReport.ForStash();
        report.Moved(30, item: "Copper", stack: Guid.NewGuid(), destination: "metal");
        report.Moved(10, item: "Tin", stack: Guid.NewGuid(), destination: "spare");
        var detail = report.Detail(placeName: id => id == "metal"
            ? ResultPlace.Of(ChestName.Away("Reinforced chest", 6, Of(2, 3)).Named("Reinforced chest"), "Metals")
            : ResultPlace.Of(ChestName.Away("Chest", 6, Of(1, 3))));
        Assert.Equal(new[]
        {
            "Stashed 2 stacks (40 items) in 2 places:",
            "Reinforced chest 6 m away, 2 of 3 from the bottom · Metals: Copper ×30",
            "Chest 6 m away, bottom of a stack of 3: Tin ×10"
        }, detail.Lines.Select(x => EnglishText.Of(x.Text)).ToArray());
    }

    // The search status line's tooltip has its own frame ("6 m", no "away").
    [Theory]
    [InlineData(2, 3, "Reinforced chest 6 m, 2 of 3 from the bottom")]
    [InlineData(3, 3, "Reinforced chest 6 m, top of a stack of 3")]
    [InlineData(1, 1, "Reinforced chest 6 m")]
    [InlineData(0, 0, "Reinforced chest 6 m")]
    public void SearchTooltip_NamesTheChestByItsPlace(int place, int size, string expected)
    {
        Assert.Equal(expected, EnglishText.Of(HighlightText.TipChest("Reinforced chest", 6, Of(place, size))));
        var tip = HighlightText.Tooltip(MessageList.Comma("Copper"), new[] { HighlightText.TipChest("Reinforced chest", 6, Of(place, size)) }, Array.Empty<Message>())!;
        Assert.Equal(expected + " · holds", EnglishText.Of(tip).Text);
    }

    // X66: the marker's short text under a glowing chest.
    [Theory]
    [InlineData(1, 3, "1 of 3")]
    [InlineData(2, 3, "2 of 3")]
    [InlineData(3, 3, "3 of 3")]
    [InlineData(9, 3, "3 of 3")] // Never past the top.
    public void Marker_SaysThePlace_CountedFromTheBottom(int place, int size, string expected) =>
        Assert.Equal(expected, EnglishText.Of(ChestPlaceText.Marker(Of(place, size))!));

    [Fact]
    public void Marker_ForAChestAlone_SaysNothing()
    {
        Assert.Null(ChestPlaceText.Marker(Of(1, 1)));
        Assert.Null(ChestPlaceText.Marker(default));
        Assert.Null(ChestPlaceText.Stack(Of(1, 1)));
    }
}
