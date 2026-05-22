using ServiceLib.Manager;
using ServiceLib.Models;
using Xunit;

namespace ServiceLib.Tests;

public class FailoverHealthStateMachineTests
{
    [Fact]
    public void ApplyProbeSuccess_ResetsFailureState()
    {
        var now = new DateTimeOffset(2026, 5, 11, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var item = new FailoverGroupItem
        {
            LastStatus = FailoverHealthStatus.Failed,
            FailureCount = 2,
            CooldownUntilTime = now + 60_000,
            LastFailureReason = "timeout",
        };

        FailoverHealthStateMachine.ApplyProbeResult(item, FailoverHealthProbeResult.Success(123), now);

        Assert.Equal(FailoverHealthStatus.Normal, item.LastStatus);
        Assert.Equal(0, item.FailureCount);
        Assert.Null(item.CooldownUntilTime);
        Assert.Null(item.LastFailureReason);
        Assert.Equal(now, item.LastSuccessTime);
        Assert.Equal(123, item.LastDelay);
    }

    [Fact]
    public void ApplyProbeFailure_FirstFailureEntersFailedWithoutCooldown()
    {
        var now = new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var item = new FailoverGroupItem
        {
            LastStatus = FailoverHealthStatus.Probing,
            FailureCount = 0,
        };

        FailoverHealthStateMachine.ApplyProbeResult(item, FailoverHealthProbeResult.Failure("request-failed"), now);

        Assert.Equal(FailoverHealthStatus.Failed, item.LastStatus);
        Assert.Equal(1, item.FailureCount);
        Assert.Null(item.CooldownUntilTime);
        Assert.Equal(now, item.LastFailureTime);
        Assert.Equal("request-failed", item.LastFailureReason);
    }

    [Fact]
    public void ApplyProbeFailure_SecondFailureEntersFailedWithoutCooldown()
    {
        var now = new DateTimeOffset(2026, 5, 11, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var item = new FailoverGroupItem
        {
            LastStatus = FailoverHealthStatus.Normal,
            FailureCount = 1,
        };

        FailoverHealthStateMachine.ApplyProbeResult(item, FailoverHealthProbeResult.Failure("timeout"), now);

        Assert.Equal(FailoverHealthStatus.Failed, item.LastStatus);
        Assert.Equal(2, item.FailureCount);
        Assert.Null(item.CooldownUntilTime);
        Assert.Equal(now, item.LastFailureTime);
        Assert.Equal("timeout", item.LastFailureReason);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(8)]
    public void GetCooldownMilliseconds_ReturnsZeroBecauseFailuresNoLongerBlockPolling(int failureCount)
    {
        Assert.Equal(0, FailoverHealthStateMachine.GetCooldownMilliseconds(failureCount));
    }

    [Fact]
    public void MarkProbeStarting_FailedAfterCooldownBecomesProbing()
    {
        var now = new DateTimeOffset(2026, 5, 11, 12, 2, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var item = new FailoverGroupItem
        {
            LastStatus = FailoverHealthStatus.Failed,
            FailureCount = 2,
            CooldownUntilTime = now - 1,
        };

        var canProbe = FailoverHealthStateMachine.TryMarkProbeStarting(item, now);

        Assert.True(canProbe);
        Assert.Equal(FailoverHealthStatus.Probing, item.LastStatus);
        Assert.Equal(now, item.LastProbeTime);
    }

    [Fact]
    public void MarkProbeStarting_FailedBeforeCooldownStillBecomesProbing()
    {
        var now = new DateTimeOffset(2026, 5, 11, 12, 2, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var item = new FailoverGroupItem
        {
            LastStatus = FailoverHealthStatus.Failed,
            FailureCount = 2,
            CooldownUntilTime = now + 1,
        };

        var canProbe = FailoverHealthStateMachine.TryMarkProbeStarting(item, now);

        Assert.True(canProbe);
        Assert.Equal(FailoverHealthStatus.Probing, item.LastStatus);
        Assert.Equal(now, item.LastProbeTime);
    }

    [Fact]
    public void MarkProbeStarting_ForceIgnoresCooldown()
    {
        var now = new DateTimeOffset(2026, 5, 11, 12, 2, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var item = new FailoverGroupItem
        {
            LastStatus = FailoverHealthStatus.Failed,
            FailureCount = 2,
            CooldownUntilTime = now + 60_000,
        };

        var canProbe = FailoverHealthStateMachine.TryMarkProbeStarting(item, now, force: true);

        Assert.True(canProbe);
        Assert.Equal(FailoverHealthStatus.Probing, item.LastStatus);
        Assert.Equal(now, item.LastProbeTime);
    }
}
