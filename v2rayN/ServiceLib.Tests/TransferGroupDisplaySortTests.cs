using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Models;
using ServiceLib.ViewModels;
using Xunit;

namespace ServiceLib.Tests;

public class TransferGroupDisplaySortTests
{
    [Fact]
    public void SortProfileDisplayItemsForHeader_DelayAscendingKeepsMissingDelayAtEnd()
    {
        var items = new[]
        {
            CreateItem("missing", "missing", delay: 0),
            CreateItem("slow", "slow", delay: 90),
            CreateItem("fast", "fast", delay: 20),
        };

        var ordered = ConfigHandler.SortProfileDisplayItemsForHeader(items, "DelayVal", asc: true);

        Assert.Equal(["fast", "slow", "missing"], ordered.Select(item => item.IndexId).ToArray());
    }

    [Fact]
    public void SortProfileDisplayItemsForHeader_SpeedAscendingKeepsMissingSpeedAtEnd()
    {
        var items = new[]
        {
            CreateItem("missing", "missing", speed: 0),
            CreateItem("slow", "slow", speed: 10),
            CreateItem("fast", "fast", speed: 100),
        };

        var ordered = ConfigHandler.SortProfileDisplayItemsForHeader(items, "SpeedVal", asc: true);

        Assert.Equal(["slow", "fast", "missing"], ordered.Select(item => item.IndexId).ToArray());
    }

    [Fact]
    public void SortProfileDisplayItemsForHeader_TrafficColumnsUseComparableNumericText()
    {
        var items = new[]
        {
            CreateItem("large", "large", todayDownValue: 100),
            CreateItem("small", "small", todayDownValue: 2),
            CreateItem("none", "none", todayDownValue: 0),
        };

        var ordered = ConfigHandler.SortProfileDisplayItemsForHeader(items, "TodayDown", asc: true);

        Assert.Equal(["none", "small", "large"], ordered.Select(item => item.IndexId).ToArray());
    }

    [Fact]
    public void SortProfileDisplayItemsForHeader_UnsupportedColumnKeepsOriginalOrder()
    {
        var items = new[]
        {
            CreateItem("z", "zeta"),
            CreateItem("a", "alpha"),
            CreateItem("m", "middle"),
        };

        var ordered = ConfigHandler.SortProfileDisplayItemsForHeader(items, "NotAColumn", asc: true);

        Assert.Equal(["z", "a", "m"], ordered.Select(item => item.IndexId).ToArray());
    }

    [Fact]
    public void ApplyTransferGroupDisplaySort_OffModeSortsWholeListByColumn()
    {
        var items = new[]
        {
            CreateItem("queue-z", "zeta", isInQueue: true, failoverPriority: 1),
            CreateItem("candidate-a", "alpha", isInQueue: false),
            CreateItem("candidate-m", "middle", isInQueue: false),
        };

        var ordered = ProfilesViewModel.ApplyTransferGroupDisplaySort(
            items,
            pinQueueItems: false,
            colName: "Remarks",
            asc: true);

        Assert.Equal(["candidate-a", "candidate-m", "queue-z"], ordered.Select(item => item.IndexId).ToArray());
    }

    [Fact]
    public void ApplyTransferGroupDisplaySort_OnModeSortsQueueAndCandidatesButKeepsQueueFirst()
    {
        var items = new[]
        {
            CreateItem("queue-p2", "bravo", isInQueue: true, failoverPriority: 2),
            CreateItem("candidate-z", "zulu", isInQueue: false),
            CreateItem("queue-p1", "charlie", isInQueue: true, failoverPriority: 1),
            CreateItem("candidate-a", "alpha", isInQueue: false),
        };

        var ordered = ProfilesViewModel.ApplyTransferGroupDisplaySort(
            items,
            pinQueueItems: true,
            colName: "Remarks",
            asc: true);

        Assert.Equal(["queue-p2", "queue-p1", "candidate-a", "candidate-z"], ordered.Select(item => item.IndexId).ToArray());
    }

    [Fact]
    public void OrderFailoverProfileModelsForDisplay_OffModeUsesProfileSortWithoutPinningQueue()
    {
        var items = new[]
        {
            WithSort(CreateItem("queue", "queue", isInQueue: true, failoverPriority: 1), 30),
            WithSort(CreateItem("candidate-a", "alpha", isInQueue: false), 10),
            WithSort(CreateItem("candidate-b", "bravo", isInQueue: false), 20),
        };

        var ordered = ProfilesViewModel.OrderFailoverProfileModelsForDisplay(
            items,
            isFailoverGroup: true,
            isActiveFailoverGroup: true,
            activeFailoverTargetProfileId: null,
            pinFailoverQueue: false);

        Assert.Equal(["candidate-a", "candidate-b", "queue"], ordered.Select(item => item.IndexId).ToArray());

        static ProfileItemModel WithSort(ProfileItemModel item, int sort)
        {
            item.Sort = sort;
            return item;
        }
    }

