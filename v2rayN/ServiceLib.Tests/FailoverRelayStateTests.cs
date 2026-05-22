using ServiceLib.Models;
using ServiceLib.Services.FailoverRelay;
using Xunit;

namespace ServiceLib.Tests;

public class FailoverRelayStateTests
{
    [Fact]
    public void MarkRequestFailure_MarksCandidateFailedAndSkipsNewRequests()
    {
        var p1 = new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", 21001, "P1");
        var p2 = new FailoverRelayCandidate("p2", "failover-p2-in", "proxy-2-p2", 21002, "P2");
        var store = new FailoverCandidateStateStore([p1, p2]);

        store.MarkRequestFailure("p1");

        Assert.Equal(FailoverRelayCandidateState.Failed, store.GetState("p1"));
        Assert.Equal(["p2"], store.GetRequestCandidates().Select(x => x.ProfileId).ToArray());
    }

    [Fact]
    public void MarkHealthRecovered_AllowsCandidateAsRecoveringThenHealthyAfterSuccess()
    {
        var p1 = new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", 21001, "P1");
        var store = new FailoverCandidateStateStore([p1]);

        store.MarkRequestFailure("p1");
        store.MarkHealthRecovered("p1");
        Assert.Equal(FailoverRelayCandidateState.Recovering, store.GetState("p1"));
        Assert.Equal(["p1"], store.GetRequestCandidates().Select(x => x.ProfileId).ToArray());

        store.MarkRequestSuccess("p1");
        Assert.Equal(FailoverRelayCandidateState.Healthy, store.GetState("p1"));
    }

    [Fact]
    public void AllFailed_ReturnsAllInPriorityOrderSoRequestCanFailWithoutDirect()
    {
        var p1 = new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", 21001, "P1");
        var p2 = new FailoverRelayCandidate("p2", "failover-p2-in", "proxy-2-p2", 21002, "P2");
        var store = new FailoverCandidateStateStore([p1, p2]);

        store.MarkRequestFailure("p1");
        store.MarkRequestFailure("p2");

        Assert.Equal(["p1", "p2"], store.GetRequestCandidates().Select(x => x.ProfileId).ToArray());
        Assert.True(store.AllCandidatesFailed);
    }

    [Fact]
    public async Task HealthConfirmation_ExternalFailedCandidateIsSkippedBeforeRequest()
    {
        var candidate = new FailoverRelayCandidate(
            "p1",
            "failover-p1-in",
            "proxy-1-p1",
            21001,
            "P1",
            FailoverHealthStatus.Failed);
        var service = new FailoverRelayHealthConfirmationService(
            new FailoverRelayOptions
            {
                SpeedPingTestUrl = "https://probe.test/generate_204",
                HealthProbeAsync = (_, _, _, _) => Task.FromResult(new FailoverRelayHealthProbeResult(true, "ok", 12)),
            },
            [candidate],
            CancellationToken.None);

        var shouldUse = await service.ShouldUseBeforeRequestAsync(candidate, CancellationToken.None);

        Assert.False(shouldUse);
    }

    [Fact]
    public async Task HealthConfirmation_RecentSuccessUsesCandidateAndRefreshesInBackgroundOnce()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var candidate = new FailoverRelayCandidate(
            "p1",
            "failover-p1-in",
            "proxy-1-p1",
            21001,
            "P1",
            FailoverHealthStatus.Normal,
            now - (long)TimeSpan.FromSeconds(45).TotalMilliseconds);
        var probeCount = 0;
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FailoverRelayHealthConfirmationService(
            new FailoverRelayOptions
            {
                SpeedPingTestUrl = "https://probe.test/generate_204",
                HealthSuccessCacheDuration = TimeSpan.FromMilliseconds(10),
                HealthRecentSuccessDuration = TimeSpan.FromMinutes(5),
                HealthProbeAsync = (_, _, _, _) =>
                {
                    Interlocked.Increment(ref probeCount);
                    probeStarted.TrySetResult();
                    return Task.FromResult(new FailoverRelayHealthProbeResult(true, "ok", 12));
                },
            },
            [candidate],
            CancellationToken.None);

