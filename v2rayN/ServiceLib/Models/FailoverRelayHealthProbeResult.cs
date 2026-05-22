namespace ServiceLib.Models;

public sealed record FailoverRelayHealthProbeResult(
    bool Success,
    string Reason,
    int? Delay = null);

public sealed record FailoverRelayHealthConfirmationResult(
    string ProfileId,
    bool Success,
    string Reason,
    int? Delay,
    long CheckedAt,
    int ConsecutiveFailures,
    bool FailureThresholdReached);
