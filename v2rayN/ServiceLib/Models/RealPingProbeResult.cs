namespace ServiceLib.Models;

public record RealPingProbeResult(string ProfileId, bool IsSuccess, int Delay, string? FailureReason, bool Cancelled = false)
{
    public static RealPingProbeResult Success(string profileId, int delay)
        => new(profileId, true, delay, null, false);

    public static RealPingProbeResult Failure(string profileId, string reason)
        => new(profileId, false, 0, reason, false);

    public static RealPingProbeResult Cancel(string profileId)
        => new(profileId, false, 0, null, true);
}
