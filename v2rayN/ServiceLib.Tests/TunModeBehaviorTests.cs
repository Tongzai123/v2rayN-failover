using ServiceLib.Enums;
using ServiceLib.Handler;
using Xunit;

namespace ServiceLib.Tests;

public class TunModeBehaviorTests
{
    [Fact]
    public void GetNotifyIconIndex_UsesPacIcon_WhenTunIsEnabled()
    {
        var index = TunModeBehavior.GetNotifyIconIndex(ESysProxyType.ForcedClear, true);

        Assert.Equal(3, index);
    }

    [Fact]
    public void NormalizeProxyForTun_ReturnsForcedClear_WhenTunEnabledWithForcedChange()
    {
        var type = TunModeBehavior.NormalizeProxyForTun(ESysProxyType.ForcedChange, true);

        Assert.Equal(ESysProxyType.ForcedClear, type);
    }

    [Fact]
    public void ShouldDisableTunForSystemProxy_ReturnsTrue_WhenSystemProxyForcedChange()
    {
        var shouldDisableTun = TunModeBehavior.ShouldDisableTunForSystemProxy(ESysProxyType.ForcedChange);

        Assert.True(shouldDisableTun);
    }
}
