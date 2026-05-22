using ServiceLib.Enums;
using ServiceLib.Manager;
using Xunit;

namespace ServiceLib.Tests;

public class LocalUpdatePolicyTests
{
    [Fact]
    public void ShouldDisableAppUpdate_ReturnsTrue_ForLocalBuild()
    {
        Assert.True(LocalUpdatePolicy.ShouldDisableAppUpdate);
    }

    [Theory]
    [InlineData(ECoreType.Xray)]
    [InlineData(ECoreType.mihomo)]
    [InlineData(ECoreType.sing_box)]
    public void IsCoreUpdateDisabled_ReturnsTrue_ForBundledCoreTypes(ECoreType coreType)
    {
        Assert.True(LocalUpdatePolicy.IsCoreUpdateDisabled(coreType));
    }

    [Fact]
    public void ShouldDisableGeoUpdate_ReturnsTrue_ForLocalBuild()
    {
        Assert.True(LocalUpdatePolicy.ShouldDisableGeoUpdate);
    }

    [Fact]
    public void ShouldKeepSubscriptionUpdate_ReturnsTrue_ForUserSubscriptionLinks()
    {
        Assert.True(LocalUpdatePolicy.ShouldKeepSubscriptionUpdate);
    }
}
