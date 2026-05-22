using ServiceLib.Enums;
using ServiceLib.Manager;
using ServiceLib.Models;
using ServiceLib.Resx;
using ServiceLib.ViewModels;
using Xunit;

namespace ServiceLib.Tests;

public class FailoverHealthDisplayTests
{
    [Theory]
    [InlineData(FailoverHealthStatus.Normal)]
    [InlineData(FailoverHealthStatus.Failed)]
    [InlineData(FailoverHealthStatus.Probing)]
    [InlineData(FailoverHealthStatus.Requesting)]
    [InlineData(FailoverHealthStatus.Degraded)]
    [InlineData(FailoverHealthStatus.Fallback)]
    [InlineData(FailoverHealthStatus.Unknown)]
    [InlineData("unexpected")]
    public void ApplyFailoverHealthDisplay_SetsExactlyOneVisibleStatus(string status)
    {
        var item = new ProfileItemModel
        {
            ShowFailoverHealthLabel = true,
            FailoverHealthStatus = status,
        };

        ProfilesViewModel.ApplyFailoverHealthDisplay(item);

        var expectedStatus = status is FailoverHealthStatus.Normal
            or FailoverHealthStatus.Failed
            or FailoverHealthStatus.Probing
            or FailoverHealthStatus.Requesting
            or FailoverHealthStatus.Degraded
            or FailoverHealthStatus.Fallback
            or FailoverHealthStatus.Unknown
            ? status
            : FailoverHealthStatus.Unknown;
        Assert.Equal(expectedStatus, item.FailoverHealthStatus);
        Assert.Equal(ExpectedLabel(expectedStatus), item.FailoverHealthLabel);
        Assert.Single(new[]
        {
            item.IsFailoverHealthNormal,
            item.IsFailoverHealthFailed,
            item.IsFailoverHealthProbing,
            item.IsFailoverHealthRequesting,
            item.IsFailoverHealthDegraded,
            item.IsFailoverHealthFallback,
            item.IsFailoverHealthUnknown,
        }, visible => visible);
    }

    [Fact]
    public void ApplyFailoverHealthDisplay_HidesStatusWhenItemIsNotInQueue()
    {
        var item = new ProfileItemModel
        {
            ShowFailoverHealthLabel = false,
            FailoverHealthStatus = FailoverHealthStatus.Normal,
        };

        ProfilesViewModel.ApplyFailoverHealthDisplay(item);

        Assert.False(item.IsFailoverHealthNormal);
        Assert.False(item.IsFailoverHealthFailed);
        Assert.False(item.IsFailoverHealthProbing);
        Assert.False(item.IsFailoverHealthRequesting);
        Assert.False(item.IsFailoverHealthDegraded);
        Assert.False(item.IsFailoverHealthFallback);
        Assert.False(item.IsFailoverHealthUnknown);
    }

    [Fact]
    public void ApplyFailoverHealthDisplay_CurrentPreferredNormalNodeShowsActiveInsteadOfNormal()
    {
        var item = new ProfileItemModel
        {
            ShowFailoverHealthLabel = true,
            IsCurrentFailoverPreferred = true,
            FailoverHealthStatus = FailoverHealthStatus.Normal,
        };

        ProfilesViewModel.ApplyFailoverHealthDisplay(item);

        Assert.True(item.ShowCurrentFailoverActiveLabel);
        Assert.False(item.IsFailoverHealthNormal);
        Assert.False(item.IsFailoverHealthFailed);
        Assert.False(item.IsFailoverHealthProbing);
        Assert.False(item.IsFailoverHealthRequesting);
        Assert.False(item.IsFailoverHealthDegraded);
        Assert.False(item.IsFailoverHealthFallback);
        Assert.False(item.IsFailoverHealthUnknown);
    }

