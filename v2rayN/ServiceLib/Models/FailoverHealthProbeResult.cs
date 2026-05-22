namespace ServiceLib.Models;

public record FailoverHealthProbeResult(bool IsSuccess, int Delay, string? FailureReason, bool Cancelled = false)
{
    public static FailoverHealthProbeResult Success(int delay)
        => new(true, delay, null, false);

    public static FailoverHealthProbeResult Failure(string reason)
        => new(false, 0, reason, false);

    public static FailoverHealthProbeResult Cancel()
        => new(false, 0, null, true);
}
