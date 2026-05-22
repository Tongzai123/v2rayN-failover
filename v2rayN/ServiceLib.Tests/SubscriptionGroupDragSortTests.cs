using ServiceLib.Handler;
using ServiceLib.Models;
using Xunit;

namespace ServiceLib.Tests;

public class SubscriptionGroupDragSortTests
{
    [Fact]
    public void GetItemOffset_SourceBeforeTarget_ShiftsIntermediateGroupsLeft()
    {
        var itemWidth = 96d;

        Assert.Equal(0, SubscriptionGroupDragDropAnimation.GetItemOffset(0, 1, 3, itemWidth));
        Assert.Equal(0, SubscriptionGroupDragDropAnimation.GetItemOffset(1, 1, 3, itemWidth));
        Assert.Equal(-itemWidth, SubscriptionGroupDragDropAnimation.GetItemOffset(2, 1, 3, itemWidth));
        Assert.Equal(-itemWidth, SubscriptionGroupDragDropAnimation.GetItemOffset(3, 1, 3, itemWidth));
        Assert.Equal(0, SubscriptionGroupDragDropAnimation.GetItemOffset(4, 1, 3, itemWidth));
    }

    [Fact]
    public void GetItemOffset_SourceAfterTarget_ShiftsIntermediateGroupsRight()
    {
        var itemWidth = 96d;

        Assert.Equal(0, SubscriptionGroupDragDropAnimation.GetItemOffset(0, 3, 1, itemWidth));
        Assert.Equal(itemWidth, SubscriptionGroupDragDropAnimation.GetItemOffset(1, 3, 1, itemWidth));
        Assert.Equal(itemWidth, SubscriptionGroupDragDropAnimation.GetItemOffset(2, 3, 1, itemWidth));
        Assert.Equal(0, SubscriptionGroupDragDropAnimation.GetItemOffset(3, 3, 1, itemWidth));
        Assert.Equal(0, SubscriptionGroupDragDropAnimation.GetItemOffset(4, 3, 1, itemWidth));
    }

    [Fact]
    public void ReorderSubItemsForMove_MovesSourceBeforeTargetAndNormalizesSort()
    {
        var items = new List<SubItem>
        {
            new() { Id = "a", Remarks = "A", Sort = 10 },
            new() { Id = "b", Remarks = "B", Sort = 20 },
            new() { Id = "c", Remarks = "C", Sort = 30 },
        };

        var result = ConfigHandler.ReorderSubItemsForMove(items, "c", "a");

        Assert.Collection(result,
            item =>
            {
                Assert.Equal("c", item.Id);
                Assert.Equal(1, item.Sort);
            },
            item =>
            {
                Assert.Equal("a", item.Id);
                Assert.Equal(2, item.Sort);
            },
            item =>
            {
                Assert.Equal("b", item.Id);
                Assert.Equal(3, item.Sort);
            });
    }

    [Fact]
    public void ReorderSubItemsForMove_IgnoresVirtualAllGroupWithoutId()
    {
        var items = new List<SubItem>
        {
            new() { Remarks = "All", Sort = 0 },
            new() { Id = "a", Remarks = "A", Sort = 1 },
            new() { Id = "b", Remarks = "B", Sort = 2 },
        };

        var result = ConfigHandler.ReorderSubItemsForMove(items, "", "a");

        Assert.Collection(result,
            item => Assert.Equal("a", item.Id),
            item => Assert.Equal("b", item.Id));
    }

    [Fact]
    public void ReorderSubItemsForMove_ReturnsOriginalOrderWhenSourceEqualsTarget()
    {
        var items = new List<SubItem>
        {
            new() { Id = "a", Remarks = "A", Sort = 1 },
            new() { Id = "b", Remarks = "B", Sort = 2 },
        };

        var result = ConfigHandler.ReorderSubItemsForMove(items, "a", "a");

        Assert.Equal(["a", "b"], result.Select(item => item.Id).ToArray());
        Assert.Equal([1, 2], result.Select(item => item.Sort).ToArray());
    }

    [Fact]
    public void ReorderSubItemsForOrder_UsesPreviewOrderAndAppendsMissingItems()
    {
        var items = new List<SubItem>
        {
            new() { Id = "a", Remarks = "A", Sort = 10 },
            new() { Id = "b", Remarks = "B", Sort = 20 },
            new() { Id = "c", Remarks = "C", Sort = 30 },
        };

        var result = ConfigHandler.ReorderSubItemsForOrder(items, ["c", "a"]);

        Assert.Equal(["c", "a", "b"], result.Select(item => item.Id).ToArray());
        Assert.Equal([1, 2, 3], result.Select(item => item.Sort).ToArray());
    }

    [Fact]
    public void ReorderSubItemsForOrder_IgnoresVirtualAndUnknownIds()
    {
        var items = new List<SubItem>
        {
            new() { Remarks = "All", Sort = 0 },
            new() { Id = "a", Remarks = "A", Sort = 1 },
            new() { Id = "b", Remarks = "B", Sort = 2 },
        };

        var result = ConfigHandler.ReorderSubItemsForOrder(items, ["missing", "", "b"]);

        Assert.Equal(["b", "a"], result.Select(item => item.Id).ToArray());
        Assert.Equal([1, 2], result.Select(item => item.Sort).ToArray());
    }
}
