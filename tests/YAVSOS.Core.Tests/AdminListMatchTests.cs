using Xunit;
using YAVSOS.Core.Templates;

namespace YAVSOS.Core.Tests;

public sealed class AdminListMatchTests
{
    [Fact]
    public void Matches_ThePlatformFormAndTheBareSteamNumber_AsTheServerDoes()
    {
        var bare = new[] { "76561190000000001" };
        Assert.True(AdminListMatch.Contains(bare, "Steam_76561190000000001", "76561190000000001", steam: true));
        Assert.True(AdminListMatch.Contains(new[] { "Steam_76561190000000001" }, "Steam_76561190000000001", "76561190000000001", true));
        Assert.True(AdminListMatch.Contains(new[] { " 76561190000000001 " }, "Steam_76561190000000001", "76561190000000001", true));
    }

    [Fact]
    public void RefusesOtherIds_EmptyLists_AndBareIdsOffSteam()
    {
        Assert.False(AdminListMatch.Contains(new[] { "76561190000000002" }, "Steam_76561190000000001", "76561190000000001", true));
        Assert.False(AdminListMatch.Contains(new string[0], "Steam_1", "1", true));
        Assert.False(AdminListMatch.Contains(null, "Steam_1", "1", true));
        Assert.False(AdminListMatch.Contains(new[] { "123" }, "Xbox_123", "123", steam: false));
        Assert.True(AdminListMatch.Contains(new[] { "Xbox_123" }, "Xbox_123", "123", steam: false));
    }
}