    [Fact]
    public void ApplyFailoverHealthDisplay_CurrentPreferredFailedNodeShowsFailedInsteadOfActive()
    {
        var item = new ProfileItemModel
        {
            ShowFailoverHealthLabel = true,
            IsCurrentFailoverPreferred = true,
            FailoverHealthStatus = FailoverHealthStatus.Failed,
        };

        ProfilesViewModel.ApplyFailoverHealthDisplay(item);

        Assert.False(item.ShowCurrentFailoverActiveLabel);
        Assert.False(item.IsFailoverHealthNormal);
        Assert.True(item.IsFailoverHealthFailed);
        Assert.False(item.IsFailoverHealthProbing);
        Assert.False(item.IsFailoverHealthRequesting);
        Assert.False(item.IsFailoverHealthDegraded);
        Assert.False(item.IsFailoverHealthFallback);
        Assert.False(item.IsFailoverHealthUnknown);
    }

    [Theory]
    [InlineData(FailoverHealthStatus.Probing)]
    [InlineData(FailoverHealthStatus.Fallback)]
    [InlineData(FailoverHealthStatus.Unknown)]
    public void ApplyFailoverHealthDisplay_CurrentPreferredNodeShowsActiveBeforeProbeCompletes(string status)
    {
        var item = new ProfileItemModel
        {
            ShowFailoverHealthLabel = true,
            IsCurrentFailoverPreferred = true,
            FailoverHealthStatus = status,
        };

        ProfilesViewModel.ApplyFailoverHealthDisplay(item);

        Assert.True(item.ShowCurrentFailoverActiveLabel);
        Assert.False(item.IsFailoverHealthNormal);
        Assert.False(item.IsFailoverHealthFailed);
        Assert.False(item.IsFailoverHealthProbing);
        Assert.False(item.IsFailoverHealthRequesting);
        Assert.False(item.IsFailoverHealthDegraded);
        Assert.False(item.IsFailoverHealthFallback);
        Assert.False(item.IsFailoverHealthUnknown);
    }

    [Theory]
    [InlineData(FailoverHealthStatus.Requesting)]
    [InlineData(FailoverHealthStatus.Degraded)]
    public void ApplyFailoverHealthDisplay_CurrentPreferredUncertainNodeShowsHealthTagInsteadOfActive(string status)
    {
        var item = new ProfileItemModel
        {
            ShowFailoverHealthLabel = true,
            IsCurrentFailoverPreferred = true,
            FailoverHealthStatus = status,
        };

        ProfilesViewModel.ApplyFailoverHealthDisplay(item);

        Assert.False(item.ShowCurrentFailoverActiveLabel);
        Assert.Equal(status == FailoverHealthStatus.Requesting, item.IsFailoverHealthRequesting);
        Assert.Equal(status == FailoverHealthStatus.Degraded, item.IsFailoverHealthDegraded);
    }

    [Fact]
    public void ApplyFailoverHealthDisplay_DoesNotShowActiveWhenRuntimeTargetDoesNotMatch()
    {
        var item = new ProfileItemModel
        {
            ShowFailoverHealthLabel = true,
            IsCurrentFailoverPreferred = false,
            FailoverHealthStatus = FailoverHealthStatus.Normal,
        };

        ProfilesViewModel.ApplyFailoverHealthDisplay(item);

        Assert.False(item.ShowCurrentFailoverActiveLabel);
        Assert.True(item.IsFailoverHealthNormal);
    }

    [Fact]
    public void ResolveFailoverDisplayActiveProfileId_FallsBackToFirstNormalNodeWhenCurrentTargetFailed()
    {
        var entries = new[]
        {
            CreateFailoverQueueEntry("p1", 1, FailoverHealthStatus.Failed),
            CreateFailoverQueueEntry("p2", 2, FailoverHealthStatus.Normal),
            CreateFailoverQueueEntry("p3", 3, FailoverHealthStatus.Normal),
        };

        var result = ProfilesViewModel.ResolveFailoverDisplayActiveProfileId("p1", entries);

        Assert.Equal("p2", result);
    }

