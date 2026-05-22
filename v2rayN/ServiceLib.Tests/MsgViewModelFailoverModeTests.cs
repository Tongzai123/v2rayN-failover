using ServiceLib.Enums;
using ServiceLib.Models;
using ServiceLib.ViewModels;
using Xunit;

namespace ServiceLib.Tests;

public class MsgViewModelFailoverModeTests
{
    [Theory]
    [InlineData(EFailoverMode.Off, true, false, false)]
    [InlineData(EFailoverMode.Failover, false, true, false)]
    [InlineData(EFailoverMode.LeastDelay, true, false, false)]
    public void ApplyFailoverModeDisplay_SetsExactlyOneModeActive(
        EFailoverMode mode,
        bool off,
        bool failover,
        bool leastDelay)
    {
        var state = MsgViewModel.GetFailoverModeDisplayState(mode);

        Assert.Equal(off, state.IsOff);
        Assert.Equal(failover, state.IsFailover);
        Assert.Equal(leastDelay, state.IsLeastDelay);
        Assert.Single(new[] { state.IsOff, state.IsFailover, state.IsLeastDelay }, x => x);
    }

    [Theory]
    [InlineData(false, EFailoverMode.Off, false)]
    [InlineData(true, EFailoverMode.Off, false)]
    [InlineData(true, EFailoverMode.Failover, true)]
    [InlineData(true, EFailoverMode.LeastDelay, false)]
    public void GetShowActiveFailoverGroupTag_ShowsOnlyWhenActiveGroupAndFailoverEnabled(
        bool hasActiveGroup,
        EFailoverMode mode,
        bool expected)
    {
        var result = MsgViewModel.GetShowActiveFailoverGroupTag(hasActiveGroup, mode);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void PrepareLeastDelayStartup_UsesRunningFailoverTargetBeforeCurrentIndexId()
    {
        var config = new Config
        {
            IndexId = "previous-node",
            FailoverMode = EFailoverMode.Off,
            FailoverEnabled = false,
        };

        MsgViewModel.PrepareLeastDelayStartup(config, "runtime-node");

        Assert.Equal(EFailoverMode.LeastDelay, config.FailoverMode);
        Assert.True(config.FailoverEnabled);
        Assert.Equal("runtime-node", config.FailoverStartupProfileId);
    }

    [Fact]
    public void PrepareLeastDelayStartup_FallsBackToCurrentIndexIdWhenRuntimeTargetMissing()
    {
        var config = new Config
        {
            IndexId = "previous-node",
            FailoverMode = EFailoverMode.Off,
            FailoverEnabled = false,
        };

        MsgViewModel.PrepareLeastDelayStartup(config, null);

        Assert.Equal(EFailoverMode.LeastDelay, config.FailoverMode);
        Assert.True(config.FailoverEnabled);
        Assert.Equal("previous-node", config.FailoverStartupProfileId);
    }

    [Fact]
    public void PrepareFailoverStartup_UsesFirstQueueNodeAsStartupTarget()
    {
        var config = new Config
        {
            FailoverMode = EFailoverMode.Off,
            FailoverEnabled = false,
            FailoverStartupProfileId = "old-target",
        };

        MsgViewModel.PrepareFailoverStartup(config, "first-queue-node");

        Assert.Equal(EFailoverMode.Failover, config.FailoverMode);
        Assert.True(config.FailoverEnabled);
        Assert.Equal("first-queue-node", config.FailoverStartupProfileId);
    }

    [Fact]
    public void PrepareFailoverStartup_ClearsStartupTargetWhenQueueTargetMissing()
    {
        var config = new Config
        {
            FailoverMode = EFailoverMode.Off,
            FailoverEnabled = false,
            FailoverStartupProfileId = "old-target",
        };

        MsgViewModel.PrepareFailoverStartup(config, null);

        Assert.Equal(EFailoverMode.Failover, config.FailoverMode);
        Assert.True(config.FailoverEnabled);
        Assert.Null(config.FailoverStartupProfileId);
    }
}
