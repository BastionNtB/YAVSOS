using System;
using Xunit;
using YAVSOS.Core.Classification;

namespace YAVSOS.Core.Tests;

public sealed class LoadedSourceSemanticsTests
{
    [Fact]
    public void CloneIdentityFallback_RequiresCompleteSuffixAndPreservesRealSpacesAndParentheses()
    {
        Assert.Null(LoadedSourceSemantics.CloneBaseName("Fine Wood"));
        Assert.Null(LoadedSourceSemantics.CloneBaseName("Wood(Ancient)"));
        Assert.Null(LoadedSourceSemantics.CloneBaseName("Wood(Clone)Other"));
        Assert.Null(LoadedSourceSemantics.CloneBaseName("(Clone)"));
        Assert.Equal("Fine Wood", LoadedSourceSemantics.CloneBaseName("Fine Wood(Clone)"));
        Assert.Equal("Wood(Ancient)", LoadedSourceSemantics.CloneBaseName("Wood(Ancient)(Clone)"));
    }
}
