using System.Linq;
using Xunit;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Core.Tests;

public sealed class MultiUserChestCopiesTests
{
    [Theory]
    [InlineData(new string[0], MultiUserChestCopy.Yavsos)]
    [InlineData(new[] { "Azumatt.AzuAutoStore" }, MultiUserChestCopy.AzuAutoStore)]
    [InlineData(new[] { "com.maxsch.valheim.MultiUserChest" }, MultiUserChestCopy.Standalone)]
    [InlineData(new[] { "com.maxsch.valheim.MultiUserChest", "Azumatt.AzuAutoStore" }, MultiUserChestCopy.Standalone)]
    [InlineData(new[] { "aedenthorn.SimpleSort" }, MultiUserChestCopy.None)]
    [InlineData(new[] { "aedenthorn.QuickStore", "Azumatt.AzuAutoStore" }, MultiUserChestCopy.None)]
    [InlineData(new[] { "org.bepinex.plugins.valheim.quick_stack" }, MultiUserChestCopy.None)]
    [InlineData(new[] { "mkz.itemdrawers", "Azumatt.AzuExtendedPlayerInventory" }, MultiUserChestCopy.Yavsos)]
    public void Choose_FollowsTheStepAsideOrder(string[] loaded, MultiUserChestCopy expected) =>
        Assert.Equal(expected, MultiUserChestCopies.Choose(loaded.Contains));
}
