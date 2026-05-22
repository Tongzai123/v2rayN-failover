namespace ServiceLib.Manager;

public static class FailoverHealthTiming
{
    public static readonly TimeSpan ProbeLoopInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan LeastDelayQuickSwitchAfter = TimeSpan.FromSeconds(7);
    public static readonly TimeSpan ProbeRoundTimeout = TimeSpan.FromSeconds(15);

    public static long GetCooldownMilliseconds(int failureCount)
    {
        return 0;
    }
}
