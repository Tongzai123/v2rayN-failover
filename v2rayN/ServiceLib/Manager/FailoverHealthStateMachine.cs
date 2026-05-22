namespace ServiceLib.Manager;

public static class FailoverHealthStateMachine
{
    public static bool TryMarkProbeStarting(FailoverGroupItem item, long now, bool force = false)
    {
        item.LastStatus = FailoverHealthStatus.Probing;
        item.LastProbeTime = now;
        return true;
    }

    public static void ApplyProbeResult(FailoverGroupItem item, FailoverHealthProbeResult result, long now)
    {
        if (result.Cancelled)
        {
            return;
        }

        item.LastProbeTime = now;
        if (result.IsSuccess)
        {
            item.LastStatus = FailoverHealthStatus.Normal;
            item.FailureCount = 0;
            item.CooldownUntilTime = null;
            item.LastFailureReason = null;
            item.LastSuccessTime = now;
            item.LastDelay = result.Delay;
            return;
        }

        item.FailureCount++;
        item.LastFailureTime = now;
        item.LastFailureReason = result.FailureReason;
        item.LastStatus = FailoverHealthStatus.Failed;
        item.CooldownUntilTime = null;
    }

    public static long GetCooldownMilliseconds(int failureCount)
    {
        return FailoverHealthTiming.GetCooldownMilliseconds(failureCount);
    }
}
