using System.Reflection;
using System.Net;
using ServiceLib.Enums;
using ServiceLib.Models;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests;

public class RealPingProbeServiceTests
{
    [Fact]
    public void RealPingProbeResult_FactoryMethodsSetExpectedState()
    {
        var success = RealPingProbeResult.Success("p1", 88);
        var failure = RealPingProbeResult.Failure("p2", "request-failed");
        var skipped = RealPingProbeResult.Failure("p3", "test-skipped");
        var cancelled = RealPingProbeResult.Cancel("p4");

        Assert.True(success.IsSuccess);
        Assert.Equal(88, success.Delay);
        Assert.Null(success.FailureReason);
        Assert.False(success.Cancelled);

        Assert.False(failure.IsSuccess);
        Assert.Equal(0, failure.Delay);
        Assert.Equal("request-failed", failure.FailureReason);

        Assert.False(skipped.IsSuccess);
        Assert.Equal("test-skipped", skipped.FailureReason);

        Assert.True(cancelled.Cancelled);
        Assert.False(cancelled.IsSuccess);
        Assert.Null(cancelled.FailureReason);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(100, 100)]
    public void GetInitialPageSize_NeverExceedsItemCount(int count, int expectedUpperBound)
    {
        var method = typeof(RealPingProbeService).GetMethod(
            "GetInitialPageSize",
            BindingFlags.NonPublic | BindingFlags.Static);

        var result = Assert.IsType<int>(method?.Invoke(null, [count]));

        Assert.True(result >= 0);
        Assert.True(result <= expectedUpperBound);
        if (count > 0)
        {
            Assert.True(result > 0);
        }
    }

    [Theory]
    [InlineData("test-skipped", true)]
    [InlineData("core-start-failed", true)]
    [InlineData("request-failed", true)]
    [InlineData("probe-failed", true)]
    [InlineData("core-unsupported", false)]
    public void IsRetryableFailure_ClassifiesReasons(string reason, bool expected)
    {
        var method = typeof(RealPingProbeService).GetMethod(
            "IsRetryableFailure",
            BindingFlags.NonPublic | BindingFlags.Static);

        var result = Assert.IsType<bool>(method?.Invoke(null, [RealPingProbeResult.Failure("p1", reason)]));

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task ProbeAsync_WhenBatchSkipsItem_RetriesSingleItemAndReturnsSuccess()
    {
        var item = new ServerTestItem
        {
            IndexId = "p1",
            Address = "198.51.100.10",
            Port = 443,
            ConfigType = EConfigType.VLESS,
            CoreType = ECoreType.Xray,
            Profile = new ProfileItem { IndexId = "p1", ConfigType = EConfigType.VLESS, Address = "198.51.100.10", Port = 443 },
        };
        var batchCalls = 0;
        var singleCalls = 0;
        var progress = new List<RealPingProbeResult>();
        var service = new RealPingProbeService(
            new Config(),
            (items, suppressFailover) =>
            {
                batchCalls++;
                Assert.True(suppressFailover);
                return Task.FromResult<IAsyncDisposable?>(new NoopAsyncDisposable());
            },
            (singleItem, suppressFailover) =>
            {
                singleCalls++;
                Assert.True(suppressFailover);
                singleItem.AllowTest = true;
                singleItem.Port = 39001;
                return Task.FromResult<IAsyncDisposable?>(new NoopAsyncDisposable());
            },
            (_, _, _, _) => Task.FromResult(88));

        var results = await service.ProbeAsync(
            [item],
            suppressFailover: true,
            CancellationToken.None,
            result =>
            {
                progress.Add(result);
                return Task.CompletedTask;
            });

        var result = Assert.Single(results);
        Assert.Equal("p1", result.ProfileId);
        Assert.True(result.IsSuccess);
        Assert.Equal(88, result.Delay);
        Assert.Equal(1, batchCalls);
        Assert.Equal(1, singleCalls);
        var progressResult = Assert.Single(progress);
        Assert.True(progressResult.IsSuccess);
        Assert.Equal(88, progressResult.Delay);
    }

    [Fact]
    public async Task ProbeAsync_PublishesSuccessfulProgressBeforeSlowPeerFinishes()
    {
        var fast = CreateTestItem("fast", 39001);
        var slow = CreateTestItem("slow", 39002);
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new List<RealPingProbeResult>();
        var service = new RealPingProbeService(
            new Config(),
            (items, _) =>
            {
                foreach (var item in items)
                {
                    item.AllowTest = true;
                }
                return Task.FromResult<IAsyncDisposable?>(new NoopAsyncDisposable());
            },
            (item, _) =>
            {
                item.AllowTest = true;
                return Task.FromResult<IAsyncDisposable?>(new NoopAsyncDisposable());
            },
            async (_, proxy, _, cancellationToken) =>
            {
                var port = ((WebProxy)proxy).Address?.Port;
                if (port == fast.Port)
                {
                    return 21;
                }

                slowStarted.SetResult();
                await releaseSlow.Task.WaitAsync(cancellationToken);
                return 120;
            });

        var probeTask = service.ProbeAsync(
            [fast, slow],
            suppressFailover: true,
            CancellationToken.None,
            result =>
            {
                progress.Add(result);
                return Task.CompletedTask;
            });

        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        Assert.Contains(progress, result => result.ProfileId == fast.IndexId && result.IsSuccess && result.Delay == 21);

        releaseSlow.SetResult();
        await probeTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static ServerTestItem CreateTestItem(string indexId, int port)
    {
        return new ServerTestItem
        {
            IndexId = indexId,
            Address = "198.51.100.10",
            Port = port,
            ConfigType = EConfigType.VLESS,
            CoreType = ECoreType.Xray,
            Profile = new ProfileItem { IndexId = indexId, ConfigType = EConfigType.VLESS, Address = "198.51.100.10", Port = port },
        };
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;
    }
}