        var shouldUse = await service.ShouldUseBeforeRequestAsync(candidate, CancellationToken.None);
        await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(shouldUse);
        Assert.Equal(1, probeCount);
    }

    [Fact]
    public async Task HealthConfirmation_NoRecentSuccessUsesCandidateWithoutBlockingProbe()
    {
        var candidate = new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", 21001, "P1");
        var probeCount = 0;
        var service = new FailoverRelayHealthConfirmationService(
            new FailoverRelayOptions
            {
                SpeedPingTestUrl = "https://probe.test/generate_204",
                HealthProbeAsync = (_, _, _, _) =>
                {
                    Interlocked.Increment(ref probeCount);
                    return Task.FromResult(new FailoverRelayHealthProbeResult(false, "timeout"));
                },
            },
            [candidate],
            CancellationToken.None);

        var shouldUse = await service.ShouldUseBeforeRequestAsync(candidate, CancellationToken.None);

        Assert.True(shouldUse);
        Assert.Equal(0, probeCount);
    }

    [Fact]
    public async Task HealthConfirmation_ConcurrentRequestsReuseSingleProbe()
    {
        var candidate = new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", 21001, "P1");
        var probeCount = 0;
        var releaseProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FailoverRelayHealthConfirmationService(
            new FailoverRelayOptions
            {
                SpeedPingTestUrl = "https://probe.test/generate_204",
                HealthProbeAsync = async (_, _, _, _) =>
                {
                    Interlocked.Increment(ref probeCount);
                    await releaseProbe.Task;
                    return new FailoverRelayHealthProbeResult(true, "ok", 12);
                },
            },
            [candidate],
            CancellationToken.None);

        var first = service.ConfirmUntilThresholdAsync(candidate, CancellationToken.None);
        var second = service.ConfirmUntilThresholdAsync(candidate, CancellationToken.None);
        releaseProbe.SetResult();

        Assert.True((await first).Success);
        Assert.True((await second).Success);
        Assert.Equal(1, probeCount);
    }

    [Fact]
    public void HealthConfirmation_DefaultProbeUrlsMatchHighConfidenceSet()
    {
        Assert.Equal(
            [
                "https://www.gstatic.com/generate_204",
                "https://connectivitycheck.gstatic.com/generate_204",
                "https://www.google.com/generate_204",
                "https://cp.cloudflare.com/generate_204",
                "http://detectportal.firefox.com/canonical.html",
                "http://www.msftconnecttest.com/connecttest.txt",
                "https://www.wikipedia.org/",
                "https://www.youtube.com/",
            ],
            FailoverRelayOptions.DefaultHealthConfirmationProbeUrls);
        Assert.Equal(
            [200, 204, 301, 302, 304],
            FailoverRelayOptions.DefaultHealthConfirmationSuccessStatusCodes);
    }

    [Fact]
    public async Task HealthConfirmation_MultiUrlWindowSucceedsWhenAnyUrlIsReachable()
    {
        var candidate = new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", 21001, "P1");
        var probeUrls = new[]
        {
            "https://probe.test/1",
            "https://probe.test/2",
            "https://probe.test/3",
            "https://probe.test/4",
            "https://probe.test/5",
            "https://probe.test/6",
            "https://probe.test/7",
            "https://probe.test/8",
        };
        var calledUrls = new List<string>();
        var service = new FailoverRelayHealthConfirmationService(
            new FailoverRelayOptions
            {
                HealthConfirmationProbeUrls = probeUrls,
                HealthProbeAsync = (_, probeUrl, _, _) =>
                {
                    lock (calledUrls)
                    {
                        calledUrls.Add(probeUrl);
                    }

                    return Task.FromResult(probeUrl == "https://probe.test/5"
                        ? new FailoverRelayHealthProbeResult(true, "ok", 23)
                        : new FailoverRelayHealthProbeResult(false, "timeout"));
                },
            },
            [candidate],
            CancellationToken.None);

        var result = await service.ConfirmUntilThresholdAsync(candidate, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.FailureThresholdReached);
        Assert.Equal(0, result.ConsecutiveFailures);
        Assert.Equal(23, result.Delay);
        Assert.Equal(probeUrls.OrderBy(x => x), calledUrls.OrderBy(x => x));
    }

    [Fact]
    public async Task HealthConfirmation_ThresholdUsesSeparateWindowsAfterDelay()
    {
        var candidate = new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", 21001, "P1");
        var calls = new List<DateTimeOffset>();
        var service = new FailoverRelayHealthConfirmationService(
            new FailoverRelayOptions
            {
                HealthConfirmationProbeUrls = ["https://probe.test/a", "https://probe.test/b"],
                HealthConfirmationFailureThreshold = 2,
                HealthConfirmationFailureWindowDelay = TimeSpan.FromMilliseconds(80),
                HealthProbeAsync = (_, _, _, _) =>
                {
                    lock (calls)
                    {
                        calls.Add(DateTimeOffset.UtcNow);
                    }

                    return Task.FromResult(new FailoverRelayHealthProbeResult(false, "timeout"));
                },
            },
            [candidate],
            CancellationToken.None);

        var result = await service.ConfirmUntilThresholdAsync(candidate, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.FailureThresholdReached);
        Assert.Equal(2, result.ConsecutiveFailures);
        Assert.Equal(4, calls.Count);

        var orderedCalls = calls.OrderBy(x => x).ToArray();
        var firstWindowEnd = orderedCalls.Take(2).Max();
        var secondWindowStart = orderedCalls.Skip(2).Min();
        Assert.True(secondWindowStart - firstWindowEnd >= TimeSpan.FromMilliseconds(60));
    }

    [Fact]
    public async Task HealthConfirmation_RequestFailuresUseSeparateWindowsAfterDelay()
    {
        var candidate = new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", 21001, "P1");
        var calls = new List<DateTimeOffset>();
        var service = new FailoverRelayHealthConfirmationService(
            new FailoverRelayOptions
            {
                HealthConfirmationProbeUrls = ["https://probe.test/a"],
                HealthConfirmationFailureThreshold = 2,
                HealthConfirmationFailureWindowDelay = TimeSpan.FromMilliseconds(80),
                HealthProbeAsync = (_, _, _, _) =>
                {
                    lock (calls)
                    {
                        calls.Add(DateTimeOffset.UtcNow);
                    }

                    return Task.FromResult(new FailoverRelayHealthProbeResult(false, "timeout"));
                },
            },
            [candidate],
            CancellationToken.None);

        var first = await service.ConfirmAfterRequestFailureAsync(candidate, CancellationToken.None);
        var second = await service.ConfirmAfterRequestFailureAsync(candidate, CancellationToken.None);

        Assert.False(first.FailureThresholdReached);
        Assert.True(second.FailureThresholdReached);
        Assert.Equal(2, calls.Count);
        Assert.True(calls[1] - calls[0] >= TimeSpan.FromMilliseconds(60));
    }

    [Fact]
    public async Task HealthConfirmation_ConcurrentThresholdWaitersConsumeFailureReportOnce()
    {
        var candidate = new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", 21001, "P1");
        var probeCount = 0;
        var service = new FailoverRelayHealthConfirmationService(
            new FailoverRelayOptions
            {
                HealthConfirmationProbeUrls = ["https://probe.test/a", "https://probe.test/b"],
                HealthConfirmationFailureThreshold = 2,
                HealthConfirmationFailureWindowDelay = TimeSpan.FromMilliseconds(10),
                HealthProbeAsync = async (_, _, _, _) =>
                {
                    Interlocked.Increment(ref probeCount);
                    await Task.Delay(20);
                    return new FailoverRelayHealthProbeResult(false, "timeout");
                },
            },
            [candidate],
            CancellationToken.None);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 30).Select(_ => service.ConfirmUntilThresholdAsync(candidate, CancellationToken.None)));

        Assert.All(results, result => Assert.True(result.FailureThresholdReached));
        Assert.Equal(4, probeCount);
        Assert.True(service.TryConsumeFailureReport(results[0]));
        Assert.All(results.Skip(1), result => Assert.False(service.TryConsumeFailureReport(result)));
    }

    [Fact]
    public async Task HealthConfirmation_FailureReportIsConsumedOnceUntilRecovery()
    {
        var candidate = new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", 21001, "P1");
        var service = new FailoverRelayHealthConfirmationService(
            new FailoverRelayOptions
            {
                HealthConfirmationProbeUrls = ["https://probe.test/a"],
                HealthConfirmationFailureThreshold = 1,
                HealthConfirmationFailureWindowDelay = TimeSpan.Zero,
                HealthProbeAsync = (_, _, _, _) =>
                    Task.FromResult(new FailoverRelayHealthProbeResult(false, "timeout")),
            },
            [candidate],
            CancellationToken.None);

        var first = await service.ConfirmUntilThresholdAsync(candidate, CancellationToken.None);
        var second = await service.ConfirmUntilThresholdAsync(candidate, CancellationToken.None);

        Assert.True(first.FailureThresholdReached);
        Assert.True(second.FailureThresholdReached);
        Assert.True(service.TryConsumeFailureReport(first));
        Assert.False(service.TryConsumeFailureReport(second));

        service.MarkHealthRecovered(candidate.ProfileId);
        var afterRecovery = await service.ConfirmUntilThresholdAsync(candidate, CancellationToken.None);

        Assert.True(afterRecovery.FailureThresholdReached);
        Assert.True(service.TryConsumeFailureReport(afterRecovery));
    }

    [Fact]
    public async Task HealthConfirmation_RequestFailureSecondWindowStartsAfterPreviousWindowEndsPlusDelay()
    {
        var candidate = new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", 21001, "P1");
        var probeCount = 0;
        var firstProbeCompleted = DateTimeOffset.MinValue;
        var secondProbeStarted = DateTimeOffset.MinValue;
        var service = new FailoverRelayHealthConfirmationService(
            new FailoverRelayOptions
            {
                HealthConfirmationProbeUrls = ["https://probe.test/a"],
                HealthConfirmationFailureThreshold = 2,
                HealthConfirmationFailureWindowDelay = TimeSpan.FromMilliseconds(80),
                HealthProbeAsync = async (_, _, _, _) =>
                {
                    var call = Interlocked.Increment(ref probeCount);
                    if (call == 1)
                    {
                        await Task.Delay(120);
                        firstProbeCompleted = DateTimeOffset.UtcNow;
                        return new FailoverRelayHealthProbeResult(false, "timeout");
                    }

                    secondProbeStarted = DateTimeOffset.UtcNow;
                    return new FailoverRelayHealthProbeResult(false, "timeout");
                },
            },
            [candidate],
            CancellationToken.None);

        var first = await service.ConfirmAfterRequestFailureAsync(candidate, CancellationToken.None);
        var second = await service.ConfirmAfterRequestFailureAsync(candidate, CancellationToken.None);

        Assert.False(first.FailureThresholdReached);
        Assert.True(second.FailureThresholdReached);
        Assert.Equal(2, probeCount);
        Assert.True(secondProbeStarted - firstProbeCompleted >= TimeSpan.FromMilliseconds(60));
    }

    [Fact]
    public async Task HealthConfirmation_InFlightFailureDoesNotOverrideLaterRecovery()
    {
        var candidate = new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", 21001, "P1");
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FailoverRelayHealthConfirmationService(
            new FailoverRelayOptions
            {
                HealthConfirmationProbeUrls = ["https://probe.test/a"],
                HealthConfirmationFailureThreshold = 1,
                HealthProbeAsync = async (_, _, _, _) =>
                {
                    probeStarted.TrySetResult();
                    await releaseProbe.Task;
                    return new FailoverRelayHealthProbeResult(false, "timeout");
                },
            },
            [candidate],
            CancellationToken.None);

        var confirmation = service.ConfirmUntilThresholdAsync(candidate, CancellationToken.None);
        await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        service.MarkHealthRecovered(candidate.ProfileId);
        releaseProbe.SetResult();

        var result = await confirmation;

        Assert.True(result.Success);
        Assert.False(result.FailureThresholdReached);
        Assert.False(service.TryConsumeFailureReport(result));
    }

    [Fact]
    public async Task HealthConfirmation_BackgroundConfirmationsAreIsolatedPerCandidate()
    {
        var p1 = new FailoverRelayCandidate("p1", "failover-p1-in", "proxy-1-p1", 21001, "P1");
        var p2 = new FailoverRelayCandidate("p2", "failover-p2-in", "proxy-2-p2", 21002, "P2");
        var calls = new List<string>();
        var service = new FailoverRelayHealthConfirmationService(
            new FailoverRelayOptions
            {
                SpeedPingTestUrl = "https://probe.test/generate_204",
                HealthConfirmationFailureThreshold = 2,
                HealthProbeAsync = (candidate, _, _, _) =>
                {
                    lock (calls)
                    {
                        calls.Add(candidate.ProfileId);
                    }
                    return Task.FromResult(new FailoverRelayHealthProbeResult(false, "timeout"));
                },
            },
            [p1, p2],
            CancellationToken.None);

        var p1ResultTask = service.ConfirmUntilThresholdAsync(p1, CancellationToken.None);
        var p2ResultTask = service.ConfirmUntilThresholdAsync(p2, CancellationToken.None);
        var results = await Task.WhenAll(p1ResultTask, p2ResultTask);

        Assert.All(results, result => Assert.True(result.FailureThresholdReached));
        Assert.Equal(2, results.Single(result => result.ProfileId == "p1").ConsecutiveFailures);
        Assert.Equal(2, results.Single(result => result.ProfileId == "p2").ConsecutiveFailures);
        Assert.Equal(2, calls.Count(profileId => profileId == "p1"));
        Assert.Equal(2, calls.Count(profileId => profileId == "p2"));
    }

    [Fact]
    public async Task HealthConfirmation_MarkHealthRecoveredRefreshesSuccessCache()
    {
        var candidate = new FailoverRelayCandidate(
            "p1",
            "failover-p1-in",
            "proxy-1-p1",
            21001,
            "P1",
            FailoverHealthStatus.Failed);
        var probeCount = 0;
        var service = new FailoverRelayHealthConfirmationService(
            new FailoverRelayOptions
            {
                SpeedPingTestUrl = "https://probe.test/generate_204",
                HealthProbeAsync = (_, _, _, _) =>
                {
                    Interlocked.Increment(ref probeCount);
                    return Task.FromResult(new FailoverRelayHealthProbeResult(false, "timeout"));
                },
            },
            [candidate],
            CancellationToken.None);

        service.MarkHealthRecovered(candidate.ProfileId);
        var shouldUse = await service.ShouldUseBeforeRequestAsync(candidate, CancellationToken.None);

        Assert.True(shouldUse);
        Assert.Equal(0, probeCount);
    }
}
