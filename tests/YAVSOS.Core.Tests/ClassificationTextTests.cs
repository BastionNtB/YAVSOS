using System;
using Xunit;
using YAVSOS.Core.Classification;

namespace YAVSOS.Core.Tests;

/// <summary>C01: the allocation-free scalar check refuses exactly what the strict UTF-8 encoder did, plus controls.</summary>
public sealed class ClassificationTextTests
{
    // Built in code: xUnit's data serialization would replace lone surrogates before the test sees them.
    [Fact]
    public void Text_RefusesMalformedUtf16ControlsAndBlank()
    {
        const char high = '\uD83D', low = '\uDE00';
        foreach (var value in new[] { "a" + high + "b", "a" + low + "b", "ab" + high, low.ToString() + high, "tab\there", "  ", "" })
            Assert.ThrowsAny<ArgumentException>(() => new FacetEvidence("mod", "pack", value, "Reason."));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("emoji 😀 pair")]
    [InlineData("Ünïcödé")]
    public void Text_AcceptsWellFormedText(string value) =>
        Assert.Equal(value, new FacetEvidence("mod", "pack", value, "Reason.").Source);

    [Fact]
    public void Text_BoundIsInCharacters()
    {
        Assert.Equal(256, new FacetEvidence("mod", "pack", new string('x', 256), "Reason.").Source.Length);
        Assert.ThrowsAny<ArgumentException>(() => new FacetEvidence("mod", "pack", new string('x', 257), "Reason."));
    }
}
