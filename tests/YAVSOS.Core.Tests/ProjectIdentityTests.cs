using System;
using System.Diagnostics;
using Xunit;
using YAVSOS.Core;

namespace YAVSOS.Core.Tests;

public sealed class ProjectIdentityTests
{
    // B08: assembly and file versions come from ProjectIdentity.Version, the single source.
    [Fact]
    public void AssemblyAndFileVersionsMatchProjectIdentity()
    {
        var assembly = typeof(ProjectIdentity).Assembly;
        Assert.Equal(Version.Parse(ProjectIdentity.Version + ".0"), assembly.GetName().Version);
        Assert.Equal(ProjectIdentity.Version + ".0", FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion);
        Assert.True(Version.Parse(ProjectIdentity.MinimumManagedVersion) <= Version.Parse(ProjectIdentity.Version));
    }
}
