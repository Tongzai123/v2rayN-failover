using System.Reflection;
using ServiceLib.Enums;
using ServiceLib.Manager;
using ServiceLib.Models;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests;

public class CoreFailoverHealthProbeTests
{
    [Fact]
    public async Task ProbeBatchAsync_UnsupportedCoreReturnsFailureWithoutStartingCore()
    {
        var probe = new CoreFailoverHealthProbe(new Config());
        var profile = CreateUnsupportedProfile();

        var results = await probe.ProbeBatchAsync([profile], CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(profile.IndexId, result.ProfileId);
        Assert.False(result.Result.IsSuccess);
        Assert.False(result.Result.Cancelled);
        Assert.Equal("core-unsupported", result.Result.FailureReason);
    }

    [Fact]
    public async Task ProbeAsync_UsesBatchResultForSingleProfile()
    {
        var probe = new CoreFailoverHealthProbe(new Config());
        var profile = CreateUnsupportedProfile();

        var result = await probe.ProbeAsync(profile, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.False(result.Cancelled);
        Assert.Equal("core-unsupported", result.FailureReason);
    }

    [Fact]
    public async Task ProbeBatchAsync_PreCancelledTokenReturnsCancelBeforeUnsupportedCore()
    {
        var probe = new CoreFailoverHealthProbe(new Config());
        var profile = CreateUnsupportedProfile();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var results = await probe.ProbeBatchAsync([profile], cts.Token);

        var result = Assert.Single(results);
        Assert.Equal(profile.IndexId, result.ProfileId);
        Assert.False(result.Result.IsSuccess);
        Assert.True(result.Result.Cancelled);
        Assert.Null(result.Result.FailureReason);
    }

    [Fact]
    public void CreateServerTestItem_DoesNotPreAllowTest()
    {
        var profile = new ProfileItem
        {
            IndexId = $"supported-{Guid.NewGuid():N}",
            ConfigType = EConfigType.SOCKS,
            CoreType = ECoreType.Xray,
            Address = "198.51.100.30",
            Port = 443,
        };
        var method = typeof(CoreFailoverHealthProbe).GetMethod(
            "CreateServerTestItem",
            BindingFlags.NonPublic | BindingFlags.Static);

        var item = Assert.IsType<ServerTestItem>(method?.Invoke(null, [profile, 3, ECoreType.Xray]));

        Assert.False(item.AllowTest);
        Assert.Equal(3, item.QueueNum);
        Assert.Equal(ECoreType.Xray, item.CoreType);
        Assert.Same(profile, item.Profile);
    }

    [Fact]
    public void ToFailoverHealthProbeResult_MapsRealPingResults()
    {
        var method = typeof(CoreFailoverHealthProbe).GetMethod(
            "ToFailoverHealthProbeResult",
            BindingFlags.NonPublic | BindingFlags.Static);

        var success = Assert.IsType<FailoverHealthProbeResult>(
            method?.Invoke(null, [RealPingProbeResult.Success("p1", 88)]));
        var failed = Assert.IsType<FailoverHealthProbeResult>(
            method?.Invoke(null, [RealPingProbeResult.Failure("p1", "request-failed")]));
        var cancelled = Assert.IsType<FailoverHealthProbeResult>(
            method?.Invoke(null, [RealPingProbeResult.Cancel("p1")]));

        Assert.True(success.IsSuccess);
        Assert.Equal(88, success.Delay);
        Assert.False(failed.IsSuccess);
        Assert.Equal("request-failed", failed.FailureReason);
        Assert.True(cancelled.Cancelled);
    }

    private static ProfileItem CreateUnsupportedProfile()
    {
        return new ProfileItem
        {
            IndexId = $"unsupported-{Guid.NewGuid():N}",
            ConfigType = EConfigType.SOCKS,
            CoreType = ECoreType.mihomo,
            Address = "198.51.100.30",
            Port = 443,
        };
    }
}