    [Fact]
    public void ResolveFailoverDisplayActiveProfileId_KeepsCurrentTargetWhenItIsNotFailed()
    {
        var entries = new[]
        {
            CreateFailoverQueueEntry("p1", 1, FailoverHealthStatus.Normal),
            CreateFailoverQueueEntry("p2", 2, FailoverHealthStatus.Normal),
        };

        var result = ProfilesViewModel.ResolveFailoverDisplayActiveProfileId("p1", entries);

        Assert.Equal("p1", result);
    }

    [Theory]
    [InlineData(EFailoverMode.Off, true, false)]
    [InlineData(EFailoverMode.Failover, true, true)]
    [InlineData(EFailoverMode.LeastDelay, true, true)]
    [InlineData(EFailoverMode.Failover, false, false)]
    public void ShouldShowPendingActiveLabel_ShowsOnlyOriginalNormalActiveNodeDuringTransferMode(
        EFailoverMode mode,
        bool isOriginalActive,
        bool expected)
    {
        var result = ProfilesViewModel.ShouldShowPendingActiveLabel(
            isFailoverGroup: false,
            isOriginalActive,
            mode);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ShouldShowPendingActiveLabel_HidesForFailoverGroupNodes()
    {
        var result = ProfilesViewModel.ShouldShowPendingActiveLabel(
            isFailoverGroup: true,
            isOriginalActive: true,
            EFailoverMode.Failover);

        Assert.False(result);
    }

    [Theory]
    [InlineData(EFailoverMode.Off, EFailoverMode.Failover, true)]
    [InlineData(EFailoverMode.Off, EFailoverMode.LeastDelay, true)]
    [InlineData(EFailoverMode.Failover, EFailoverMode.Off, true)]
    [InlineData(EFailoverMode.LeastDelay, EFailoverMode.Off, true)]
    [InlineData(EFailoverMode.Failover, EFailoverMode.LeastDelay, false)]
    [InlineData(EFailoverMode.LeastDelay, EFailoverMode.Failover, false)]
    [InlineData(EFailoverMode.Off, EFailoverMode.Off, false)]
    public void ShouldRefreshNormalGroupForPendingActiveStateChange_RefreshesOnlyWhenPendingActiveVisibilityChanges(
        EFailoverMode oldMode,
        EFailoverMode newMode,
        bool expected)
    {
        var result = ProfilesViewModel.ShouldRefreshNormalGroupForPendingActiveStateChange(oldMode, newMode);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(EFailoverMode.Failover)]
    [InlineData(EFailoverMode.LeastDelay)]
    public void GetFailoverDisplayTargetProfileId_UsesStartupTargetBeforeRuntimeTarget(EFailoverMode mode)
    {
        var config = new Config
        {
            ActiveFailoverGroupId = "group-a",
            FailoverMode = mode,
            FailoverStartupProfileId = "startup-node",
        };

        var result = ProfilesViewModel.GetFailoverDisplayTargetProfileId(config, "runtime-node");

        Assert.Equal("startup-node", result);
    }

    [Fact]
    public void ApplyFailoverSpeedTestDisplay_ProbingUsesSpeedtestingDelayText()
    {
        var item = new ProfileItemModel
        {
            ShowFailoverHealthLabel = true,
            FailoverHealthStatus = FailoverHealthStatus.Probing,
            Delay = 91,
            DelayVal = "91",
        };

        ProfilesViewModel.ApplyFailoverSpeedTestDisplay(item);

        Assert.Equal(0, item.Delay);
        Assert.Equal(ResUI.Speedtesting, item.DelayVal);
    }

    [Fact]
    public void ApplyFailoverSpeedTestDisplay_NormalKeepsExistingDelayText()
    {
        var item = new ProfileItemModel
        {
            ShowFailoverHealthLabel = true,
            FailoverHealthStatus = FailoverHealthStatus.Normal,
            Delay = 91,
            DelayVal = "91",
        };

        ProfilesViewModel.ApplyFailoverSpeedTestDisplay(item);

        Assert.Equal(91, item.Delay);
        Assert.Equal("91", item.DelayVal);
    }

    [Fact]
    public void ApplyFailoverCompletedDelayDisplay_SuccessDelayChangesProbingToNormal()
    {
        var item = new ProfileItemModel
        {
            ShowFailoverHealthLabel = true,
            FailoverHealthStatus = FailoverHealthStatus.Probing,
            Delay = 91,
            DelayVal = "91",
        };

        ProfilesViewModel.ApplyFailoverCompletedDelayDisplay(item);

        Assert.Equal(FailoverHealthStatus.Normal, item.FailoverHealthStatus);
        Assert.True(item.IsFailoverHealthNormal);
        Assert.False(item.IsFailoverHealthProbing);
        Assert.Equal(ResUI.TbFailoverHealthNormal, item.FailoverHealthLabel);
    }

    [Fact]
    public void ApplyFailoverCompletedDelayDisplay_FailedDelayKeepsProbingUntilRefresh()
    {
        var item = new ProfileItemModel
        {
            ShowFailoverHealthLabel = true,
            FailoverHealthStatus = FailoverHealthStatus.Probing,
            Delay = -1,
            DelayVal = "-1",
        };

        ProfilesViewModel.ApplyFailoverCompletedDelayDisplay(item);

        Assert.Equal(FailoverHealthStatus.Probing, item.FailoverHealthStatus);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void GetFailoverPriorityDisplayState_HidesPriorityOnlyForActiveLeastDelay(bool isActiveLeastDelay, bool expectedVisible)
    {
        Assert.Equal(expectedVisible, ProfilesViewModel.ShouldShowFailoverPriorityLabel(isInQueue: true, isActiveLeastDelay));
    }

    [Fact]
    public void GetFailoverPriorityDisplayState_HidesPriorityForNonQueueItem()
    {
        Assert.False(ProfilesViewModel.ShouldShowFailoverPriorityLabel(isInQueue: false, isActiveLeastDelay: false));
    }

    [Fact]
    public void OrderFailoverProfileModelsForDisplay_ActiveLeastDelayKeepsActiveFirstThenRuntimeOrder()
    {
        var active = new ProfileItemModel
        {
            IndexId = "active",
            IsInFailoverQueue = true,
            FailoverPriority = 2,
            Sort = 20,
        };
        var best = new ProfileItemModel
        {
            IndexId = "best",
            IsInFailoverQueue = true,
            FailoverPriority = 1,
            Sort = 10,
        };
        var fallback = new ProfileItemModel
        {
            IndexId = "fallback",
            IsInFailoverQueue = true,
            FailoverPriority = 3,
            Sort = 30,
        };
        var candidate = new ProfileItemModel
        {
            IndexId = "candidate",
            IsInFailoverQueue = false,
            Sort = 1,
        };

        var ordered = ProfilesViewModel.OrderFailoverProfileModelsForDisplay(
            [candidate, fallback, active, best],
            isFailoverGroup: true,
            isActiveFailoverGroup: true,
            activeFailoverTargetProfileId: "active",
            pinFailoverQueue: false);

        Assert.Equal(["active", "best", "fallback", "candidate"], ordered.Select(item => item.IndexId).ToArray());
    }

    private static string ExpectedLabel(string status)
    {
        return status switch
        {
            FailoverHealthStatus.Normal => ResUI.TbFailoverHealthNormal,
            FailoverHealthStatus.Failed => ResUI.TbFailoverHealthFailed,
            FailoverHealthStatus.Probing => ResUI.TbFailoverHealthProbing,
            FailoverHealthStatus.Requesting => ResUI.TbFailoverHealthRequesting,
            FailoverHealthStatus.Degraded => ResUI.TbFailoverHealthDegraded,
            FailoverHealthStatus.Fallback => ResUI.TbFailoverHealthFallback,
            _ => ResUI.TbFailoverHealthUnknown,
        };
    }

    private static FailoverQueueEntry CreateFailoverQueueEntry(string profileId, int sort, string status)
        => new(
            new FailoverGroupItem
            {
                Id = $"queue-{profileId}",
                SourceProfileId = profileId,
                Sort = sort,
                Enabled = true,
                LastStatus = status,
            },
            new ProfileItem
            {
                IndexId = profileId,
            });
}
