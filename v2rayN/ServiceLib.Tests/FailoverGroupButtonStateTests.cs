using ServiceLib.Models;
using ServiceLib.ViewModels;
using Xunit;

namespace ServiceLib.Tests;

public class FailoverGroupButtonStateTests
{
    [Fact]
    public void IsSelectedActiveFailoverGroup_ReturnsTrueOnlyForSelectedActiveFailoverGroup()
    {
        var selectedSub = new SubItem
        {
            Id = "failover-a",
            IsFailoverGroup = true,
        };

        var result = ProfilesViewModel.IsSelectedActiveFailoverGroup(selectedSub, "failover-a");

        Assert.True(result);
    }

    [Fact]
    public void IsSelectedActiveFailoverGroup_ReturnsFalseForNormalGroup()
    {
        var selectedSub = new SubItem
        {
            Id = "normal-a",
            IsFailoverGroup = false,
        };

        var result = ProfilesViewModel.IsSelectedActiveFailoverGroup(selectedSub, "normal-a");

        Assert.False(result);
    }

    [Fact]
    public void IsSelectedActiveFailoverGroup_ReturnsFalseForInactiveFailoverGroup()
    {
        var selectedSub = new SubItem
        {
            Id = "failover-a",
            IsFailoverGroup = true,
        };

        var result = ProfilesViewModel.IsSelectedActiveFailoverGroup(selectedSub, "failover-b");

        Assert.False(result);
    }
}