    [Fact]
    public void OrderFailoverProfileModelsForDisplay_PriorityRowsIgnoreActiveTarget()
    {
        var items = new[]
        {
            WithSort(CreateItem("p1", "p1", isInQueue: true, failoverPriority: 1), 10),
            WithSort(CreateItem("p2", "p2", isInQueue: true, failoverPriority: 2), 20),
            WithSort(CreateItem("p5-active", "p5", isInQueue: true, failoverPriority: 5), 50),
            WithSort(CreateItem("candidate", "candidate", isInQueue: false), 5),
        };

        var ordered = ProfilesViewModel.OrderFailoverProfileModelsForDisplay(
            items,
            isFailoverGroup: true,
            isActiveFailoverGroup: true,
            activeFailoverTargetProfileId: "p5-active",
            pinFailoverQueue: true);

        Assert.Equal(["p1", "p2", "p5-active", "candidate"], ordered.Select(item => item.IndexId).ToArray());

        static ProfileItemModel WithSort(ProfileItemModel item, int sort)
        {
            item.Sort = sort;
            return item;
        }
    }

    [Theory]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    public void CanMoveTransferGroupItem_EnforcesQueueBoundaryOnlyWhenQueuePinned(
        bool pinFailoverQueue,
        bool sourceInQueue,
        bool targetInQueue,
        bool expected)
    {
        Assert.Equal(expected, ProfilesViewModel.CanMoveTransferGroupItem(
            pinFailoverQueue,
            sourceInQueue,
            targetInQueue));
    }

    [Fact]
    public void ShouldPromoteFailoverQueueFirstAfterDrag_OnlyForActiveFailoverMode()
    {
        var config = new Config
        {
            ActiveFailoverGroupId = "group",
            FailoverMode = EFailoverMode.Failover,
        };

        Assert.True(ProfilesViewModel.ShouldPromoteFailoverQueueFirstAfterDrag(config, "group", "first"));

        config.FailoverMode = EFailoverMode.LeastDelay;
        Assert.False(ProfilesViewModel.ShouldPromoteFailoverQueueFirstAfterDrag(config, "group", "first"));

        config.FailoverMode = EFailoverMode.Failover;
        Assert.False(ProfilesViewModel.ShouldPromoteFailoverQueueFirstAfterDrag(config, "other", "first"));
        Assert.False(ProfilesViewModel.ShouldPromoteFailoverQueueFirstAfterDrag(config, "group", null));
    }

    [Fact]
    public void ShouldRefreshTransferGroupForStateChange_RefreshesWhenSwitchingOnCurrentActiveGroup()
    {
        Assert.True(ProfilesViewModel.ShouldRefreshTransferGroupForStateChange(
            EFailoverMode.Off,
            EFailoverMode.Failover,
            oldActiveGroupId: "group-a",
            newActiveGroupId: "group-a",
            currentGroupId: "group-a"));
    }

    [Fact]
    public void ShouldRefreshTransferGroupForStateChange_RefreshesWhenCurrentGroupBecomesActive()
    {
        Assert.True(ProfilesViewModel.ShouldRefreshTransferGroupForStateChange(
            EFailoverMode.Failover,
            EFailoverMode.Failover,
            oldActiveGroupId: "group-b",
            newActiveGroupId: "group-a",
            currentGroupId: "group-a"));
    }

    [Fact]
    public void ShouldRefreshTransferGroupForStateChange_DoesNotRefreshWhenSwitchingOff()
    {
        Assert.False(ProfilesViewModel.ShouldRefreshTransferGroupForStateChange(
            EFailoverMode.Failover,
            EFailoverMode.Off,
            oldActiveGroupId: "group-a",
            newActiveGroupId: "group-a",
            currentGroupId: "group-a"));
    }

    [Fact]
    public void ShouldDeactivateTransferGroupSortForStateChange_DeactivatesWhenSwitchingOff()
    {
        Assert.True(ProfilesViewModel.ShouldDeactivateTransferGroupSortForStateChange(
            EFailoverMode.LeastDelay,
            EFailoverMode.Off,
            currentGroupId: "group-a"));
    }

