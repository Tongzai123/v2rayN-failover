using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Models;
using Xunit;

namespace ServiceLib.Tests;

public class FailoverModeConfigTests
{
    [Fact]
    public void NormalizeFailoverMode_MigratesLegacyEnabledToFailover()
    {
        var config = new Config
        {
            FailoverEnabled = true,
            ActiveFailoverGroupId = "group-a",
        };

        ConfigHandler.NormalizeFailoverMode(config);

        Assert.Equal(EFailoverMode.Failover, config.FailoverMode);
        Assert.True(config.FailoverEnabled);
    }

    [Fact]
    public void NormalizeFailoverMode_MigratesLegacyDisabledToOff()
    {
        var config = new Config
        {
            FailoverEnabled = false,
            ActiveFailoverGroupId = "group-a",
        };

        ConfigHandler.NormalizeFailoverMode(config);

        Assert.Equal(EFailoverMode.Off, config.FailoverMode);
        Assert.False(config.FailoverEnabled);
    }

    [Fact]
    public void NormalizeFailoverMode_ClearsModeWhenActiveGroupMissing()
    {
        var config = new Config
        {
            FailoverMode = EFailoverMode.LeastDelay,
            FailoverEnabled = true,
            ActiveFailoverGroupId = "",
        };

        ConfigHandler.NormalizeFailoverMode(config);

        Assert.Equal(EFailoverMode.Off, config.FailoverMode);
        Assert.False(config.FailoverEnabled);
    }

    [Fact]
    public void NormalizeFailoverMode_DisablesExplicitLeastDelay()
    {
        var config = new Config
        {
            FailoverMode = EFailoverMode.LeastDelay,
            FailoverEnabled = false,
            ActiveFailoverGroupId = "group-a",
        };

        ConfigHandler.NormalizeFailoverMode(config);

        Assert.Equal(EFailoverMode.Off, config.FailoverMode);
        Assert.False(config.FailoverEnabled);
    }
}