    [Fact]
    public void ApplyTransferGroupDisplaySort_DoesNotModifyQueueFieldsOrSortFields()
    {
        var queue = CreateItem("queue", "zeta", isInQueue: true, failoverPriority: 7);
        var candidate = CreateItem("candidate", "alpha", isInQueue: false);
        var originalQueueSort = queue.Sort;
        var originalCandidateSort = candidate.Sort;

        _ = ProfilesViewModel.ApplyTransferGroupDisplaySort(
            [candidate, queue],
            pinQueueItems: true,
            colName: "Remarks",
            asc: true);

        Assert.Equal(7, queue.FailoverPriority);
        Assert.True(queue.IsInFailoverQueue);
        Assert.Equal(originalQueueSort, queue.Sort);
        Assert.Equal(originalCandidateSort, candidate.Sort);
    }

    [Fact]
    public void ShouldPinTransferGroupPriorityQueue_PinsForHeaderSortOrEnabledMode()
    {
        Assert.True(ProfilesViewModel.ShouldPinTransferGroupPriorityQueue(
            isFailoverGroup: true,
            isActiveFailoverGroup: true,
            failoverMode: EFailoverMode.Off,
            isActiveLeastDelay: false,
            sortActive: true));
        Assert.True(ProfilesViewModel.ShouldPinTransferGroupPriorityQueue(
            isFailoverGroup: true,
            isActiveFailoverGroup: true,
            failoverMode: EFailoverMode.Failover,
            isActiveLeastDelay: false,
            sortActive: false));
        Assert.False(ProfilesViewModel.ShouldPinTransferGroupPriorityQueue(
            isFailoverGroup: true,
            isActiveFailoverGroup: true,
            failoverMode: EFailoverMode.Off,
            isActiveLeastDelay: false,
            sortActive: false));
        Assert.False(ProfilesViewModel.ShouldPinTransferGroupPriorityQueue(
            isFailoverGroup: true,
            isActiveFailoverGroup: true,
            failoverMode: EFailoverMode.LeastDelay,
            isActiveLeastDelay: true,
            sortActive: true));
        Assert.False(ProfilesViewModel.ShouldPinTransferGroupPriorityQueue(
            isFailoverGroup: false,
            isActiveFailoverGroup: false,
            failoverMode: EFailoverMode.Failover,
            isActiveLeastDelay: false,
            sortActive: true));
    }

    [Fact]
    public void ShouldRestrictTransferGroupDragBoundary_OnlyWhenCurrentActiveGroupIsEnabled()
    {
        var config = new Config
        {
            ActiveFailoverGroupId = "group",
            FailoverMode = EFailoverMode.Failover,
        };

        Assert.True(ProfilesViewModel.ShouldRestrictTransferGroupDragBoundary(config, "group", currentGroupIsFailoverGroup: true));

        config.FailoverMode = EFailoverMode.Off;
        Assert.False(ProfilesViewModel.ShouldRestrictTransferGroupDragBoundary(config, "group", currentGroupIsFailoverGroup: true));

        config.FailoverMode = EFailoverMode.Failover;
        Assert.False(ProfilesViewModel.ShouldRestrictTransferGroupDragBoundary(config, "other", currentGroupIsFailoverGroup: true));
        Assert.False(ProfilesViewModel.ShouldRestrictTransferGroupDragBoundary(config, "group", currentGroupIsFailoverGroup: false));
    }

    [Fact]
    public void GetQueueOrderForAddToFailoverQueue_UsesCurrentVisualOrder()
    {
        var items = new[]
        {
            CreateItem("queue-p1", "alpha", isInQueue: true, failoverPriority: 1),
            CreateItem("candidate", "bravo", isInQueue: false),
            CreateItem("queue-p2", "charlie", isInQueue: true, failoverPriority: 2),
            CreateItem("other", "delta", isInQueue: false),
        };

        var ordered = ProfilesViewModel.GetQueueOrderForAddToFailoverQueue(items, ["candidate"]);

        Assert.Equal(["queue-p1", "candidate", "queue-p2"], ordered);
    }

    private static ProfileItemModel CreateItem(
        string indexId,
        string remarks,
        int delay = 0,
        decimal speed = 0,
        long todayDownValue = 0,
        bool isInQueue = false,
        int failoverPriority = 0)
    {
        return new ProfileItemModel
        {
            IndexId = indexId,
            Remarks = remarks,
            Address = $"198.51.100.{Math.Abs(indexId.GetHashCode()) % 200 + 1}",
            Port = indexId.Length,
            Network = "tcp",
            StreamSecurity = string.Empty,
            Subid = "sub-a",
            SubRemarks = "sub-a",
            Sort = indexId.Length * 10,
            Delay = delay,
            Speed = speed,
            TodayDown = $"{todayDownValue} B",
            TodayUp = "0000000000000000",
            TotalDown = "0000000000000000",
            TotalUp = "0000000000000000",
            TodayDownValue = todayDownValue,
            IsInFailoverQueue = isInQueue,
            FailoverPriority = failoverPriority,
        };
    }
}
